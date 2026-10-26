namespace FinOpsDesk.Modules.Reconciliation.Domain.Matching;

public enum SmartMatchSignalCode
{
    ReferenceExact = 1,
    ReferenceTokenSetEqual = 2,
    CompactFormEqual = 3,
    NumericIdentifierEqual = 4,
    AmountExact = 5,
    AmountDiffers = 6,
    ContestedCandidates = 7,
    ExactMatchAmbiguous = 8,
}