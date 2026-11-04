using System.Diagnostics;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace FinOpsDesk.E2ETests;

internal static partial class DevelopmentCertificateExport
{
    public static void WritePublicPem(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        RefuseRepositoryPath(path);
        DevelopmentCertificateMaterial material = LoadMaterial();
        RequireUsableServerCertificate(material.HasPrivateKey, material.ServesLocalhost);
        string pem = ToPublicPem(material.PublicDer);
        RejectPrivateKeyMaterial(pem);
        WriteRestricted(path, pem);
    }

    internal static DevelopmentCertificateMaterial LoadMaterial()
    {
        RunDevCerts("dev-certs", "https");
        string output = RunDevCerts("dev-certs", "https", "--check");
        string thumbprint = ReadThumbprint(output);
        using X509Store store = new(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        foreach (X509Certificate2 candidate in store.Certificates)
        {
            if (!string.Equals(candidate.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool hasPrivateKey = candidate.HasPrivateKey;
            bool servesLocalhost = ServesLocalhost(candidate);
            RequireUsableServerCertificate(hasPrivateKey, servesLocalhost);
            return new DevelopmentCertificateMaterial(
                candidate.Export(X509ContentType.Cert),
                candidate.Thumbprint,
                hasPrivateKey,
                servesLocalhost);
        }

        throw new InvalidOperationException(
            "The ASP.NET Core HTTPS development certificate is not in the current-user personal store. "
            + "Cookie E2E does not read the Windows Root store and was not skipped.");
    }

    internal static string ReadThumbprint(string output)
    {
        Match match = ThumbprintPattern().Match(output);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                "Could not read the development certificate thumbprint. Cookie E2E was not skipped.");
        }

        return match.Value;
    }

    internal static void RequireUsableServerCertificate(bool hasPrivateKey, bool servesLocalhost)
    {
        if (!hasPrivateKey)
        {
            throw new InvalidOperationException(
                "The ASP.NET Core HTTPS development certificate has no private key. "
                + "The API cannot present it. Cookie E2E was not skipped.");
        }

        if (!servesLocalhost)
        {
            throw new InvalidOperationException(
                "The ASP.NET Core HTTPS development certificate is not valid for localhost. "
                + "Hostname verification stays enabled. Cookie E2E was not skipped.");
        }
    }

    internal static void RejectPrivateKeyMaterial(string pem)
    {
        if (pem.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Refusing to write a development certificate export that contains a private key.");
        }
    }

    internal static bool ServesLocalhost(X509Certificate2 certificate)
    {
        const string subjectAlternativeName = "2.5.29.17";
        Asn1Tag dnsName = new(TagClass.ContextSpecific, 2);
        foreach (X509Extension extension in certificate.Extensions)
        {
            if (extension.Oid?.Value != subjectAlternativeName)
            {
                continue;
            }

            try
            {
                AsnReader outer = new(extension.RawData, AsnEncodingRules.DER);
                AsnReader sequence = outer.ReadSequence();
                while (sequence.HasData)
                {
                    if (!sequence.PeekTag().HasSameClassAndValue(dnsName))
                    {
                        sequence.ReadEncodedValue();
                        continue;
                    }

                    string dns = sequence.ReadCharacterString(UniversalTagNumber.IA5String, dnsName);
                    if (dns.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch (AsnContentException)
            {
                return false;
            }
        }

        return false;
    }

    internal static X509Certificate2 CopyServerCertificate()
    {
        DevelopmentCertificateMaterial material = LoadMaterial();
        using X509Store store = new(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        foreach (X509Certificate2 candidate in store.Certificates)
        {
            if (!string.Equals(candidate.Thumbprint, material.Thumbprint, StringComparison.OrdinalIgnoreCase) || !candidate.HasPrivateKey)
            {
                continue;
            }

            string password = Guid.NewGuid().ToString("N");
            byte[] pfx = candidate.Export(X509ContentType.Pfx, password);
            try
            {
                // Schannel cannot use an ephemeral key for a server certificate.
                // UserKeySet without PersistKeySet keeps the key in the user
                // container only until this certificate is disposed. The PFX bytes
                // are cleared and are not written to a file.
                return X509CertificateLoader.LoadPkcs12(
                    pfx,
                    password,
                    X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pfx);
            }
        }

        throw new InvalidOperationException(
            "The ASP.NET Core HTTPS development certificate could not be copied for the health probe. Cookie E2E was not skipped.");
    }

    private static string RunDevCerts(params string[] arguments)
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException(
                "Could not start dotnet dev-certs. Cookie E2E was not skipped.");
        }

        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "dotnet dev-certs https failed with exit code " + process.ExitCode
                + ". The HTTPS development certificate was not created. Cookie E2E was not skipped. "
                + RedactCertificateOutput(output));
        }

        return output;
    }

    private static string RedactCertificateOutput(string output)
    {
        string redacted = PrivateKeyPattern().Replace(output, "[redacted-private-key]");
        redacted = SensitiveText.Redact(redacted);
        if (redacted.Length > 400)
        {
            return redacted[..400];
        }

        return redacted;
    }

    private static string ToPublicPem(byte[] der)
    {
        return "-----BEGIN CERTIFICATE-----" + Environment.NewLine
            + Convert.ToBase64String(der, Base64FormattingOptions.InsertLineBreaks) + Environment.NewLine
            + "-----END CERTIFICATE-----" + Environment.NewLine;
    }

    private static void WriteRestricted(string path, string pem)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, pem, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void RefuseRepositoryPath(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetFullPath(RepoPaths.Root);
        string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (full.Equals(root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Refusing to write the development certificate inside the repository.");
        }
    }

    [GeneratedRegex("[0-9A-Fa-f]{40}", RegexOptions.CultureInvariant)]
    private static partial Regex ThumbprintPattern();

    [GeneratedRegex("-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\\s\\S]*?-----END [A-Z0-9 ]*PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyPattern();

    internal readonly record struct DevelopmentCertificateMaterial(
        byte[] PublicDer,
        string Thumbprint,
        bool HasPrivateKey,
        bool ServesLocalhost);
}