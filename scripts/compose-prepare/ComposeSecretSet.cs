using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace FinOpsDesk.ComposePrepare;

public static class ComposeSecretSet
{
    public const int Created = 0;

    public const int Unchanged = 10;

    public const int Refused = 2;

    private const string PasswordFileName = "sql-password.txt";

    private const string SigningKeyFileName = "jwt-signing-key.txt";

    private const string CertificateFileName = "api.crt";

    private const string PrivateKeyFileName = "api.key";

    private const string IntegrationTestSigningKey = "finopsdesk-integration-test-signing-key";

    internal static readonly string[] SecretFileNames =
    [
        PasswordFileName,
        SigningKeyFileName,
        CertificateFileName,
        PrivateKeyFileName,
    ];

    public static Task<int> ExecuteAsync(
        string directory,
        TextWriter output,
        TextWriter error)
    {
        return ExecuteAsync(directory, output, error, afterFileCreated: null);
    }

    public static async Task<int> ExecuteAsync(
        string directory,
        TextWriter output,
        TextWriter error,
        Func<int, Task>? afterFileCreated)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        string root = Path.GetFullPath(directory);
        string[] paths = SecretFileNames.Select(name => Path.Combine(root, name)).ToArray();
        bool[] present = paths.Select(File.Exists).ToArray();

        if (present.All(exists => !exists))
        {
            Directory.CreateDirectory(root);
            SecretFiles created = CreateFiles();
            await WriteNewAsync(paths, created, afterFileCreated);

            await output.WriteLineAsync("Created the compose secret set.");
            return Created;
        }

        if (present.Any(exists => !exists))
        {
            string missing = string.Join(
                ", ",
                SecretFileNames.Where((_, index) => !present[index]));
            await error.WriteLineAsync(
                $"Refusing to change the compose secret set. Missing: {missing}.");
            return Refused;
        }

        string? problem = Validate(paths);
        if (problem is not null)
        {
            await error.WriteLineAsync(
                $"Refusing to change the compose secret set. {problem}");
            return Refused;
        }

        await output.WriteLineAsync("The compose secret set is unchanged.");
        return Unchanged;
    }

    private static async Task WriteNewAsync(
        string[] paths,
        SecretFiles files,
        Func<int, Task>? afterFileCreated)
    {
        string[] contents = [files.Password, files.SigningKey, files.CertificatePem, files.PrivateKeyPem];
        List<string> createdByThisAttempt = [];
        try
        {
            for (int index = 0; index < paths.Length; index++)
            {
                await using FileStream stream = new(
                    paths[index],
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None);
                createdByThisAttempt.Add(paths[index]);
                await stream.WriteAsync(Encoding.UTF8.GetBytes(contents[index]));
                if (afterFileCreated is not null)
                {
                    await afterFileCreated(index);
                }
            }
        }
        catch
        {
            foreach (string path in createdByThisAttempt)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            throw;
        }
    }

    private static string? Validate(string[] paths)
    {
        string password = File.ReadAllText(paths[0]);
        if (!IsSqlPassword(password))
        {
            return "Invalid: sql-password.txt.";
        }

        string signingKey = File.ReadAllText(paths[1]);
        if (!IsSigningKey(signingKey))
        {
            return "Invalid: jwt-signing-key.txt.";
        }

        string certificatePem = File.ReadAllText(paths[2]);
        string privateKeyPem = File.ReadAllText(paths[3]);
        try
        {
            using X509Certificate2 certificate = X509Certificate2.CreateFromPem(certificatePem, privateKeyPem);
            if (!HasDnsName(certificate, "api"))
            {
                return "Invalid: api.crt.";
            }
        }
        catch (CryptographicException)
        {
            return "Invalid: api.crt or api.key.";
        }

        return null;
    }

    private static SecretFiles CreateFiles()
    {
        using RSA rsa = RSA.Create(2048);
        CertificateRequest request = new(
            "CN=api",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        SubjectAlternativeNameBuilder san = new();
        san.AddDnsName("api");
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            true));
        DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        using X509Certificate2 certificate = request.CreateSelfSigned(notBefore, notBefore.AddYears(2));

        return new SecretFiles(
            CreatePassword(),
            CreateSigningKey(),
            certificate.ExportCertificatePem(),
            rsa.ExportPkcs8PrivateKeyPem());
    }

    private static string CreatePassword()
    {
        const string uppercase = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        const string lowercase = "abcdefghijklmnopqrstuvwxyz";
        const string digits = "0123456789";
        const string alphabet = uppercase + lowercase + digits;
        while (true)
        {
            char[] chars = new char[32];
            chars[0] = uppercase[RandomNumberGenerator.GetInt32(uppercase.Length)];
            chars[1] = lowercase[RandomNumberGenerator.GetInt32(lowercase.Length)];
            chars[2] = digits[RandomNumberGenerator.GetInt32(digits.Length)];
            for (int index = 3; index < chars.Length; index++)
            {
                chars[index] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
            }

            for (int index = chars.Length - 1; index > 0; index--)
            {
                int swap = RandomNumberGenerator.GetInt32(index + 1);
                (chars[index], chars[swap]) = (chars[swap], chars[index]);
            }

            string password = new(chars);
            if (!password.Contains("sa", StringComparison.OrdinalIgnoreCase))
            {
                return password;
            }
        }
    }

    private static string CreateSigningKey()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes);
    }

    private static bool IsSqlPassword(string password)
    {
        if (password.Length < 8
            || password.Contains("sa", StringComparison.OrdinalIgnoreCase)
            || password.Any(char.IsWhiteSpace))
        {
            return false;
        }

        bool upper = false;
        bool lower = false;
        bool digit = false;
        foreach (char character in password)
        {
            if (char.IsAsciiLetterUpper(character))
            {
                upper = true;
            }
            else if (char.IsAsciiLetterLower(character))
            {
                lower = true;
            }
            else if (char.IsAsciiDigit(character))
            {
                digit = true;
            }
            else
            {
                return false;
            }
        }

        return upper && lower && digit;
    }

    private static bool IsSigningKey(string signingKey)
    {
        if (string.Equals(signingKey, IntegrationTestSigningKey, StringComparison.Ordinal)
            || signingKey.Any(char.IsWhiteSpace))
        {
            return false;
        }

        return Encoding.UTF8.GetByteCount(signingKey) >= 32;
    }

    private static bool HasDnsName(X509Certificate2 certificate, string dnsName)
    {
        foreach (X509Extension extension in certificate.Extensions)
        {
            if (extension.Oid?.Value != "2.5.29.17")
            {
                continue;
            }

            AsnReader reader = new(extension.RawData, AsnEncodingRules.DER);
            AsnReader sequence = reader.ReadSequence();
            while (sequence.HasData)
            {
                Asn1Tag tag = sequence.PeekTag();
                if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 2)
                {
                    string dns = sequence.ReadCharacterString(UniversalTagNumber.IA5String, tag);
                    if (string.Equals(dns, dnsName, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                else
                {
                    sequence.ReadEncodedValue();
                }
            }
        }

        return false;
    }

    private sealed record SecretFiles(
        string Password,
        string SigningKey,
        string CertificatePem,
        string PrivateKeyPem);
}