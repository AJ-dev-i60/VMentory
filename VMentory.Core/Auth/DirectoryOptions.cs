namespace VMentory.Core.Auth;

using System.Security.Cryptography.X509Certificates;

/// One directory server to try, in order.
///
/// <paramref name="CertificateName"/> exists because the DCs are reached by address while their
/// LDAPS certificates are issued to their hostnames — `CN=DC1.i60.local` answering on
/// `172.0.0.10`. Connecting by name instead is not an option: `.local` is refused over unicast DNS
/// by systemd-resolved, so the name may not resolve from a Linux container at all. So we dial the
/// address and tell the TLS layer which name to expect.
public sealed record DirectoryServer(string Host, int Port, string CertificateName);

public enum DirectoryTransport
{
    /// LDAPS — TLS from the first byte, port 636. The only transport that should ever be used.
    Ldaps,
    /// Cleartext LDAP on port 389. Requires VMENTORY_AD_ALLOW_CLEARTEXT=1 and is logged as a
    /// warning on every sign-in, because it puts the user's domain password on the wire in the open.
    Plain,
}

/// Configuration for Windows-domain sign-in (ENG-0017). Entirely environment-driven, and absent any
/// of it the feature does not register — same contract as OIDC in ENG-0014.
public sealed class DirectoryOptions
{
    public bool Enabled { get; private init; }

    public IReadOnlyList<DirectoryServer> Servers { get; private init; } = Array.Empty<DirectoryServer>();
    public DirectoryTransport Transport { get; private init; } = DirectoryTransport.Ldaps;

    /// The AD DNS domain, e.g. `i60.local`. Used to complete a bare username into a UPN and as the
    /// default suffix a server certificate's name must sit under.
    public string Domain { get; private init; } = "";

    /// Display label for the sign-in form, e.g. "i60 domain".
    public string DisplayName { get; private init; } = "Windows domain";

    /// Search base, e.g. `DC=i60,DC=local`. Read from the rootDSE when left unset.
    public string? BaseDn { get; private init; }

    /// Where the directory's issuing CA came from, for the startup log. When a CA is configured,
    /// server certificates are validated against *that* trust store only — not the system one —
    /// which is how a private enterprise CA is trusted without installing anything into the image.
    public string? CaSource { get; private init; }

    /// Skips certificate validation entirely. Development only; refuses to be silent about itself.
    public bool TlsInsecure { get; private init; }

    /// Group name (sAMAccountName) → role. Membership is evaluated transitively.
    public IReadOnlyDictionary<AppRole, string> RoleGroups { get; private init; }
        = new Dictionary<AppRole, string>();

    /// Role for an authenticated account that matched none of the role groups. Null = refuse the
    /// sign-in, which is the default: a directory account existing is not an authorization decision.
    public AppRole? DefaultRole { get; private init; }

    public TimeSpan Timeout { get; private init; } = TimeSpan.FromSeconds(10);

    /// Loaded once at startup so a missing or malformed PEM fails loudly at boot rather than on
    /// someone's first sign-in attempt.
    public X509Certificate2Collection? CaCertificates { get; private init; }

    /// Config problems found while reading the environment. Non-empty means Enabled is false.
    public IReadOnlyList<string> Errors { get; private init; } = Array.Empty<string>();

    /// Things that are legal but worth shouting about — cleartext transport, disabled TLS checks.
    public IReadOnlyList<string> Warnings { get; private init; } = Array.Empty<string>();

    public static readonly DirectoryOptions Disabled = new() { Enabled = false };

    public static DirectoryOptions FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;

        string? Get(string name)
        {
            var v = read(name);
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }

        var hostsRaw = Get("VMENTORY_AD_HOSTS");
        var domain   = Get("VMENTORY_AD_DOMAIN");

        // Both are required; neither has a defensible default. With them absent the feature is off
        // and no error is reported — an unconfigured deployment is not a broken one.
        if (hostsRaw == null && domain == null) return Disabled;

        var errors   = new List<string>();
        var warnings = new List<string>();

