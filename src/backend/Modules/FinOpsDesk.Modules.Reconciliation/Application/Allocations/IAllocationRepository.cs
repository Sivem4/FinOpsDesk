using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

public interface IAllocationRepository
{
    void Add(Allocation allocation);
}