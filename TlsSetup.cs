using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace VMentory.Web;

/// <summary>
/// Resolves the HTTPS server certificate for Core-terminated TLS (ENG-0010).
/// Precedence: operator PFX → operator PEM (cert+key) → self-signed fallback.
/// Nothing is baked into the image — cert material is injected at runtime via env/volume.
/// The dashboard cert is a DISTINCT trust domain from the agent mTLS PKI (ENG-0005); do not conflate.
/// </summary>
public static class TlsSetup
{
    // Env contract (all runtime-injected, never baked into the image):
    //   VMENTORY_TLS_PFX          — path to an operator-provided PKCS#12 bundle
    //   VMENTORY_TLS_PFX_PASSWORD — password for the PFX (optional)
    //   VMENTORY_TLS_CERT_PEM     — path to an operator-provided PEM certificate (chain)
    //   VMENTORY_TLS_KEY_PEM      — path to the matching PEM private key
    public static X509Certificate2 ResolveServerCertificate(AppConfig cfg, out string source)
    {
        var pfx = Environment.GetEnvironmentVariable("VMENTORY_TLS_PFX");
        if (!string.IsNullOrWhiteSpace(pfx))
        {
            source = $"operator PFX ({pfx})";
            var pw = Environment.GetEnvironmentVariable("VMENTORY_TLS_PFX_PASSWORD");
            return new X509Certificate2(pfx, pw, X509KeyStorageFlags.Exportable);
        }

        var certPem = Environment.GetEnvironmentVariable("VMENTORY_TLS_CERT_PEM");
        var keyPem  = Environment.GetEnvironmentVariable("VMENTORY_TLS_KEY_PEM");
        if (!string.IsNullOrWhiteSpace(certPem) && !string.IsNullOrWhiteSpace(keyPem))
        {
            source = $"operator PEM ({certPem})";
            using var loaded = X509Certificate2.CreateFromPemFile(certPem, keyPem);
            // Round-trip through PFX so Kestrel gets a cert with a usable, attached private key on all platforms.
            return new X509Certificate2(loaded.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
        }

        // Self-signed fallback — secure-by-default for a single operator standing up the container
        // (ENG-0010: self-signed-with-warning + easy cert mount, not refuse-to-start). When the data
        // directory is durable (real mode), cache the generated cert so the browser warning is one-time
        // and the server identity is stable across restarts. Mock mode stays ephemeral (writes nothing).
        if (cfg.Persist && !string.IsNullOrWhiteSpace(cfg.DataDir))
        {
            var path = Path.Combine(cfg.DataDir, "vmentory-selfsigned.pfx");
            if (File.Exists(path))
            {
                source = $"self-signed (cached: {path})";
                return new X509Certificate2(path, (string?)null, X509KeyStorageFlags.Exportable);
            }

            var generated = GenerateSelfSigned();
            try
            {
                File.WriteAllBytes(path, generated.Export(X509ContentType.Pfx));
                source = $"self-signed (generated, cached: {path})";
            }
            catch
            {
                source = "self-signed (generated, ephemeral — could not cache)";
            }
            return generated;
        }

        source = "self-signed (generated, ephemeral)";
        return GenerateSelfSigned();
    }

    private static X509Certificate2 GenerateSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=VMentory", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // serverAuth

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        try { san.AddDnsName(Dns.GetHostName()); } catch { /* best effort */ }
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());

        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        // Round-trip through PFX so the private key is materialized/usable by Kestrel (notably on Windows).
        return new X509Certificate2(cert.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }
}