        if (hostsRaw == null) errors.Add("VMENTORY_AD_HOSTS is not set.");
        if (domain   == null) errors.Add("VMENTORY_AD_DOMAIN is not set (e.g. i60.local).");

        var transport = DirectoryTransport.Ldaps;
        var transportRaw = Get("VMENTORY_AD_TRANSPORT")?.ToLowerInvariant();
        if (transportRaw is "plain" or "ldap")
        {
            if (Get("VMENTORY_AD_ALLOW_CLEARTEXT") == "1")
            {
                transport = DirectoryTransport.Plain;
                warnings.Add("VMENTORY_AD_TRANSPORT=plain — domain passwords are sent over the "
                           + "network in the clear. Use ldaps unless this is a throwaway lab.");
            }
            else
            {
                errors.Add("VMENTORY_AD_TRANSPORT=plain puts domain passwords on the wire in "
                         + "cleartext. Set VMENTORY_AD_ALLOW_CLEARTEXT=1 to accept that, or use ldaps.");
            }
        }
        else if (transportRaw is not null and not "ldaps")
        {
            errors.Add($"VMENTORY_AD_TRANSPORT='{transportRaw}' is not recognised (ldaps | plain). "
                     + "StartTLS is not implemented.");
        }

        var defaultPort = transport == DirectoryTransport.Ldaps ? 636 : 389;
        var servers = ParseServers(hostsRaw, defaultPort, domain, errors);

        var tlsInsecure = Get("VMENTORY_AD_TLS_INSECURE") == "1";
        if (tlsInsecure && transport == DirectoryTransport.Ldaps)
            warnings.Add("VMENTORY_AD_TLS_INSECURE=1 — the directory's TLS certificate is not being "
                       + "checked, so this connection can be impersonated. Development only.");

        // The CA can arrive as a file path or, because a Coolify environment variable is easier to
        // set than a mounted file, as base64 of the PEM — the same convention the fleet probe key
        // already uses.
        X509Certificate2Collection? caCerts = null;
        var caPath = Get("VMENTORY_AD_CA_PEM");
        var caB64  = Get("VMENTORY_AD_CA_B64");
        var caSource = caPath != null ? $"VMENTORY_AD_CA_PEM ({caPath})" : caB64 != null ? "VMENTORY_AD_CA_B64" : null;

