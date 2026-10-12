namespace VMentory.Core.Auth.Ldap;

using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

/// LDAP v3 result codes we act on by name. Everything else is reported by number.
internal static class LdapResult
{
    public const int Success                   = 0;
    public const int OperationsError           = 1;
    public const int ProtocolError             = 2;
    public const int SizeLimitExceeded         = 4;
    public const int InvalidCredentials        = 49;
    public const int InsufficientAccessRights  = 50;
    public const int Busy                      = 51;
    public const int Unavailable               = 52;
    public const int UnwillingToPerform        = 53;
    public const int Referral                  = 10;
}

internal enum LdapScope { Base = 0, SingleLevel = 1, Subtree = 2 }

internal sealed record LdapEntry(string Dn, Dictionary<string, List<string>> Attributes)
{
    /// First value of an attribute, case-insensitively by name, or null.
    public string? First(string attribute) =>
        Attributes.TryGetValue(attribute, out var vals) && vals.Count > 0 ? vals[0] : null;

    public List<string> All(string attribute) =>
        Attributes.TryGetValue(attribute, out var vals) ? vals : new List<string>();
}

/// Thrown when the directory answers with a non-success result code. Carries the code so the caller
/// can tell "wrong password" (49) from "the directory is unwell" (51/52/1) — a distinction that
/// matters more than any other in this file: reporting a dead DC as bad credentials sends an
/// operator hunting a password problem that does not exist.
internal sealed class LdapResultException : Exception
{
    public int ResultCode { get; }
    public string DiagnosticMessage { get; }

    public LdapResultException(string operation, int resultCode, string diagnosticMessage)
        : base($"{operation} failed: LDAP result {resultCode}" +
               (string.IsNullOrWhiteSpace(diagnosticMessage) ? "" : $" — {diagnosticMessage}"))
    {
        ResultCode        = resultCode;
        DiagnosticMessage = diagnosticMessage ?? "";
    }
}

