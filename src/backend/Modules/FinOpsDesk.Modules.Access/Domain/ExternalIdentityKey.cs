using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FinOpsDesk.Modules.Access.Domain;

// SHA-256 of a length-prefixed UTF-8 encoding. SQL Server equality, including a
// binary collation, treats strings that differ only by trailing spaces as equal,
// so the nvarchar columns are not the uniqueness key. Callers still compare the
// stored issuer and subject with ordinal equality before accepting a row.
public static class ExternalIdentityKey
{
    private static readonly Encoding Utf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static byte[] Compute(string issuer, string subject)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        ArgumentNullException.ThrowIfNull(subject);

        if (issuer.Length == 0 || issuer.Length > ApplicationUser.IssuerMaxCharacters)
        {
            throw new ArgumentException("Issuer length is not valid.", nameof(issuer));
        }

        if (subject.Length == 0 || subject.Length > ApplicationUser.SubjectMaxCharacters)
        {
            throw new ArgumentException("Subject length is not valid.", nameof(subject));
        }

        byte[] issuerBytes = Utf8.GetBytes(issuer);
        byte[] subjectBytes = Utf8.GetBytes(subject);
        byte[] payload = new byte[8 + issuerBytes.Length + subjectBytes.Length];
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0, 4), issuerBytes.Length);
        issuerBytes.CopyTo(payload.AsSpan(4));
        int subjectOffset = 4 + issuerBytes.Length;
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(subjectOffset, 4), subjectBytes.Length);
        subjectBytes.CopyTo(payload.AsSpan(subjectOffset + 4));
        return SHA256.HashData(payload);
    }

    public static bool Same(string storedIssuer, string storedSubject, string issuer, string subject)
    {
        return string.Equals(storedIssuer, issuer, StringComparison.Ordinal)
            && string.Equals(storedSubject, subject, StringComparison.Ordinal);
    }
}