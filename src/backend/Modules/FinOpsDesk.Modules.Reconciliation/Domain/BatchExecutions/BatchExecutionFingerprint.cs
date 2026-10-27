namespace FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;

public sealed class BatchExecutionFingerprint
{
    public const int Length = 32;

    private readonly byte[] _bytes;

    public BatchExecutionFingerprint(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length != Length)
        {
            throw new ArgumentException(
                "Batch execution fingerprint must be 32 bytes.",
                nameof(bytes));
        }

        _bytes = (byte[])bytes.Clone();
    }

    public byte[] ToArray()
    {
        return (byte[])_bytes.Clone();
    }

    public override bool Equals(object? obj)
    {
        return obj is BatchExecutionFingerprint other
            && _bytes.AsSpan().SequenceEqual(other._bytes);
    }

    public override int GetHashCode()
    {
        HashCode hash = default;

        foreach (byte value in _bytes)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }
}