namespace VMentory.Core.Auth.Ldap;

using System.Text;

// Minimal BER/DER codec for the four LDAP protocol operations VMentory needs (ENG-0017):
// bindRequest, bindResponse, searchRequest and the search results. This is deliberately NOT a
// general ASN.1 implementation — it encodes exactly the shapes RFC 4511 defines for those ops and
// rejects everything else loudly.
//
// Why hand-rolled rather than System.DirectoryServices.Protocols: that package is a P/Invoke shim
// over OpenLDAP on Linux, so it needs libldap in the runtime image and hands TLS trust decisions to
// the native library. Doing the wire format here keeps the container dependency-free and — the real
// reason — lets the TLS channel be a plain .NET SslStream, where pinning the domain's issuing CA and
// checking the server name are ordinary, well-trodden code. See 0017 for the full argument.

internal static class Ber
{
    // Universal tags
    public const byte TagInteger     = 0x02;
    public const byte TagOctetString = 0x04;
    public const byte TagEnumerated  = 0x0A;
    public const byte TagBoolean     = 0x01;
    public const byte TagSequence    = 0x30;   // constructed

    /// DER length octets: short form below 0x80, long form above.
    public static byte[] Length(int n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
        if (n < 0x80) return new[] { (byte)n };

        var tmp = new List<byte>(4);
        for (var v = n; v > 0; v >>= 8) tmp.Insert(0, (byte)(v & 0xFF));

        var buf = new byte[tmp.Count + 1];
        buf[0] = (byte)(0x80 | tmp.Count);
        tmp.CopyTo(buf, 1);
        return buf;
    }

    /// One tag-length-value element whose content is the concatenation of <paramref name="parts"/>.
    public static byte[] Tlv(byte tag, params byte[][] parts)
    {
        var contentLength = 0;
        foreach (var p in parts) contentLength += p.Length;

        var len = Length(contentLength);
        var buf = new byte[1 + len.Length + contentLength];
        buf[0] = tag;
        Buffer.BlockCopy(len, 0, buf, 1, len.Length);

        var off = 1 + len.Length;
        foreach (var p in parts)
        {
            Buffer.BlockCopy(p, 0, buf, off, p.Length);
            off += p.Length;
        }
        return buf;
    }

    /// Minimal signed two's-complement integer, per DER.
    public static byte[] Int(long value, byte tag = TagInteger)
    {
        var bytes = new List<byte>(8);
        var v = value;
        while (true)
        {
            var b = (byte)(v & 0xFF);
            v >>= 8;
            bytes.Insert(0, b);
            // Stop once what remains is pure sign extension of the byte just written.
            if ((v == 0 && (b & 0x80) == 0) || (v == -1 && (b & 0x80) != 0)) break;
        }
        return Tlv(tag, bytes.ToArray());
    }

    public static byte[] Enumerated(int value) => Int(value, TagEnumerated);

    public static byte[] Str(string s, byte tag = TagOctetString) =>
        Tlv(tag, Encoding.UTF8.GetBytes(s));

    public static byte[] Bool(bool b, byte tag = TagBoolean) =>
        Tlv(tag, new[] { (byte)(b ? 0xFF : 0x00) });

    public static byte[] Seq(params byte[][] parts) => Tlv(TagSequence, parts);
}

/// One parsed BER element, as a window onto the buffer it was read from.
internal sealed class BerElement
{
    public byte Tag { get; init; }
    public byte[] Buffer { get; init; } = Array.Empty<byte>();
    public int ContentStart { get; init; }
    public int ContentLength { get; init; }

    public bool IsConstructed => (Tag & 0x20) != 0;

    public string AsString() => Encoding.UTF8.GetString(Buffer, ContentStart, ContentLength);

    public long AsInt()
    {
        if (ContentLength == 0) return 0;
        long v = (sbyte)Buffer[ContentStart];                       // sign-extend the first octet
        for (var i = 1; i < ContentLength; i++) v = (v << 8) | Buffer[ContentStart + i];
        return v;
    }

    public List<BerElement> Children()
    {
        var list = new List<BerElement>();
        var off = ContentStart;
        var end = ContentStart + ContentLength;
        while (off < end) list.Add(BerParse.Read(Buffer, ref off, end));
        return list;
    }

    /// The nth child, or throw with a message that says which op was malformed.
    public BerElement Child(int index, string what)
    {
        var kids = Children();
        if (index >= kids.Count)
            throw new LdapProtocolException($"{what}: expected at least {index + 1} elements, found {kids.Count}");
        return kids[index];
    }
}

internal static class BerParse
{
    // A single LDAP response should never be anywhere near this large; the cap stops a confused or
    // hostile peer from making us allocate on a length field alone.
    public const int MaxElementBytes = 8 * 1024 * 1024;

    /// Read one TLV starting at <paramref name="off"/>, advancing it past the element.
    public static BerElement Read(byte[] buf, ref int off, int end)
    {
        if (off + 2 > end) throw new LdapProtocolException("truncated BER element");

        var tag = buf[off++];
        int len = buf[off++];

        if ((len & 0x80) != 0)
        {
            var n = len & 0x7F;
            if (n == 0) throw new LdapProtocolException("indefinite BER lengths are not supported");
            if (n > 4)  throw new LdapProtocolException($"BER length of {n} octets is out of range");
            if (off + n > end) throw new LdapProtocolException("truncated BER length");

            len = 0;
            for (var i = 0; i < n; i++) len = (len << 8) | buf[off++];
            if (len is < 0 or > MaxElementBytes)
                throw new LdapProtocolException($"BER element length {len} is out of range");
        }

        if (off + len > end) throw new LdapProtocolException("BER element overruns its container");

        var el = new BerElement { Tag = tag, Buffer = buf, ContentStart = off, ContentLength = len };
        off += len;
        return el;
    }
}

internal sealed class LdapProtocolException : Exception
{
    public LdapProtocolException(string message) : base(message) { }
}
