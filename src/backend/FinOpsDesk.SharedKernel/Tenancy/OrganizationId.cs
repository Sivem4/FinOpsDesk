namespace FinOpsDesk.SharedKernel.Tenancy;

public sealed record OrganizationId
{
    public Guid Value { get; }

    public OrganizationId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                "Organization id cannot be empty.",
                nameof(value));
        }

        Value = value;
    }

    public static OrganizationId New()
    {
        return new OrganizationId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString();
    }
}