using Remot.Server.Setup;
using Xunit;

namespace Remot.Server.Tests;

public class CertGeneratorTests
{
    [Fact]
    public void Generated_cert_has_sha256_fingerprint_and_subject()
    {
        var (cert, fingerprint) = CertGenerator.GenerateSelfSigned(dnsName: "remot", password: "p");
        Assert.Equal(64, fingerprint.Length);   // 32 字节 hex
        Assert.Contains("remot", cert.Subject);
    }
}
