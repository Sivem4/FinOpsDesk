using FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.BatchExecutions;

public sealed record BatchExecutionSnapshot(
    BatchExecutionId Id,
    OrganizationId OrganizationId,
    Guid IdempotencyKey,
    BatchExecutionFingerprint Fingerprint,
    BatchExecutionStatus Status,
    int? ResultVersion,
    string? ResultPayload,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);