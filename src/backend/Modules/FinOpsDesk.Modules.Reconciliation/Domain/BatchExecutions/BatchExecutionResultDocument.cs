using System.Globalization;
using System.Text;
using System.Text.Json;

using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;

namespace FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;

public static class BatchExecutionResultDocument
{
    public static string Write(BatchExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        using MemoryStream stream = new();
        using Utf8JsonWriter writer = new(stream);
        writer.WriteStartObject();
        writer.WriteNumber("resultVersion", result.ResultVersion);
        writer.WriteNumber("requestedCount", result.RequestedCount);
        writer.WriteNumber("confirmedCount", result.ConfirmedCount);
        writer.WriteNumber("rejectedCount", result.RejectedCount);
        writer.WriteNumber("interruptedCount", result.InterruptedCount);
        writer.WriteBoolean("allConfirmed", result.AllConfirmed);
        writer.WriteBoolean("hasRejections", result.HasRejections);
        writer.WriteBoolean("partial", result.Partial);
        writer.WritePropertyName("items");
        writer.WriteStartArray();

        foreach (BatchExecutionItem item in result.Items)
        {
            writer.WriteStartObject();
            writer.WriteString("invoiceId", item.InvoiceId.Value.ToString("D"));
            writer.WriteString("bankTransactionId", item.BankTransactionId.Value.ToString("D"));
            writer.WriteString("outcome", OutcomeName(item.Outcome));

            if (item.Outcome == BatchExecutionItemOutcome.Confirmed
                && item.AllocationId is not null
                && item.Amount is not null)
            {
                writer.WriteString("allocationId", item.AllocationId.Value.ToString("D"));
                writer.WritePropertyName("amount");
                writer.WriteRawValue(
                    item.Amount.Amount.ToString("0.0000", CultureInfo.InvariantCulture));
                writer.WriteString("currency", item.Amount.Currency.Value);
            }
            else if (item.Outcome != BatchExecutionItemOutcome.Confirmed)
            {
                writer.WriteNull("allocationId");
                writer.WriteNull("amount");
                writer.WriteNull("currency");
            }
            else
            {
                throw new InvalidOperationException(
                    "A confirmed batch item requires an allocation id, amount, and currency.");
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static BatchExecutionResult Read(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "Batch execution result payload must be an object.",
                nameof(payload));
        }

        int resultVersion = RequireInt(root, "resultVersion");
        int requestedCount = RequireInt(root, "requestedCount");
        int confirmedCount = RequireInt(root, "confirmedCount");
        int rejectedCount = RequireInt(root, "rejectedCount");
        int interruptedCount = RequireInt(root, "interruptedCount");
        bool allConfirmed = RequireBool(root, "allConfirmed");
        bool hasRejections = RequireBool(root, "hasRejections");
        bool partial = RequireBool(root, "partial");
        JsonElement itemsElement = RequireProperty(root, "items");

        if (itemsElement.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException(
                "Batch execution result items must be an array.",
                nameof(payload));
        }

        List<BatchExecutionItem> items = [];

        foreach (JsonElement item in itemsElement.EnumerateArray())
        {
            items.Add(ReadItem(item));
        }

        return BatchExecutionResult.FromDocument(
            resultVersion,
            requestedCount,
            confirmedCount,
            rejectedCount,
            interruptedCount,
            allConfirmed,
            hasRejections,
            partial,
            items);
    }

    private static BatchExecutionItem ReadItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Batch execution result item must be an object.");
        }

        InvoiceId invoiceId = new(RequireGuid(item, "invoiceId"));
        BankTransactionId bankTransactionId = new(RequireGuid(item, "bankTransactionId"));
        BatchExecutionItemOutcome outcome = ParseOutcome(RequireString(item, "outcome"));
        AllocationId? allocationId = ReadOptionalGuid(item, "allocationId", static value => new AllocationId(value));
        Money? amount = ReadAmount(item);

