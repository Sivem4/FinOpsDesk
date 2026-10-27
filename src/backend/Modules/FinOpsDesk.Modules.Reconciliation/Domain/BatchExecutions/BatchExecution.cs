using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;

public sealed class BatchExecution
{
    public BatchExecutionId Id { get; }

    public OrganizationId OrganizationId { get; }

    public Guid IdempotencyKey { get; }

    public BatchExecutionFingerprint Fingerprint { get; }

    public BatchExecutionStatus Status { get; private set; }

    public int? ResultVersion { get; private set; }

    public string? ResultPayload { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset? CompletedAt { get; private set; }

#pragma warning disable CS8618
    private BatchExecution()
    {
    }
#pragma warning restore CS8618

    private BatchExecution(
        BatchExecutionId id,
        OrganizationId organizationId,
        Guid idempotencyKey,
        BatchExecutionFingerprint fingerprint,
        DateTimeOffset createdAt)
    {
        Id = id;
        OrganizationId = organizationId;
        IdempotencyKey = idempotencyKey;
        Fingerprint = fingerprint;
        Status = BatchExecutionStatus.Running;
        CreatedAt = createdAt;
    }

    public static BatchExecution Start(
        BatchExecutionId id,
        OrganizationId organizationId,
        Guid idempotencyKey,
        BatchExecutionFingerprint fingerprint,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(fingerprint);

        if (organizationId.Value == Guid.Empty || idempotencyKey == Guid.Empty)
        {
            throw new ArgumentException(
                "Batch execution identity values cannot be empty.");
        }

        if (createdAt == default)
        {
            throw new ArgumentOutOfRangeException(
                nameof(createdAt),
                "Batch execution creation time cannot be the default value.");
        }

        return new BatchExecution(
            id,
            organizationId,
            idempotencyKey,
            fingerprint,
            createdAt);
    }

    public void Complete(BatchExecutionResult result, DateTimeOffset completedAt)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (Status != BatchExecutionStatus.Running)
        {
            throw new InvalidOperationException(
                "A completed batch execution cannot be completed again.");
        }

        if (completedAt == default)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedAt),
                "Batch execution completion time cannot be the default value.");
        }

        if (completedAt < CreatedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedAt),
                "Batch execution completion time cannot precede creation time.");
        }

        EnsureResultMatches(Fingerprint, result);

        string payload = BatchExecutionResultDocument.Write(result);

        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new InvalidOperationException(
                "A completed batch execution requires a result payload.");
        }

        Status = BatchExecutionStatus.Completed;
        ResultVersion = result.ResultVersion;
        ResultPayload = payload;
        CompletedAt = completedAt;
    }

    internal static void EnsureResultMatches(
        BatchExecutionFingerprint fingerprint,
        BatchExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentNullException.ThrowIfNull(result);

        BatchExactMatchPair[] pairs = new BatchExactMatchPair[result.Items.Count];

        for (int index = 0; index < result.Items.Count; index++)
        {
            BatchExecutionItem item = result.Items[index];
            pairs[index] = new BatchExactMatchPair(item.InvoiceId, item.BankTransactionId);
        }

        BatchExecutionFingerprint actual = BatchExactMatchIdentity.Create(pairs).Fingerprint;

        if (!fingerprint.Equals(actual))
        {
            throw new InvalidOperationException(
                "Batch execution result pairs do not match the original request.");
        }
    }
}