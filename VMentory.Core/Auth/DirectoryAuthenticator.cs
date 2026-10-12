namespace VMentory.Core.Auth;

using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using VMentory.Core.Auth.Ldap;

public enum DirectoryAuthOutcome
{
    /// Authenticated, and a role was resolved.
    Success,
    /// The directory rejected the username or password (LDAP 49).
    BadCredentials,
    /// Authenticated, but the account is in none of the role groups and there is no default role.
    NoRole,
    /// Authenticated, but the account cannot be used — disabled, or its directory entry is
    /// unreadable or ambiguous.
    AccountUnusable,
    /// No configured directory server could be reached or completed the exchange. ⭐ Deliberately
    /// NOT folded into BadCredentials: telling an operator "wrong password" when a DC is down sends
    /// them hunting a problem that does not exist.
    DirectoryUnavailable,
    /// Directory sign-in is not configured, or its configuration is invalid.
    Disabled,
}

public sealed record DirectoryAuthResult(
    DirectoryAuthOutcome Outcome,
    string Message,
    string? Detail = null,
    string? Upn = null,
    string? SamAccountName = null,
    string? DisplayName = null,
    string? Mail = null,
    AppRole? Role = null,
    IReadOnlyList<string>? MatchedGroups = null)
{
    public bool Succeeded => Outcome == DirectoryAuthOutcome.Success;
}

/// Validates a username and password against Active Directory over LDAPS, and derives the console
/// role from the account's group membership (ENG-0017).
///
/// The password check IS the LDAP simple bind: the directory is the only thing that ever sees the
/// password, nothing is cached, and no password is ever stored by VMentory for a directory account.
public sealed class DirectoryAuthenticator
{
    // Attributes read from the user's own entry.
    private const string AttrDn          = "distinguishedName";
    private const string AttrSam         = "sAMAccountName";
    private const string AttrUpn         = "userPrincipalName";
    private const string AttrDisplayName = "displayName";
    private const string AttrMail        = "mail";
    private const string AttrUac         = "userAccountControl";

    private const int UacAccountDisabled = 0x0002;

    /// Highest first. Which of several matched groups wins has to be fixed and documented, because
    /// a person legitimately sits in more than one.
    private static readonly AppRole[] RolePrecedence =
        { AppRole.Admin, AppRole.VmOperator, AppRole.BackupOperator, AppRole.Viewer };

    private readonly DirectoryOptions _options;
    private readonly Action<string>? _log;

    public DirectoryAuthenticator(DirectoryOptions options, Action<string>? log = null)
    {
        _options = options;
        _log     = log;
    }

