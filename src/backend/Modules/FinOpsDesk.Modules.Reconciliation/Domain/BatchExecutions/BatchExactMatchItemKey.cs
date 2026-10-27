using System.Security.Cryptography;
using System.Text;

using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;

public static class BatchExactMatchItemKey
{
    public const string Version = "batch-exact-match-item/v1";

    public static Guid Derive(
        OrganizationId organizationId,
        Guid batchIdempotencyKey,
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(invoiceId);
        ArgumentNullException.ThrowIfNull(bankTransactionId);

        if (organizationId.Value == Guid.Empty
            || batchIdempotencyKey == Guid.Empty
            || invoiceId.Value == Guid.Empty
            || bankTransactionId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "Batch item identity values cannot be empty.");
        }

        string canonical = Version
            + "\n"
            + organizationId.Value.ToString("D")
            + "\n"
            + batchIdempotencyKey.ToString("D")
            + "\n"
            + invoiceId.Value.ToString("D")
            + "\n"
            + bankTransactionId.Value.ToString("D")
            + "\n";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        Span<byte> identifier = stackalloc byte[16];
        hash.AsSpan(0, identifier.Length).CopyTo(identifier);

        // Version 8 marks a custom identifier. The variant bits mark an RFC 4122 UUID.
        identifier[6] = (byte)((identifier[6] & 0x0F) | 0x80);
        identifier[8] = (byte)((identifier[8] & 0x3F) | 0x80);

        string hex = Convert.ToHexString(identifier).ToLowerInvariant();

        return Guid.ParseExact(
            hex[..8]
                + "-"
                + hex.Substring(8, 4)
                + "-"
                + hex.Substring(12, 4)
                + "-"
                + hex.Substring(16, 4)
                + "-"
                + hex[20..],
            "D");
    }
}