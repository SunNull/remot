using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Remot.Server.Setup;

public static class CertGenerator
{
    /// <summary>生成自签证书并导出为可持久化的 X509Certificate2,返回证书与 SHA256 指纹。</summary>
    public static (X509Certificate2 Cert, string Fingerprint) GenerateSelfSigned(string dnsName, string password)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={dnsName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsName);
        req.CertificateExtensions.Add(san.Build());

        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        // 导出再导入,使私钥可持久化(避免 ephemeral 状态),pfx 可落盘。
        var pfx = cert.Export(X509ContentType.Pfx, password);
        var persisted = X509CertificateLoader.LoadPkcs12(pfx, password,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
        var fingerprint = persisted.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant();
        return (persisted, fingerprint);
    }
}
