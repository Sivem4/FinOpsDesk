namespace FinOpsDesk.Modules.Reconciliation.Application;

// Counts handler entry for tests. Production registers the no-op.
public interface IFinancialHandlerObservation
{
    void OnImport();

    void OnExecute();
}

public sealed class NoFinancialHandlerObservation : IFinancialHandlerObservation
{
    public static NoFinancialHandlerObservation Instance { get; } = new();

    public void OnImport()
    {
    }

    public void OnExecute()
    {
    }
}