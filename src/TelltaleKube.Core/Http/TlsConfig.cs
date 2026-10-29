using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace TelltaleKube.Core.Http;

internal static class TlsConfig
{
    public static X509Certificate2Collection LoadCertificates(byte[] data)
    {
        var collection = new X509Certificate2Collection();
        var text = Encoding.ASCII.GetString(data);
        if (text.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            collection.ImportFromPem(text);
        }
        else
        {
            collection.Add(X509CertificateLoader.LoadCertificate(data));
        }
        if (collection.Count == 0)
        {
            throw new CryptographicException("No certificates found in certificate-authority data.");
        }
        return collection;
    }

    public static X509Certificate2 LoadClientCertificate(byte[] certPem, byte[] keyPem)
    {
        using var pemCert = X509Certificate2.CreateFromPem(Encoding.ASCII.GetString(certPem), Encoding.ASCII.GetString(keyPem));
        // SChannel (Windows) cannot use ephemeral PEM keys for client auth; round-trip through PKCS#12 in memory.
        // The export is never written to disk and uses a random one-time password.
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var pfx = pemCert.Export(X509ContentType.Pkcs12, password);
        try
        {
            var flags = OperatingSystem.IsLinux() || OperatingSystem.IsAndroid()
                ? X509KeyStorageFlags.EphemeralKeySet
                : X509KeyStorageFlags.UserKeySet;
            return X509CertificateLoader.LoadPkcs12(pfx, password, flags);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    /// <summary>
    /// Validates the API server certificate. With a cluster CA, the chain must terminate in exactly that CA
    /// (system roots are NOT consulted, so a public CA cannot impersonate a private cluster).
    /// </summary>
    public static RemoteCertificateValidationCallback CreateValidator(X509Certificate2Collection? clusterCa, bool insecureSkipVerify)
    {
        if (insecureSkipVerify)
        {
#pragma warning disable CA5359 // Explicit, policy-gated user opt-in (SecurityPolicy.AllowInsecureTls).
            return static (_, _, _, _) => true;
#pragma warning restore CA5359
        }

        if (clusterCa is null || clusterCa.Count == 0)
        {
            return static (_, _, _, errors) => errors == SslPolicyErrors.None;
        }

        return (_, certificate, _, errors) =>
        {
            if (certificate is null) return false;
            // Name mismatch is never acceptable, even with a custom CA.
            if ((errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
            {
                return false;
            }

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(clusterCa);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // Cluster CAs rarely publish CRLs/OCSP.
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            using var leaf = new X509Certificate2(certificate);
            return chain.Build(leaf);
        };
    }
}