    public async Task<DirectoryAuthResult> AuthenticateAsync(string username, string password, CancellationToken ct = default)
    {
        if (!_options.Enabled)
            return new DirectoryAuthResult(DirectoryAuthOutcome.Disabled,
                "Windows domain sign-in is not configured on this deployment.");

        // ⚠️ Load-bearing guard. An LDAP simple bind with a DN and an empty password is an
        // *unauthenticated* bind and the directory answers success — so a blank password would
        // otherwise authenticate anyone whose username exists. See RFC 4513 §5.1.2.
        if (string.IsNullOrWhiteSpace(password))
            return new DirectoryAuthResult(DirectoryAuthOutcome.BadCredentials,
                "Enter your domain password.",
                "empty password refused before binding (an empty simple bind is an anonymous bind)");

        if (string.IsNullOrWhiteSpace(username))
            return new DirectoryAuthResult(DirectoryAuthOutcome.BadCredentials, "Enter your domain username.");

        var (sam, upn) = NormaliseUsername(username.Trim(), _options.Domain);

        var transportFailures = new List<string>();

        foreach (var server in _options.Servers)
        {
            LdapClient? client = null;
            try
            {
                client = await LdapClient.ConnectAsync(
                    server.Host,
                    server.Port,
                    useTls: _options.Transport == DirectoryTransport.Ldaps,
                    tlsTargetName: server.CertificateName,
                    certificateValidation: BuildCertificateValidation(server),
                    timeout: _options.Timeout,
                    ct).ConfigureAwait(false);

                try
                {
                    await client.BindSimpleAsync(upn, password, ct).ConfigureAwait(false);
                }
                catch (LdapResultException ex) when (ex.ResultCode == LdapResult.InvalidCredentials)
                {
                    // Stop here rather than replaying the same wrong password at the next DC — that
                    // only doubles the hit on the account's lockout counter.
                    return new DirectoryAuthResult(DirectoryAuthOutcome.BadCredentials,
                        "That username or password was not accepted by the domain.",
                        $"{server.Host}: LDAP 49 — {ex.DiagnosticMessage}");
                }

                return await ResolveAccountAsync(client, server, sam, upn, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Connect failure, TLS failure, timeout, protocol error, or a result code that says
                // the directory is unwell — all of them mean "try the next DC".
                var detail = $"{server.Host}:{server.Port} — {ex.GetType().Name}: {ex.Message}";
                transportFailures.Add(detail);
                _log?.Invoke($"directory sign-in: {detail}");
            }
            finally
            {
                client?.Dispose();
            }
        }

        return new DirectoryAuthResult(DirectoryAuthOutcome.DirectoryUnavailable,
            "The domain could not be reached to check your sign-in. This is not a password problem — "
          + "tell whoever runs the console.",
            string.Join(" | ", transportFailures));
    }

    /// Everything after a successful bind: read the account, then resolve its role from groups.
    private async Task<DirectoryAuthResult> ResolveAccountAsync(
        LdapClient client, DirectoryServer server, string sam, string upn, CancellationToken ct)
    {
        var baseDn = _options.BaseDn ?? await ReadDefaultNamingContextAsync(client, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(baseDn))
            return new DirectoryAuthResult(DirectoryAuthOutcome.AccountUnusable,
                "Signed in, but the directory did not say where to look up accounts.",
                $"{server.Host}: rootDSE carried no defaultNamingContext and VMENTORY_AD_BASE_DN is unset");

        var userFilter = LdapFilter.And(
            LdapFilter.Equal("objectClass", "user"),
            LdapFilter.Not(LdapFilter.Equal("objectClass", "computer")),
            LdapFilter.Or(
                LdapFilter.Equal(AttrUpn, upn),
                LdapFilter.Equal(AttrSam, sam)));

        var entries = await client.SearchAsync(
            baseDn, LdapScope.Subtree, userFilter,
            new[] { AttrDn, AttrSam, AttrUpn, AttrDisplayName, AttrMail, AttrUac },
            sizeLimit: 2, timeLimitSeconds: (int)_options.Timeout.TotalSeconds, ct).ConfigureAwait(false);

        if (entries.Count == 0)
            return new DirectoryAuthResult(DirectoryAuthOutcome.AccountUnusable,
                "Your password was accepted but your account could not be read from the directory.",
                $"{server.Host}: no entry matched userPrincipalName={upn} or sAMAccountName={sam} under {baseDn}");

        if (entries.Count > 1)
            return new DirectoryAuthResult(DirectoryAuthOutcome.AccountUnusable,
                "That username matches more than one directory account. Sign in with your full "
              + "user@domain name.",
                $"{server.Host}: {entries.Count} entries matched {sam}/{upn}");

        var entry    = entries[0];
        var userDn   = entry.First(AttrDn) ?? entry.Dn;
        var realSam  = entry.First(AttrSam) ?? sam;
        var realUpn  = entry.First(AttrUpn) ?? upn;
        var display  = entry.First(AttrDisplayName);
        var mail     = entry.First(AttrMail);

        // A disabled account cannot bind, so this is belt and braces — but it costs one integer
        // parse and closes the gap if a directory is ever configured to allow it.
        if (int.TryParse(entry.First(AttrUac), out var uac) && (uac & UacAccountDisabled) != 0)
            return new DirectoryAuthResult(DirectoryAuthOutcome.AccountUnusable,
                "That domain account is disabled.",
                $"{server.Host}: userAccountControl={uac}");

        var (role, matched) = await ResolveRoleAsync(client, baseDn, userDn, ct).ConfigureAwait(false);

        if (role == null)
            return new DirectoryAuthResult(DirectoryAuthOutcome.NoRole,
                "Your domain sign-in worked, but your account has not been granted access to "
              + "VMentory. Ask for membership of the relevant domain group.",
                $"{server.Host}: {realSam} is in none of "
              + $"[{string.Join(", ", _options.RoleGroups.Values)}] and no default role is configured",
                Upn: realUpn, SamAccountName: realSam, DisplayName: display, Mail: mail,
                MatchedGroups: matched);

        return new DirectoryAuthResult(DirectoryAuthOutcome.Success,
            "Signed in.",
            matched.Count > 0
                ? $"{server.Host}: role {role} from group(s) {string.Join(", ", matched)}"
                : $"{server.Host}: role {role} from VMENTORY_AD_DEFAULT_ROLE",
            Upn: realUpn, SamAccountName: realSam, DisplayName: display, Mail: mail,
            Role: role, MatchedGroups: matched);
    }

    /// One search answers every group question: which of the configured role groups contain this
    /// user, evaluated transitively so nested groups count.
    private async Task<(AppRole? role, IReadOnlyList<string> matched)> ResolveRoleAsync(
        LdapClient client, string baseDn, string userDn, CancellationToken ct)
    {
        if (_options.RoleGroups.Count == 0)
            return (_options.DefaultRole, Array.Empty<string>());

        var nameFilters = _options.RoleGroups.Values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(g => LdapFilter.Equal(AttrSam, g))
            .ToArray();

        var groupFilter = LdapFilter.And(
            LdapFilter.Equal("objectClass", "group"),
            LdapFilter.ExtensibleMatch("member", LdapFilter.MatchingRuleInChain, userDn),
            nameFilters.Length == 1 ? nameFilters[0] : LdapFilter.Or(nameFilters));

        var groups = await client.SearchAsync(
            baseDn, LdapScope.Subtree, groupFilter, new[] { AttrSam },
            sizeLimit: nameFilters.Length, timeLimitSeconds: (int)_options.Timeout.TotalSeconds, ct)
            .ConfigureAwait(false);

        var matchedNames = groups
            .Select(g => g.First(AttrSam))
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToList();

        foreach (var role in RolePrecedence)
        {
            if (!_options.RoleGroups.TryGetValue(role, out var groupName)) continue;
            if (matchedNames.Any(n => string.Equals(n, groupName, StringComparison.OrdinalIgnoreCase)))
                return (role, matchedNames);
        }

        return (_options.DefaultRole, matchedNames);
    }

    private static async Task<string?> ReadDefaultNamingContextAsync(LdapClient client, CancellationToken ct)
    {
        var rootDse = await client.SearchAsync(
            "", LdapScope.Base, LdapFilter.Present("objectClass"),
            new[] { "defaultNamingContext" }, sizeLimit: 1, timeLimitSeconds: 10, ct).ConfigureAwait(false);

        return rootDse.FirstOrDefault()?.First("defaultNamingContext");
    }

    /// Accepts `user`, `DOMAIN\user` and `user@domain`, and returns both forms we need: the
    /// sAMAccountName for the lookup filter and a UPN to bind with.
    ///
    /// The bind uses the UPN because it is unambiguous across a forest and needs no NetBIOS name —
    /// verified against i60.local, where `armandtd@i60.local` binds and resolves to `I60\ArmandtD`.
    internal static (string sam, string upn) NormaliseUsername(string input, string domain)
    {
        var value = input;

        var backslash = value.IndexOf('\\');
        if (backslash >= 0) value = value[(backslash + 1)..].Trim();

        var at = value.IndexOf('@');
        if (at > 0)
        {
            // Already a UPN — keep the domain the user typed; they may be from another suffix.
            return (value[..at], value);
        }

        return (value, string.IsNullOrEmpty(domain) ? value : $"{value}@{domain}");
    }

    /// Trust decision for the LDAPS channel: the domain's own issuing CA, plus the name the
    /// certificate must be issued to.
    private RemoteCertificateValidationCallback? BuildCertificateValidation(DirectoryServer server)
    {
        if (_options.Transport != DirectoryTransport.Ldaps) return null;

        if (_options.TlsInsecure)
            return (_, _, _, _) => true;

        return (_, certificate, chain, errors) =>
        {
            if (certificate == null) return false;

            // The default policy already checked the name against TargetHost and the chain against
            // the system store. Nothing left to prove.
            if (errors == SslPolicyErrors.None) return true;

            if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)) return false;

            using var leaf = new X509Certificate2(certificate);

            // The name must match whatever the operator said to expect, whichever error we are
            // recovering from. allowCommonName covers a DC certificate without a DNS SAN.
            if (!leaf.MatchesHostname(server.CertificateName, allowWildcards: true, allowCommonName: true))
            {
                _log?.Invoke($"directory sign-in: {server.Host} presented a certificate for "
                           + $"'{leaf.Subject}', which does not match '{server.CertificateName}'");
                return false;
            }

            if (!errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors)) return true;

