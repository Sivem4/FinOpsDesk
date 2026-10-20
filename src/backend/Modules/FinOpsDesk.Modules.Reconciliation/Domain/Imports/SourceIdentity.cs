namespace FinOpsDesk.Modules.Reconciliation.Domain.Imports;

public sealed record SourceIdentity
{
    public SourceSystem SourceSystem { get; }

    public ExternalId ExternalId { get; }

    public SourceIdentity(SourceSystem sourceSystem, ExternalId externalId)
    {
        ArgumentNullException.ThrowIfNull(sourceSystem);
        ArgumentNullException.ThrowIfNull(externalId);

        SourceSystem = sourceSystem;
        ExternalId = externalId;
    }

    public override string ToString()
    {
        return $"{SourceSystem}:{ExternalId}";
    }
}