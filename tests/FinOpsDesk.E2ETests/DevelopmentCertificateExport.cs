using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace FinOpsDesk.E2ETests;

internal static class DevelopmentCertificateExport
{
    public static void WritePublicPem(string path)
    {
        string thumbprint = ReadTrustedThumbprint();
        using X509Store store = new(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        X509Certificate2? certificate = null;
        foreach (X509Certificate2 candidate in store.Certificates)
        {
            if (string.Equals(candidate.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase))
            {
                certificate = candidate;
                break;
            }
        }

        if (certificate is null)
        {
            throw new InvalidOperationException(
                "The ASP.NET Core development certificate is not in CurrentUser\\Root. "
                + "Run: dotnet dev-certs https --trust");
        }

        if (certificate.HasPrivateKey)
        {
            throw new InvalidOperationException(
                "Refusing to export a development certificate that reports a private key.");
        }

        byte[] der = certificate.Export(X509ContentType.Cert);
        string pem = "-----BEGIN CERTIFICATE-----" + Environment.NewLine
            + Convert.ToBase64String(der, Base64FormattingOptions.InsertLineBreaks) + Environment.NewLine
            + "-----END CERTIFICATE-----" + Environment.NewLine;
        File.WriteAllText(path, pem, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string ReadTrustedThumbprint()
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "dev-certs https --check --trust",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start dotnet dev-certs.");
        }

        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "The ASP.NET Core HTTPS development certificate is not trusted. "
                + "Run: dotnet dev-certs https --trust");
        }

        Match match = Regex.Match(output, "[0-9A-Fa-f]{40}");
        if (!match.Success)
        {
            throw new InvalidOperationException("Could not read the development certificate thumbprint.");
        }

        return match.Value;
    }
}