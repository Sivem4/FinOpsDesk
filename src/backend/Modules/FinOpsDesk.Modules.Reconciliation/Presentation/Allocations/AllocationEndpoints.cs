using System.Buffers;
using System.Text.Json;

using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Results;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;
using Microsoft.OpenApi;

namespace FinOpsDesk.Modules.Reconciliation.Presentation.Allocations;

internal static class AllocationEndpoints
{
    private const int MaxJsonBodyBytes = 4096;

    private static readonly string[] RootPropertyNames = ["invoiceId", "bankTransactionId", "amount"];

    private static readonly string[] AmountPropertyNames = ["amount", "currency"];

    public static RouteGroupBuilder MapAllocationEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapPost("/allocations", AllocateAsync)
            .WithName("AllocateExplicitAmount")
            .AddOpenApiOperationTransformer(DocumentAllocationOperation);

        return group;
    }

    private static async Task<Results<JsonHttpResult<ConfirmExplicitAmountResponse>, ProblemHttpResult>> AllocateAsync(
        HttpRequest httpRequest,
        ConfirmExplicitAmountHandler handler,
        CancellationToken cancellationToken)
    {
        if (!IsJsonContentType(httpRequest.ContentType))
        {
            return UnsupportedMediaType();
        }

        if (!TryReadIdempotencyKey(httpRequest, out Guid idempotencyKey, out Error? keyError))
        {
            return Problem(keyError!, StatusCodeFor(keyError!), TitleFor(keyError!));
        }

        byte[]? body = await ReadBoundedBodyAsync(httpRequest.Body, cancellationToken);
        if (body is null)
        {
            return RequestTooLarge();
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException exception) when (exception.InnerException is not OperationCanceledException)
        {
            return InvalidAllocationRequest();
        }

        using (document)
        {
            if (!TryReadRequest(
                    document.RootElement,
                    out Guid invoiceId,
                    out Guid bankTransactionId,
                    out decimal amount,
                    out string? currency,
                    out Error? requestError)
                || string.IsNullOrWhiteSpace(currency))
            {
                Error requestFailure = requestError
                    ?? ReconciliationApplicationErrors.AllocationInvalidRequest;
                return Problem(requestFailure, StatusCodeFor(requestFailure), TitleFor(requestFailure));
            }

            CurrencyCode parsedCurrency;
            try
            {
                parsedCurrency = new CurrencyCode(currency);
            }
            catch (ArgumentException)
            {
                return InvalidAllocationRequest();
            }

            Result<ConfirmExplicitAmountResponse> result = await handler.Handle(
                new ConfirmExplicitAmountCommand(
                    new InvoiceId(invoiceId),
                    new BankTransactionId(bankTransactionId),
                    new Money(amount, parsedCurrency),
                    idempotencyKey),
                cancellationToken);

            if (result.IsFailure)
            {
                return Problem(result.Error, StatusCodeFor(result.Error), TitleFor(result.Error));
            }

            int statusCode = result.Value.Replayed
                ? StatusCodes.Status200OK
                : StatusCodes.Status201Created;

            return TypedResults.Json(result.Value, statusCode: statusCode);
        }
    }

    private static bool TryReadRequest(
        JsonElement root,
        out Guid invoiceId,
        out Guid bankTransactionId,
        out decimal amount,
        out string? currency,
        out Error? error)
    {
        invoiceId = Guid.Empty;
        bankTransactionId = Guid.Empty;
        amount = 0m;
        currency = null;
        error = ReconciliationApplicationErrors.AllocationInvalidRequest;

        if (root.ValueKind != JsonValueKind.Object
            || !TryReadObject(root, RootPropertyNames, out Dictionary<string, JsonElement> properties, out error))
        {
            return false;
        }

        if (!properties.TryGetValue("invoiceId", out JsonElement invoiceElement)
            || !properties.TryGetValue("bankTransactionId", out JsonElement bankTransactionElement)
            || !properties.TryGetValue("amount", out JsonElement amountElement)
            || !TryReadGuid(invoiceElement, out invoiceId)
            || !TryReadGuid(bankTransactionElement, out bankTransactionId)
            || amountElement.ValueKind != JsonValueKind.Object)
        {
            error = ReconciliationApplicationErrors.AllocationInvalidRequest;
            return false;
        }

        if (!TryReadObject(amountElement, AmountPropertyNames, out Dictionary<string, JsonElement> amountProperties, out error))
        {
            return false;
        }

        if (!amountProperties.TryGetValue("amount", out JsonElement valueElement)
            || !amountProperties.TryGetValue("currency", out JsonElement currencyElement)
            || currencyElement.ValueKind != JsonValueKind.String)
        {
            error = ReconciliationApplicationErrors.AllocationInvalidRequest;
            return false;
        }

        currency = currencyElement.GetString();
        if (string.IsNullOrWhiteSpace(currency))
        {
            error = ReconciliationApplicationErrors.AllocationInvalidRequest;
            return false;
        }

        if (valueElement.ValueKind != JsonValueKind.Number
            || !JsonDecimalLiteral.TryReadExact(valueElement, out amount))
        {
            error = valueElement.ValueKind == JsonValueKind.Number
                ? ReconciliationApplicationErrors.ExplicitAmountInvalid
                : ReconciliationApplicationErrors.AllocationInvalidRequest;
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryReadObject(
        JsonElement element,
        IReadOnlyList<string> allowedNames,
        out Dictionary<string, JsonElement> properties,
        out Error? error)
    {
        properties = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!allowedNames.Any(name => name.Equals(property.Name, StringComparison.OrdinalIgnoreCase))
                || !properties.TryAdd(property.Name, property.Value))
            {
                error = ReconciliationApplicationErrors.AllocationInvalidRequest;
                return false;
            }
        }

        error = null;
        return true;
    }

    private static bool TryReadGuid(JsonElement element, out Guid value)
    {
        value = Guid.Empty;
        return element.ValueKind == JsonValueKind.String
            && Guid.TryParse(element.GetString(), out value)
            && value != Guid.Empty;
    }

    private static async Task<byte[]?> ReadBoundedBodyAsync(Stream body, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        byte[] rented = ArrayPool<byte>.Shared.Rent(1024);
        try
        {
            int total = 0;
            while (true)
            {
                int read = await body.ReadAsync(rented.AsMemory(0, rented.Length), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > MaxJsonBodyBytes)
                {
                    return null;
                }

                buffer.Write(rented, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        return buffer.ToArray();
    }

    private static bool IsJsonContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        string mediaType = contentType.Split(';', 2)[0].Trim();
        return mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase);
    }

    private static ProblemHttpResult InvalidAllocationRequest()
    {
        return Problem(
            ReconciliationApplicationErrors.AllocationInvalidRequest,
            StatusCodes.Status400BadRequest,
            "Allocation request is invalid.");
    }

    private static ProblemHttpResult UnsupportedMediaType()
    {
        return Problem(
            ReconciliationApplicationErrors.AllocationUnsupportedMediaType,
            StatusCodes.Status415UnsupportedMediaType,
            "Unsupported media type.");
    }

    private static ProblemHttpResult RequestTooLarge()
    {
        return Problem(
            ReconciliationApplicationErrors.AllocationRequestTooLarge,
            StatusCodes.Status413PayloadTooLarge,
            "Allocation request is too large.");
    }

    private static bool TryReadIdempotencyKey(
        HttpRequest httpRequest,
        out Guid idempotencyKey,
        out Error? error)
    {
        if (!httpRequest.Headers.TryGetValue("Idempotency-Key", out StringValues values)
            || values.Count == 0
            || string.IsNullOrWhiteSpace(values[0]))
        {
            idempotencyKey = Guid.Empty;
            error = ReconciliationApplicationErrors.IdempotencyKeyRequired;
            return false;
        }

        if (values.Count != 1
            || !Guid.TryParseExact(values[0], "D", out idempotencyKey)
            || idempotencyKey == Guid.Empty)
        {
            idempotencyKey = Guid.Empty;
            error = ReconciliationApplicationErrors.IdempotencyKeyInvalid;
            return false;
        }

        error = null;
        return true;
    }

    private static int StatusCodeFor(Error error)
    {
        if (error.Code == ReconciliationApplicationErrors.IdempotencyKeyRequired.Code
            || error.Code == ReconciliationApplicationErrors.IdempotencyKeyInvalid.Code
            || error.Code == ReconciliationApplicationErrors.AllocationInvalidRequest.Code
            || error.Code == ReconciliationApplicationErrors.ExplicitAmountInvalid.Code)
        {
            return StatusCodes.Status400BadRequest;
        }

        if (error.Code == ReconciliationApplicationErrors.AllocationRequestTooLarge.Code)
        {
            return StatusCodes.Status413PayloadTooLarge;
        }

        if (error.Code == ReconciliationApplicationErrors.AllocationUnsupportedMediaType.Code)
        {
            return StatusCodes.Status415UnsupportedMediaType;
        }

        if (error.Code == ReconciliationApplicationErrors.InvoiceNotFound.Code
            || error.Code == ReconciliationApplicationErrors.BankTransactionNotFound.Code)
        {
            return StatusCodes.Status404NotFound;
        }

        if (error.Code == ReconciliationErrors.CurrencyMismatch.Code
            || error.Code == ReconciliationErrors.InvoiceAllocationExceedsOutstanding.Code
            || error.Code == ReconciliationErrors.BankTransactionAllocationExceedsUnapplied.Code
            || error.Code == ReconciliationApplicationErrors.IdempotencyPayloadConflict.Code
            || error.Code == ReconciliationApplicationErrors.ConcurrencyConflict.Code)
        {
            return StatusCodes.Status409Conflict;
        }

        throw new InvalidOperationException(
            $"Unexpected allocation failure '{error.Code}'.");
    }

    private static string TitleFor(Error error)
    {
        if (error.Code == ReconciliationApplicationErrors.IdempotencyKeyRequired.Code)
        {
            return "Idempotency key is required.";
        }

        if (error.Code == ReconciliationApplicationErrors.IdempotencyKeyInvalid.Code)
        {
            return "Idempotency key is invalid.";
        }

        if (error.Code == ReconciliationApplicationErrors.AllocationInvalidRequest.Code)
        {
            return "Allocation request is invalid.";
        }

        if (error.Code == ReconciliationApplicationErrors.ExplicitAmountInvalid.Code)
        {
            return "Allocation amount is invalid.";
        }

        if (error.Code == ReconciliationApplicationErrors.AllocationRequestTooLarge.Code)
        {
            return "Allocation request is too large.";
        }

        if (error.Code == ReconciliationApplicationErrors.AllocationUnsupportedMediaType.Code)
        {
            return "Unsupported media type.";
        }

        if (error.Code == ReconciliationApplicationErrors.InvoiceNotFound.Code)
        {
            return "Invoice was not found.";
        }

        if (error.Code == ReconciliationApplicationErrors.BankTransactionNotFound.Code)
        {
            return "Bank transaction was not found.";
        }

        if (error.Code == ReconciliationErrors.CurrencyMismatch.Code)
        {
            return "Currencies do not match.";
        }

        if (error.Code == ReconciliationErrors.InvoiceAllocationExceedsOutstanding.Code)
        {
            return "Allocation exceeds the invoice outstanding amount.";
        }

        if (error.Code == ReconciliationErrors.BankTransactionAllocationExceedsUnapplied.Code)
        {
            return "Allocation exceeds the unapplied bank transaction amount.";
        }

        if (error.Code == ReconciliationApplicationErrors.IdempotencyPayloadConflict.Code)
        {
            return "Idempotency key was already used.";
        }

        if (error.Code == ReconciliationApplicationErrors.ConcurrencyConflict.Code)
        {
            return "The financial records were modified.";
        }

        throw new InvalidOperationException(
            $"Unexpected allocation failure '{error.Code}'.");
    }

    private static Task DocumentAllocationOperation(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        operation.RequestBody = new OpenApiRequestBody
        {
            Required = true,
            Description = "One invoice, one bank transaction, and one explicit amount.",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new OpenApiMediaType
                {
                    Schema = CreateAllocationRequestSchema(),
                },
            },
        };

        operation.Parameters ??= [];
        bool headerPresent = false;
        foreach (IOpenApiParameter parameter in operation.Parameters)
        {
            if (string.Equals(parameter.Name, "Idempotency-Key", StringComparison.OrdinalIgnoreCase)
                && parameter.In == ParameterLocation.Header)
            {
                headerPresent = true;
                break;
            }
        }

        if (!headerPresent)
        {
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "Idempotency-Key",
                In = ParameterLocation.Header,
                Required = true,
                Description = "Exactly one UUID in D format. An empty UUID is rejected.",
                Schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Format = "uuid",
                },
            });
        }

        return Task.CompletedTask;
    }

    private static OpenApiSchema CreateAllocationRequestSchema()
    {
        return new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            AdditionalPropertiesAllowed = false,
            Required = new HashSet<string>
            {
                "invoiceId",
                "bankTransactionId",
                "amount",
            },
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["invoiceId"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Format = "uuid",
                },
                ["bankTransactionId"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Format = "uuid",
                },
                ["amount"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    AdditionalPropertiesAllowed = false,
                    Required = new HashSet<string>
                    {
                        "amount",
                        "currency",
                    },
                    Properties = new Dictionary<string, IOpenApiSchema>
                    {
                        ["amount"] = new OpenApiSchema
                        {
                            Type = JsonSchemaType.Number,
                            Description =
                                "JSON number greater than zero and less than 1000000000000000. It must fit decimal(19,4) without rounding. Trailing zeros and exact exponent notation are allowed.",
                            ExclusiveMinimum = "0",
                            ExclusiveMaximum = "1000000000000000",
                            MultipleOf = 0.0001m,
                        },
                        ["currency"] = new OpenApiSchema
                        {
                            Type = JsonSchemaType.String,
                            Description = "Three-letter currency code.",
                            MinLength = 3,
                            MaxLength = 3,
                            Pattern = "^[A-Za-z]{3}$",
                        },
                    },
                },
            },
        };
    }

    private static ProblemHttpResult Problem(Error error, int statusCode, string title)
    {
        return TypedResults.Problem(
            detail: error.Message,
            statusCode: statusCode,
            title: title,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = error.Code,
            });
    }
}