        return new BatchExecutionItem(
            invoiceId,
            bankTransactionId,
            outcome,
            allocationId,
            amount);
    }

    private static Money? ReadAmount(JsonElement item)
    {
        JsonElement amount = RequireProperty(item, "amount");
        JsonElement currency = RequireProperty(item, "currency");
        bool amountNull = amount.ValueKind == JsonValueKind.Null;
        bool currencyNull = currency.ValueKind == JsonValueKind.Null;

        if (amountNull && currencyNull)
        {
            return null;
        }

        if (amountNull || currencyNull || amount.ValueKind != JsonValueKind.Number)
        {
            throw new ArgumentException(
                "A confirmed batch item amount and currency must be present together.");
        }

        decimal value = amount.GetDecimal();

        return new Money(value, new CurrencyCode(currency.GetString()!));
    }

    private static T? ReadOptionalGuid<T>(
        JsonElement item,
        string name,
        Func<Guid, T> create)
        where T : class
    {
        JsonElement property = RequireProperty(item, name);

        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return create(ParseGuid(property));
    }

    private static string OutcomeName(BatchExecutionItemOutcome outcome)
    {
        return outcome switch
        {
            BatchExecutionItemOutcome.Confirmed => "confirmed",
            BatchExecutionItemOutcome.SkippedAmbiguous => "skippedAmbiguous",
            BatchExecutionItemOutcome.SkippedCoverage => "skippedCoverage",
            BatchExecutionItemOutcome.SkippedReview => "skippedReview",
            BatchExecutionItemOutcome.Stale => "stale",
            BatchExecutionItemOutcome.Conflict => "conflict",
            BatchExecutionItemOutcome.NotFound => "notFound",
            BatchExecutionItemOutcome.Interrupted => "interrupted",
            _ => throw new ArgumentOutOfRangeException(
                nameof(outcome),
                "Batch execution item outcome is not defined.")
        };
    }

    private static BatchExecutionItemOutcome ParseOutcome(string name)
    {
        return name switch
        {
            "confirmed" => BatchExecutionItemOutcome.Confirmed,
            "skippedAmbiguous" => BatchExecutionItemOutcome.SkippedAmbiguous,
            "skippedCoverage" => BatchExecutionItemOutcome.SkippedCoverage,
            "skippedReview" => BatchExecutionItemOutcome.SkippedReview,
            "stale" => BatchExecutionItemOutcome.Stale,
            "conflict" => BatchExecutionItemOutcome.Conflict,
            "notFound" => BatchExecutionItemOutcome.NotFound,
            "interrupted" => BatchExecutionItemOutcome.Interrupted,
            _ => throw new ArgumentException("Batch execution item outcome is not defined.")
        };
    }

    private static int RequireInt(JsonElement root, string name)
    {
        JsonElement property = RequireProperty(root, name);

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out int value))
        {
            throw new ArgumentException("Batch execution result field '" + name + "' must be an integer.");
        }

        return value;
    }

    private static bool RequireBool(JsonElement root, string name)
    {
        JsonElement property = RequireProperty(root, name);

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ArgumentException(
                "Batch execution result field '" + name + "' must be a boolean.")
        };
    }

    private static string RequireString(JsonElement item, string name)
    {
        JsonElement property = RequireProperty(item, name);

        if (property.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException("Batch execution result field '" + name + "' must be a string.");
        }

        return property.GetString()
            ?? throw new ArgumentException("Batch execution result field '" + name + "' must be a string.");
    }

    private static Guid RequireGuid(JsonElement item, string name)
    {
        return ParseGuid(RequireProperty(item, name));
    }

    private static Guid ParseGuid(JsonElement property)
    {
        string? text = property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

        if (text is null
            || !Guid.TryParseExact(text, "D", out Guid value)
            || value == Guid.Empty
            || !string.Equals(text, value.ToString("D"), StringComparison.Ordinal))
        {
            throw new ArgumentException("Batch execution result identifier must be a lowercase guid.");
        }

        return value;
    }

    private static JsonElement RequireProperty(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out JsonElement property))
        {
            throw new ArgumentException("Batch execution result field '" + name + "' is required.");
        }

        return property;
    }
}