/// A single-connection LDAP v3 client: connect (optionally over TLS), simple bind, search, unbind.
/// Not thread-safe and not pooled — one instance serves one sign-in attempt and is then disposed.
internal sealed class LdapClient : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly Stream _stream;
    private int _nextMessageId = 1;

    private LdapClient(TcpClient tcp, Stream stream)
    {
        _tcp    = tcp;
        _stream = stream;
    }

    /// Connects, and when <paramref name="useTls"/> is set completes the TLS handshake before
    /// returning, so no LDAP byte ever leaves the process in the clear.
    public static async Task<LdapClient> ConnectAsync(
        string host,
        int port,
        bool useTls,
        string tlsTargetName,
        RemoteCertificateValidationCallback? certificateValidation,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var tcp = new TcpClient();
        Stream? stream = null;
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(timeout);
            await tcp.ConnectAsync(host, port, connectCts.Token).ConfigureAwait(false);

            tcp.NoDelay = true;
            stream = tcp.GetStream();

            if (useTls)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false, certificateValidation);
                stream = ssl;

                using var tlsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                tlsCts.CancelAfter(timeout);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost            = tlsTargetName,
                    EnabledSslProtocols   = SslProtocols.Tls12 | SslProtocols.Tls13,
                    // The callback passed to the SslStream constructor owns the trust decision; it
                    // pins the directory's issuing CA rather than deferring to the system store.
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                }, tlsCts.Token).ConfigureAwait(false);
            }

            return new LdapClient(tcp, stream);
        }
        catch
        {
            stream?.Dispose();
            tcp.Dispose();
            throw;
        }
    }

    /// LDAP simple bind. A success here is the whole password check.
    ///
    /// ⚠️ The caller MUST reject an empty password before calling this. A simple bind with a DN and
    /// an empty password is an *unauthenticated* bind (RFC 4513 §5.1.2) and the directory answers
    /// success — the classic LDAP authentication bypass. <see cref="DirectoryAuthenticator"/> guards
    /// it, and this method guards it again rather than trusting that.
    public async Task BindSimpleAsync(string dn, string password, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException(
                "A simple bind with an empty password is an anonymous bind and would succeed. Refusing.",
                nameof(password));

        var messageId = _nextMessageId++;
        var request = Ber.Seq(
            Ber.Int(messageId),
            Ber.Tlv(0x60,                                  // [APPLICATION 0] bindRequest
                Ber.Int(3),                                // version
                Ber.Str(dn),                               // name
                Ber.Str(password, 0x80)));                 // [0] simple

        await WriteAsync(request, ct).ConfigureAwait(false);

        var (responseId, op) = await ReadMessageAsync(ct).ConfigureAwait(false);
        if (responseId != messageId)
            throw new LdapProtocolException($"bind response carried message id {responseId}, expected {messageId}");
        if (op.Tag != 0x61)
            throw new LdapProtocolException($"expected a bindResponse (0x61), got tag 0x{op.Tag:X2}");

        var (code, diagnostic) = ReadResult(op, "bindResponse");
        if (code != LdapResult.Success) throw new LdapResultException("Bind", code, diagnostic);
    }

    public async Task<List<LdapEntry>> SearchAsync(
        string baseDn,
        LdapScope scope,
        byte[] filter,
        IReadOnlyList<string> attributes,
        int sizeLimit,
        int timeLimitSeconds,
        CancellationToken ct)
    {
        var messageId = _nextMessageId++;
        var attrParts = attributes.Select(a => Ber.Str(a)).ToArray();

        var request = Ber.Seq(
            Ber.Int(messageId),
            Ber.Tlv(0x63,                                  // [APPLICATION 3] searchRequest
                Ber.Str(baseDn),
                Ber.Enumerated((int)scope),
                Ber.Enumerated(0),                         // derefAliases: neverDerefAliases
                Ber.Int(sizeLimit),
                Ber.Int(timeLimitSeconds),
                Ber.Bool(false),                           // typesOnly
                filter,
                Ber.Seq(attrParts)));

        await WriteAsync(request, ct).ConfigureAwait(false);

        var entries = new List<LdapEntry>();
        while (true)
        {
            var (responseId, op) = await ReadMessageAsync(ct).ConfigureAwait(false);
            if (responseId != messageId)
                throw new LdapProtocolException($"search response carried message id {responseId}, expected {messageId}");

            switch (op.Tag)
            {
                case 0x64:                                 // searchResEntry
                    entries.Add(ParseEntry(op));
                    break;

                case 0x73:                                 // searchResRef — AD emits these at the
                    break;                                 // domain root for other partitions; ignore.

                case 0x65:                                 // searchResDone
                {
                    var (code, diagnostic) = ReadResult(op, "searchResDone");
                    // sizeLimitExceeded means "here are the first N" — fine for membership checks,
                    // where one matching entry is already the answer.
                    if (code is LdapResult.Success or LdapResult.SizeLimitExceeded or LdapResult.Referral)
                        return entries;
                    throw new LdapResultException("Search", code, diagnostic);
                }

                default:
                    throw new LdapProtocolException($"unexpected search response tag 0x{op.Tag:X2}");
            }
        }
    }

    private static LdapEntry ParseEntry(BerElement op)
    {
        var kids = op.Children();
        if (kids.Count < 2) throw new LdapProtocolException("searchResEntry is missing its attribute list");

        var dn    = kids[0].AsString();
        var attrs = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var attr in kids[1].Children())
        {
            var parts = attr.Children();
            if (parts.Count < 2) continue;
            var name = parts[0].AsString();
            var vals = parts[1].Children().Select(v => v.AsString()).ToList();
            attrs[name] = vals;
        }

        return new LdapEntry(dn, attrs);
    }

    private static (int code, string diagnostic) ReadResult(BerElement op, string what)
    {
        var kids = op.Children();
        if (kids.Count < 3) throw new LdapProtocolException($"{what} is missing its result fields");
        return ((int)kids[0].AsInt(), kids[2].AsString());
    }

    private async Task WriteAsync(byte[] message, CancellationToken ct)
    {
        await _stream.WriteAsync(message, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// Reads exactly one LDAPMessage and returns its id plus the protocol-op element.
    private async Task<(long messageId, BerElement op)> ReadMessageAsync(CancellationToken ct)
    {
        var header = new byte[2];
        await ReadExactAsync(header, 2, ct).ConfigureAwait(false);

        if (header[0] != Ber.TagSequence)
            throw new LdapProtocolException($"expected an LDAPMessage SEQUENCE (0x30), got tag 0x{header[0]:X2}");

        int length = header[1];
        if ((length & 0x80) != 0)
        {
            var n = length & 0x7F;
            if (n is 0 or > 4) throw new LdapProtocolException($"LDAPMessage length of {n} octets is out of range");

            var ext = new byte[n];
            await ReadExactAsync(ext, n, ct).ConfigureAwait(false);

            length = 0;
            foreach (var b in ext) length = (length << 8) | b;
        }

        if (length is < 0 or > BerParse.MaxElementBytes)
            throw new LdapProtocolException($"LDAPMessage length {length} is out of range");

        var body = new byte[length];
        await ReadExactAsync(body, length, ct).ConfigureAwait(false);

        var off = 0;
        var id  = BerParse.Read(body, ref off, length);
        if (id.Tag != Ber.TagInteger)
            throw new LdapProtocolException("LDAPMessage does not start with a messageID");

        var op = BerParse.Read(body, ref off, length);
        return (id.AsInt(), op);
    }

    private async Task ReadExactAsync(byte[] buffer, int count, CancellationToken ct)
    {
        var read = 0;
        while (read < count)
        {
            var n = await _stream.ReadAsync(buffer.AsMemory(read, count - read), ct).ConfigureAwait(false);
            if (n == 0) throw new LdapProtocolException("the directory closed the connection mid-message");
            read += n;
        }
    }

    public void Dispose()
    {
        // An unbindRequest is courtesy, not a requirement, and a dead socket must not throw out of
        // Dispose and mask the real failure.
        try
        {
            var bye = Ber.Seq(Ber.Int(_nextMessageId++), Ber.Tlv(0x42));   // [APPLICATION 2] unbindRequest
            _stream.Write(bye, 0, bye.Length);
            _stream.Flush();
        }
        catch { /* ignored */ }

        try { _stream.Dispose(); } catch { /* ignored */ }
        try { _tcp.Dispose(); }    catch { /* ignored */ }
    }
}

/// LDAP search filters, built as BER rather than rendered to the RFC 4515 string form and parsed by
/// the server.
///
/// ⭐ This is why filter injection cannot happen here, and why there is deliberately no escaping
/// function: a filter's structure lives entirely in its BER tags, and an assertion value is a raw
/// octet string that the server never re-parses. A username of `*)(objectClass=*` is matched as that
/// literal string. Applying RFC 4515 escaping would be the actual bug — `\` and `(` are ordinary
/// characters inside a distinguished name, so escaping a user's DN before a membership query turns a
/// correct query into one that matches nothing.
internal static class LdapFilter
{
    /// The AD extensible matching rule LDAP_MATCHING_RULE_IN_CHAIN: evaluates group membership
    /// transitively, so a user in a group nested inside a role group still matches.
    public const string MatchingRuleInChain = "1.2.840.113556.1.4.1941";

    /// LDAP_MATCHING_RULE_BIT_AND — tests flag bits in an integer attribute server-side.
    public const string MatchingRuleBitAnd = "1.2.840.113556.1.4.803";

    public static byte[] Equal(string attribute, string value) =>
        Ber.Tlv(0xA3, Ber.Str(attribute), Ber.Str(value));               // [3] equalityMatch

    public static byte[] Present(string attribute) =>
        Ber.Str(attribute, 0x87);                                        // [7] present

    public static byte[] And(params byte[][] filters) => Ber.Tlv(0xA0, filters);
    public static byte[] Or(params byte[][] filters)  => Ber.Tlv(0xA1, filters);
    public static byte[] Not(byte[] filter)           => Ber.Tlv(0xA2, filter);

    /// extensibleMatch — the BER equivalent of `attribute:rule:=value`.
    public static byte[] ExtensibleMatch(string attribute, string matchingRule, string value) =>
        Ber.Tlv(0xA9,
            Ber.Str(matchingRule, 0x81),                                 // [1] matchingRule
            Ber.Str(attribute,    0x82),                                 // [2] type
            Ber.Str(value,        0x83));                                // [3] matchValue

    /// True when any bit of <paramref name="mask"/> is set in the attribute.
    public static byte[] BitAnd(string attribute, long mask) =>
        ExtensibleMatch(attribute, MatchingRuleBitAnd, mask.ToString());
}
