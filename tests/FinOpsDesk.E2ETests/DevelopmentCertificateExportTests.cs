using System.Security.Cryptography.X509Certificates;

namespace FinOpsDesk.E2ETests;

public sealed class DevelopmentCertificateExportTests
{
    [Fact]
    public void Public_export_matches_the_server_certificate_and_omits_the_private_key()
    {
        string path = Path.Combine(Path.GetTempPath(), "finopsdesk-public-" + Guid.NewGuid().ToString("N") + ".crt");
        try
        {
            DevelopmentCertificateExport.WritePublicPem(path);
            string pem = File.ReadAllText(path);
            Assert.False(pem.Contains("PRIVATE KEY", StringComparison.Ordinal));
            Assert.Contains("BEGIN CERTIFICATE", pem, StringComparison.Ordinal);
            using X509Certificate2 published = X509CertificateLoader.LoadCertificateFromFile(path);
            DevelopmentCertificateExport.DevelopmentCertificateMaterial material = DevelopmentCertificateExport.LoadMaterial();
            Assert.Equal(material.Thumbprint, published.Thumbprint);
            Assert.True(material.HasPrivateKey);
            Assert.False(published.HasPrivateKey);
            Assert.True(DevelopmentCertificateExport.ServesLocalhost(published));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Certificate_setup_failure_is_closed()
    {
        Assert.Throws<InvalidOperationException>(() => DevelopmentCertificateExport.ReadThumbprint(string.Empty));
        Assert.Throws<InvalidOperationException>(() => DevelopmentCertificateExport.RejectPrivateKeyMaterial(
            "-----BEGIN PRIVATE KEY-----" + Environment.NewLine + "not-a-key" + Environment.NewLine + "-----END PRIVATE KEY-----"));
        Assert.Throws<InvalidOperationException>(() => DevelopmentCertificateExport.RequireUsableServerCertificate(
            hasPrivateKey: false,
            servesLocalhost: true));
        Assert.Throws<InvalidOperationException>(() => DevelopmentCertificateExport.RequireUsableServerCertificate(
            hasPrivateKey: true,
            servesLocalhost: false));

        string repositoryPath = Path.Combine(RepoPaths.Root, "finopsdesk-public-certificate-must-not-be-written.crt");
        Assert.Throws<InvalidOperationException>(() => DevelopmentCertificateExport.WritePublicPem(repositoryPath));
        Assert.False(File.Exists(repositoryPath));
    }
}