        if (caSource != null && !tlsInsecure)
        {
            try
            {
                caCerts = new X509Certificate2Collection();
                if (caPath != null) caCerts.ImportFromPemFile(caPath);
                else caCerts.ImportFromPem(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(caB64!)));

                if (caCerts.Count == 0) errors.Add($"{caSource} contains no certificates.");
            }
            catch (Exception ex)
            {
                caCerts = null;
                errors.Add($"{caSource} could not be read: {ex.Message}");
            }
        }
        else if (caSource == null && transport == DirectoryTransport.Ldaps && !tlsInsecure)
        {
            warnings.Add("Neither VMENTORY_AD_CA_PEM nor VMENTORY_AD_CA_B64 is set, so the directory's "
                       + "certificate must chain to a CA in the system trust store. A domain's own "
                       + "enterprise CA does not — export it and set one of these.");
        }

        var roleGroups = new Dictionary<AppRole, string>();
        foreach (var (role, variable) in new[]
                 {
                     (AppRole.Admin,           "VMENTORY_AD_GROUP_ADMIN"),
                     (AppRole.VmOperator,      "VMENTORY_AD_GROUP_VMOPERATOR"),
                     (AppRole.BackupOperator,  "VMENTORY_AD_GROUP_BACKUPOPERATOR"),
                     (AppRole.Viewer,          "VMENTORY_AD_GROUP_VIEWER"),
                 })
        {
            var group = Get(variable);
            if (group != null) roleGroups[role] = group;
        }

        AppRole? defaultRole = null;
        var defaultRoleRaw = Get("VMENTORY_AD_DEFAULT_ROLE");
        if (defaultRoleRaw != null)
        {
            if (Enum.TryParse<AppRole>(defaultRoleRaw, ignoreCase: true, out var parsed))
                defaultRole = parsed;
            else
                errors.Add($"VMENTORY_AD_DEFAULT_ROLE='{defaultRoleRaw}' is not a role "
                         + $"({string.Join(" | ", Enum.GetNames<AppRole>())}).");
        }

        if (roleGroups.Count == 0 && defaultRole == null)
            errors.Add("No way to assign a role: set at least one of VMENTORY_AD_GROUP_ADMIN / "
                     + "_VMOPERATOR / _BACKUPOPERATOR / _VIEWER, or VMENTORY_AD_DEFAULT_ROLE. "
                     + "Without one, every sign-in would be refused.");

        var timeout = TimeSpan.FromSeconds(10);
        var timeoutRaw = Get("VMENTORY_AD_TIMEOUT_SECONDS");
        if (timeoutRaw != null)
        {
            if (int.TryParse(timeoutRaw, out var secs) && secs is >= 1 and <= 120)
                timeout = TimeSpan.FromSeconds(secs);
            else
                errors.Add($"VMENTORY_AD_TIMEOUT_SECONDS='{timeoutRaw}' must be a whole number of seconds, 1–120.");
        }

        return new DirectoryOptions
        {
            Enabled        = errors.Count == 0,
            Servers        = servers,
            Transport      = transport,
            Domain         = domain ?? "",
            DisplayName    = Get("VMENTORY_AD_NAME") ?? (domain != null ? $"{domain} domain" : "Windows domain"),
            BaseDn         = Get("VMENTORY_AD_BASE_DN"),
            CaSource       = caSource,
            CaCertificates = caCerts,
            TlsInsecure    = tlsInsecure,
            RoleGroups     = roleGroups,
            DefaultRole    = defaultRole,
            Timeout        = timeout,
            Errors         = errors,
            Warnings       = warnings,
        };
    }

    /// `host[:port][|certificateName]`, comma- or semicolon-separated.
    private static List<DirectoryServer> ParseServers(string? raw, int defaultPort, string? domain, List<string> errors)
    {
        var servers = new List<DirectoryServer>();
        if (raw == null) return servers;

        foreach (var entry in raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var certName = (string?)null;
            var address  = entry;

            var bar = entry.IndexOf('|');
            if (bar >= 0)
            {
                address  = entry[..bar].Trim();
                certName = entry[(bar + 1)..].Trim();
                if (certName.Length == 0) certName = null;
            }

            var port = defaultPort;
            var colon = address.LastIndexOf(':');
            // Guard against an IPv6 literal: only treat a trailing :NNNN as a port.
            if (colon > 0 && address.IndexOf(':') == colon && int.TryParse(address[(colon + 1)..], out var parsedPort))
            {
                if (parsedPort is < 1 or > 65535)
                {
                    errors.Add($"VMENTORY_AD_HOSTS entry '{entry}' has an out-of-range port.");
                    continue;
                }
                port    = parsedPort;
                address = address[..colon].Trim();
            }

            if (address.Length == 0)
            {
                errors.Add($"VMENTORY_AD_HOSTS entry '{entry}' has no host.");
                continue;
            }

            // Absent an explicit name, expect the certificate to be issued to the address itself —
            // which only works when the entry is a hostname. For an IP, say so rather than silently
            // failing the handshake later.
            if (certName == null)
            {
                var looksLikeIp = System.Net.IPAddress.TryParse(address, out _);
                if (looksLikeIp && domain != null)
                    errors.Add($"VMENTORY_AD_HOSTS entry '{entry}' is an address with no certificate "
                             + $"name. Write it as '{address}|DC1.{domain}' so TLS knows what name to "
                             + "expect, because the certificate is issued to the hostname.");
                certName = address;
            }

            servers.Add(new DirectoryServer(address, port, certName));
        }

        if (servers.Count == 0 && raw.Length > 0 && errors.Count == 0)
            errors.Add("VMENTORY_AD_HOSTS parsed to no usable servers.");

        return servers;
    }
}