            // A private enterprise CA is not in the system store, so rebuild the chain trusting
            // exactly the configured CA and nothing else.
            if (_options.CaCertificates == null || _options.CaCertificates.Count == 0)
            {
                _log?.Invoke($"directory sign-in: {server.Host}'s certificate does not chain to a "
                           + "trusted CA and VMENTORY_AD_CA_PEM is not set");
                return false;
            }

            using var rebuilt = new X509Chain();
            rebuilt.ChainPolicy.TrustMode         = X509ChainTrustMode.CustomRootTrust;
            rebuilt.ChainPolicy.RevocationMode    = X509RevocationMode.NoCheck;
            rebuilt.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            rebuilt.ChainPolicy.CustomTrustStore.AddRange(_options.CaCertificates);

            // Carry over any intermediates the server sent, so a chain through a subordinate CA builds.
            if (chain != null)
                foreach (var element in chain.ChainElements)
                    rebuilt.ChainPolicy.ExtraStore.Add(element.Certificate);

            if (rebuilt.Build(leaf)) return true;

            var why = string.Join("; ", rebuilt.ChainStatus.Select(s => s.StatusInformation.Trim()));
            _log?.Invoke($"directory sign-in: {server.Host}'s certificate failed to chain to the "
                       + $"configured CA — {why}");
            return false;
        };
    }
}
