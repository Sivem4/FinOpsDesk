using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Results;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;
using Microsoft.OpenApi;

namespace FinOpsDesk.Modules.Reconciliation.Presentation.ReviewCases;

internal static class ReviewCaseEndpoints
{
    private const int MaxJsonBodyBytes = 4096;

    private const string ConcurrencyTokenDescription =
        "Opaque Base64 encoding of the 8-byte SQL Server rowversion. "
        + "Submit the token read with the case. Do not replace it with a newer token after a conflict.";

    private const string ActorDescription =
        "The actor is taken from the authenticated server context. "
        + "The body, query, and headers do not supply an actor. "
        + "A newly executed action appends one review-case audit entry in the same save. "
        + "Replay does not append an entry and does not change the original actor, time, decision, or concurrency token.";

    private const string TenantDescription =
        "Requires the TenantMember policy. The effective OrganizationId comes from the host organization context. "
        + "A client organization id in the body, query, or header is not accepted as the tenant.";

    private static readonly string[] OpenPropertyNames =
        ["reason", "invoiceId", "bankTransactionId", "sourceLabel", "note"];

    private static readonly string[] ResolvePropertyNames =
        ["outcome", "allocationId", "resolutionNote", "concurrencyToken"];

    private static readonly string[] DismissPropertyNames =
        ["resolutionNote", "concurrencyToken"];

    private static readonly string[] ListQueryNames =
        ["status", "invoiceId", "bankTransactionId", "page", "pageSize"];

    private static readonly string[] AuditQueryNames = ["page", "pageSize"];

    private static readonly string[] CommentQueryNames = ["page", "pageSize"];

    private static readonly string[] CommentPropertyNames = ["text"];

    private static readonly string[] AssignmentPropertyNames = ["concurrencyToken"];

    private static readonly string[] DelegatePropertyNames = ["concurrencyToken", "recipientActorId"];

    public static RouteGroupBuilder MapReviewCaseEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapPost("/review-cases", OpenAsync)
            .WithName("OpenReviewCase")
            .WithSummary("Open a review case for the authenticated organization.")
            .WithDescription(
                TenantDescription
                + " A new case returns 201 and a Location header for GET /api/review-cases/{id}. "
                + "Without Idempotency-Key, an existing open case for the same subject returns 200 and does not change the stored note, source, or concurrency token. "
                + "A present Idempotency-Key must be one UUID in D format with no surrounding whitespace. The same key and the same normalized request return that case after it is resolved or dismissed. "
                + "This operation does not allocate money and does not run exact-1 or smart-1. "
                + ActorDescription)
            .Produces<ReviewCaseResponse>(StatusCodes.Status201Created, "application/json")
            .Produces<ReviewCaseResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .AddOpenApiOperationTransformer(DocumentOpenOperation)
            .WithMetadata(FinancialEndpointClassification.Write(
                "review-cases.open",
                FinancialWriteOperation.OpenReviewCase));

        group.MapGet("/review-cases", ListAsync)
            .WithName("ListReviewCases")
            .WithSummary("List review cases for the authenticated organization.")
            .WithDescription(
                TenantDescription
                + " Results are ordered by opened time descending, then review case id descending. "
                + "The list does not run exact-1 or smart-1 and does not load the open reconciliation book.")
            .Produces<ReviewCaseListResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .AddOpenApiOperationTransformer(DocumentListOperation)
            .WithMetadata(FinancialEndpointClassification.Read("review-cases.read"));

        group.MapGet("/review-cases/summary", SummaryAsync)
            .WithName("GetReviewCaseSummary")
            .WithSummary("Count persisted review cases for the authenticated organization.")
            .WithDescription(
                TenantDescription
                + " The six counters cover every persisted case in that organization. "
                + "They do not follow the list page or its filters. "
                + "A later case for a subject whose earlier case is closed counts separately. "
                + "totalCount is openCount plus resolvedCount plus dismissedCount. "
                + "openCount is openCompetingMatchCount plus openNeedsInvestigationCount. "
                + "An empty organization returns six zeros. "
                + "The counters are one SQL aggregation and can be stale after the read returns. "
                + "This route does not set an isolation level or a database option and does not guarantee a point-in-time snapshot. "
                + "The response has no actors, notes, document ids, or amounts. "
                + "Any query parameter is rejected. "
                + "This read does not change cases, audit rows, or rowversion.")
            .Produces<ReviewCaseSummaryResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .AddOpenApiOperationTransformer(DocumentSummaryOperation)
            .WithMetadata(FinancialEndpointClassification.Read("review-cases.read"));

        group.MapGet("/review-cases/delegation-recipients", GetDelegationRecipientsAsync)
            .WithName("GetReviewCaseDelegationRecipients")
            .WithSummary("List development delegation recipients for the authenticated organization.")
            .WithDescription(
                TenantDescription
                + " Requires review-cases.assignment.read. "
                + "DevelopmentJwt returns the host-configured recipient ids for the server organization. "
                + "A missing section, a missing organization, or an empty list returns no recipients. "
                + "ProductionCookie is refused before that configuration is read. "
                + "The response does not accept a client organization id.")
            .Produces<ReviewCaseDelegationRecipientsResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithMetadata(FinancialEndpointClassification.Read("review-cases.assignment.read"));

        group.MapGet("/review-cases/{id}", GetAsync)
            .WithName("GetReviewCase")
            .WithSummary("Get one review case and its current linked balances.")
            .WithDescription(
                TenantDescription
                + " Linked balances are current observations, not stored snapshots and not an authorization to allocate. "
                + "Inconsistent stored money returns 500 and does not include the corrupted amounts.")
            .Produces<ReviewCaseDetailResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .AddOpenApiOperationTransformer(DocumentGetOperation)
            .WithMetadata(FinancialEndpointClassification.Read("review-cases.read"));

        group.MapGet("/review-cases/{id}/audit", GetAuditAsync)
            .WithName("GetReviewCaseAudit")
            .WithSummary("List persisted audit entries for one review case.")
            .WithDescription(
                TenantDescription
                + " DevelopmentJwt allows any TenantMember to read ActorId. "
                + "ProductionCookie read authorization, when enabled, requires audit.read. "
                + "review-cases.read does not authorize this history. "
                + "A missing actor id is allowed on this read. "
                + "Results are ordered by RecordedAt ascending, then action ascending, then audit entry id ascending. "
                + "Page defaults to 1 and page size defaults to 50, with a maximum of 100. "
                + "An existing case with no audit rows returns an empty page. "
                + "A historical case may contain only a terminal entry. "
                + "The response does not reconstruct a missing Opened entry and does not copy current case fields, balances, or the concurrency token. "
                + "The existence check, the count, and the page are separate statements and are not one snapshot. "
                + "This read does not change the case or its audit rows.")
            .Produces<ReviewCaseAuditListResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .AddOpenApiOperationTransformer(DocumentAuditOperation)
            .WithMetadata(FinancialEndpointClassification.Read("audit.read"));

        group.MapPost("/review-cases/{id}/comments", AddCommentAsync)
            .WithName("AddReviewCaseComment")
            .WithSummary("Append an immutable comment to one review case.")
            .WithDescription(
                TenantDescription
                + " The comment can be appended while the case is Open, Resolved, or Dismissed. "
                + "It does not reopen a closed case and does not change the decision, ClosedAt, rowversion, balances, allocations, or audit rows. "
                + "text is required plain text. string.Trim() normalizes it, and the stored value must then contain 1 to 500 UTF-16 code units. "
                + "U+FEFF is preserved. HTML is not interpreted. "
                + "Idempotency-Key is required and must be one non-empty UUID in D format with no surrounding whitespace. "
                + "The same key, case, and normalized text return the original comment. "
                + "A different case or text for that key conflicts. "
                + "The same text with a new key can append another comment. "
                + "Organization, actor, and time come from the server. "
                + "A missing or invalid actor is rejected before replay. "
                + "There is no single-comment route and the response has no Location header.")
            .Produces<ReviewCaseCommentResponse>(StatusCodes.Status201Created, "application/json")
            .Produces<ReviewCaseCommentResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .AddOpenApiOperationTransformer(DocumentCommentOperation)
            .WithMetadata(FinancialEndpointClassification.Write(
                "review-cases.comment",
                FinancialWriteOperation.CommentReviewCase));

        group.MapGet("/review-cases/{id}/comments", GetCommentsAsync)
            .WithName("GetReviewCaseComments")
            .WithSummary("List immutable comments for one review case.")
            .WithDescription(
                TenantDescription
                + " DevelopmentJwt allows any TenantMember to read ActorId. "
                + "ProductionCookie read authorization, when enabled, requires audit.read. "
                + "review-cases.read does not authorize this list. "
                + "Results are ordered by RecordedAt ascending, then comment id ascending. "
                + "Page defaults to 1 and page size defaults to 50, with a maximum of 100. "
                + "totalCount is a 64-bit count. "
                + "An existing case with no comments returns an empty page. "
                + "The existence check, the count, and the page are separate statements and are not one snapshot. "
                + "This read does not change the case, the comments, or rowversion.")
            .Produces<ReviewCaseCommentListResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .AddOpenApiOperationTransformer(DocumentCommentsOperation)
            .WithMetadata(FinancialEndpointClassification.Read("audit.read"));

        group.MapPost("/review-cases/{id}/assignment", ClaimAssignmentAsync)
            .WithName("ClaimReviewCaseAssignment")
            .WithSummary("Claim an unassigned open review case for the current actor.")
            .WithDescription(
                TenantDescription
                + " The body contains only concurrencyToken. "
                + "Idempotency-Key is required and must be one non-empty UUID in D format with no surrounding whitespace. "
                + "A new claim returns HTTP 201 and no Location header. "
                + "The same key and the same operation, case, actor source, actor id, and canonical token return HTTP 200 with the historical result. "
                + "The response does not include the resulting concurrency token. "
                + "The actor and organization come from the server. A missing or invalid actor is rejected before the key is read. "
                + "Resolve and dismiss do not require or clear an assignment.")
            .Produces<ReviewCaseAssignmentResponse>(StatusCodes.Status201Created, "application/json")
            .Produces<ReviewCaseAssignmentResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .AddOpenApiOperationTransformer(DocumentAssignmentOperation)
            .WithMetadata(FinancialEndpointClassification.Write(
                "review-cases.assign",
                FinancialWriteOperation.ClaimReviewCase));

        group.MapPost("/review-cases/{id}/assignment/release", ReleaseAssignmentAsync)
            .WithName("ReleaseReviewCaseAssignment")
            .WithSummary("Release the current actor's assignment of an open review case.")
            .WithDescription(
                TenantDescription
                + " Only the actor who holds the assignment can release it. "
                + "The body contains only concurrencyToken. "
                + "Idempotency-Key is required and shares the claim key namespace. "
                + "A new release returns HTTP 201 and no Location header. "
                + "A matching historical release returns HTTP 200 and does not restore a later assignment. "
                + "The response does not name another assignee.")
            .Produces<ReviewCaseAssignmentResponse>(StatusCodes.Status201Created, "application/json")
            .Produces<ReviewCaseAssignmentResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .AddOpenApiOperationTransformer(DocumentAssignmentOperation)
            .WithMetadata(FinancialEndpointClassification.Write(
                "review-cases.assign",
                FinancialWriteOperation.ReleaseReviewCase));

        group.MapPost("/review-cases/{id}/assignment/delegate", DelegateAssignmentAsync)
            .WithName("DelegateReviewCaseAssignment")
            .WithSummary("Delegate an open review case from the current assignee to another recipient.")
            .WithDescription(
                TenantDescription
                + " Only the current assignee of an open case can delegate it. "
                + "The closed body contains concurrencyToken and recipientActorId. "
                + "The recipient is trimmed, must differ from the sender, and is checked against the current organization list. "
                + "A missing list, a foreign recipient, and an id that is not listed stop a new command. "
                + "A matching historical delegation returns HTTP 200 after the recipient leaves the list. "
                + "A new delegation returns HTTP 201 without Location and without the resulting concurrency token. "
                + "The response records the sender, the recipient, and the delegation time.")
            .Produces<ReviewCaseDelegationResponse>(StatusCodes.Status201Created, "application/json")
            .Produces<ReviewCaseDelegationResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .AddOpenApiOperationTransformer(DocumentDelegateOperation)
            .WithMetadata(FinancialEndpointClassification.Write(
                "review-cases.assign",
                FinancialWriteOperation.DelegateReviewCase));

        group.MapGet("/review-cases/{id}/assignment", GetAssignmentAsync)
            .WithName("GetReviewCaseAssignment")
            .WithSummary("Read the current assignment of one review case.")
            .WithDescription(
                TenantDescription
                + " Requires review-cases.assignment.read. review-cases.read does not authorize this route and does not reveal the assignee. "
                + "DevelopmentJwt does not distinguish organization roles. "
                + "An unassigned case returns assignee null and the current concurrency token. "
                + "Unknown and foreign cases share HTTP 404 reconciliation.review_case.not_found. "
                + "This read does not change the case. Contract v2 does not advertise this permission.")
            .Produces<ReviewCaseAssignmentStateResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .AddOpenApiOperationTransformer(DocumentAssignmentReadOperation)
            .WithMetadata(FinancialEndpointClassification.Read("review-cases.assignment.read"));

        group.MapPost("/review-cases/{id}/resolve", ResolveAsync)
            .WithName("ResolveReviewCase")
            .WithSummary("Resolve an open review case.")
            .WithDescription(
                TenantDescription
                + " AllocationRecorded requires an existing confirmed allocation. "
                + "NoActionRequired and HandledOutside reject an allocation id. "
                + "An identical terminal replay returns 200 and preserves ClosedAt and the concurrency token. "
                + "This operation does not confirm an allocation. "
                + ActorDescription)
            .Produces<ReviewCaseResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .AddOpenApiOperationTransformer(DocumentResolveOperation)
            .WithMetadata(FinancialEndpointClassification.Write(
                "review-cases.resolve",
                FinancialWriteOperation.ResolveReviewCase));

        group.MapPost("/review-cases/{id}/dismiss", DismissAsync)
            .WithName("DismissReviewCase")
            .WithSummary("Dismiss an open review case.")
            .WithDescription(
                TenantDescription
                + " Outcome and allocationId are not accepted. "
                + "An identical dismissal replay returns 200 and preserves ClosedAt and the concurrency token. "
                + "Dismissal does not change invoice, bank transaction, or allocation rows. "
                + ActorDescription)
            .Produces<ReviewCaseResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .AddOpenApiOperationTransformer(DocumentDismissOperation)
            .WithMetadata(FinancialEndpointClassification.Write(
                "review-cases.resolve",
                FinancialWriteOperation.DismissReviewCase));

        return group;
    }

    private static async Task<Results<Created<ReviewCaseResponse>, Ok<ReviewCaseResponse>, ProblemHttpResult>> OpenAsync(
        HttpRequest httpRequest,
        OpenReviewCaseHandler handler,
        CancellationToken cancellationToken)
    {
        if (RejectUnexpectedQuery(httpRequest, []) is ProblemHttpResult queryProblem)
        {
            return queryProblem;
        }

        if (!TryReadIdempotencyKey(httpRequest, out Guid? idempotencyKey, out ProblemHttpResult? keyProblem))
        {
            return keyProblem!;
        }

        JsonRead read = await TryReadJsonObjectAsync(httpRequest, OpenPropertyNames, cancellationToken);
        if (read.Problem is not null)
        {
            return read.Problem;
        }

        using (read.Document)
        {
            if (!TryReadOpen(
                    read.Properties!,
                    idempotencyKey,
                    out OpenReviewCaseCommand? command,
                    out ProblemHttpResult? requestProblem))
            {
                return requestProblem!;
            }

            Result<OpenReviewCaseResponse> result = await handler.Handle(command!, cancellationToken);
            if (result.IsFailure)
            {
                return Problem(result.Error);
            }

            ReviewCaseResponse reviewCase = result.Value.ReviewCase;
            if (result.Value.Created)
            {
                return TypedResults.Created($"/api/review-cases/{reviewCase.ReviewCaseId:D}", reviewCase);
            }

            return TypedResults.Ok(reviewCase);
        }
    }

    private static async Task<Results<Ok<ReviewCaseListResponse>, ProblemHttpResult>> ListAsync(
        HttpRequest httpRequest,
        ListReviewCasesHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryBindList(httpRequest, out ListReviewCasesQuery? query, out ProblemHttpResult? problem))
        {
            return problem!;
        }

        Result<ReviewCaseListResponse> result = await handler.Handle(query!, cancellationToken);
        if (result.IsFailure)
        {
            return Problem(result.Error);
        }

        return TypedResults.Ok(result.Value);
    }

    private static async Task<Results<Ok<ReviewCaseSummaryResponse>, ProblemHttpResult>> SummaryAsync(
        HttpRequest httpRequest,
        GetReviewCaseSummaryHandler handler,
        CancellationToken cancellationToken)
    {
        if (RejectUnexpectedQuery(httpRequest, []) is ProblemHttpResult queryProblem)
        {
            return queryProblem;
        }

        Result<ReviewCaseSummaryResponse> result = await handler.Handle(cancellationToken);
        if (result.IsFailure)
        {
            return Problem(result.Error);
        }

        return TypedResults.Ok(result.Value);
    }

    private static async Task<Results<Ok<ReviewCaseDetailResponse>, ProblemHttpResult>> GetAsync(
        string id,
        HttpRequest httpRequest,
        GetReviewCaseHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryReadRouteId(id, out Guid reviewCaseId, out ProblemHttpResult? idProblem))
        {
            return idProblem!;
        }

        if (RejectUnexpectedQuery(httpRequest, []) is ProblemHttpResult queryProblem)
        {
            return queryProblem;
        }

        Result<ReviewCaseDetailResponse> result = await handler.Handle(
            new GetReviewCaseQuery(new ReviewCaseId(reviewCaseId)),
            cancellationToken);
        if (result.IsFailure)
        {
            return Problem(result.Error);
        }

        return TypedResults.Ok(result.Value);
    }

    private static async Task<Results<Ok<ReviewCaseAuditListResponse>, ProblemHttpResult>> GetAuditAsync(
        string id,
        HttpRequest httpRequest,
        GetReviewCaseAuditHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryReadAuditRouteId(id, out Guid reviewCaseId, out ProblemHttpResult? idProblem))
        {
            return idProblem!;
        }

        if (!TryBindAuditQuery(httpRequest, out int page, out int pageSize, out ProblemHttpResult? queryProblem))
        {
            return queryProblem!;
        }

        Result<ReviewCaseAuditListResponse> result = await handler.Handle(
            new GetReviewCaseAuditQuery(new ReviewCaseId(reviewCaseId), page, pageSize),
            cancellationToken);
        if (result.IsFailure)
        {
            return Problem(result.Error);
        }

        return TypedResults.Ok(result.Value);
    }

    private static async Task<Results<JsonHttpResult<ReviewCaseCommentResponse>, Ok<ReviewCaseCommentResponse>, ProblemHttpResult>> AddCommentAsync(
        string id,
        HttpRequest httpRequest,
        AddReviewCaseCommentHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryReadCommentRouteId(id, out Guid reviewCaseId, out ProblemHttpResult? idProblem))
        {
            return idProblem!;
        }

        if (RejectUnexpectedCommentQuery(httpRequest, []) is ProblemHttpResult queryProblem)
        {
            return queryProblem;
        }

        if (!TryReadRequiredCommentIdempotencyKey(httpRequest, out Guid idempotencyKey, out ProblemHttpResult? keyProblem))
        {
            return keyProblem!;
        }

        JsonRead read = await TryReadJsonObjectAsync(
            httpRequest,
            CommentPropertyNames,
            cancellationToken,
            ReconciliationApplicationErrors.ReviewCaseCommentInvalidRequest);
        if (read.Problem is not null)
        {
            return read.Problem;
        }

        using (read.Document)
        {
            if (!TryReadCommentText(read.Properties!, out string? text))
            {
                return Problem(ReconciliationApplicationErrors.ReviewCaseCommentInvalidRequest);
            }

            Result<AddReviewCaseCommentResponse> result = await handler.Handle(
                new AddReviewCaseCommentCommand(new ReviewCaseId(reviewCaseId), text, idempotencyKey),
                cancellationToken);
            if (result.IsFailure)
            {
                return Problem(result.Error);
            }

            return result.Value.Created
                ? TypedResults.Json(result.Value.Comment, statusCode: StatusCodes.Status201Created)
                : TypedResults.Ok(result.Value.Comment);
        }
    }

    private static async Task<Results<Ok<ReviewCaseCommentListResponse>, ProblemHttpResult>> GetCommentsAsync(
        string id,
        HttpRequest httpRequest,
        GetReviewCaseCommentsHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryReadCommentRouteId(id, out Guid reviewCaseId, out ProblemHttpResult? idProblem))
        {
            return idProblem!;
        }

        if (!TryBindCommentQuery(httpRequest, out int page, out int pageSize, out ProblemHttpResult? queryProblem))
        {
            return queryProblem!;
        }

        Result<ReviewCaseCommentListResponse> result = await handler.Handle(
            new GetReviewCaseCommentsQuery(new ReviewCaseId(reviewCaseId), page, pageSize),
            cancellationToken);
        if (result.IsFailure)
        {
            return Problem(result.Error);
        }

        return TypedResults.Ok(result.Value);
    }

    private static async Task<Results<JsonHttpResult<ReviewCaseAssignmentResponse>, Ok<ReviewCaseAssignmentResponse>, ProblemHttpResult>> ClaimAssignmentAsync(
        string id,
        HttpRequest httpRequest,
        ClaimReviewCaseHandler handler,
        CancellationToken cancellationToken)
    {
        return await ChangeAssignmentAsync(
            id,
            httpRequest,
            async (reviewCaseId, rowVersion, canonicalToken, idempotencyKey, token) =>
                await handler.Handle(
                    new ClaimReviewCaseCommand(new ReviewCaseId(reviewCaseId), rowVersion, canonicalToken, idempotencyKey),
                    token),
            cancellationToken);
    }

    private static async Task<Results<JsonHttpResult<ReviewCaseAssignmentResponse>, Ok<ReviewCaseAssignmentResponse>, ProblemHttpResult>> ReleaseAssignmentAsync(
        string id,
        HttpRequest httpRequest,
        ReleaseReviewCaseHandler handler,
        CancellationToken cancellationToken)
    {
        return await ChangeAssignmentAsync(
            id,
            httpRequest,
            async (reviewCaseId, rowVersion, canonicalToken, idempotencyKey, token) =>
                await handler.Handle(
                    new ReleaseReviewCaseCommand(new ReviewCaseId(reviewCaseId), rowVersion, canonicalToken, idempotencyKey),
                    token),
            cancellationToken);
    }

    private static async Task<Results<JsonHttpResult<ReviewCaseDelegationResponse>, Ok<ReviewCaseDelegationResponse>, ProblemHttpResult>> DelegateAssignmentAsync(
        string id,
        HttpRequest httpRequest,
        DelegateReviewCaseHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryReadAssignmentRouteId(id, out Guid reviewCaseId, out ProblemHttpResult? idProblem))
        {
            return idProblem!;
        }

        if (RejectUnexpectedAssignmentQuery(httpRequest) is ProblemHttpResult queryProblem)
        {
            return queryProblem;
        }

        if (!TryReadRequiredAssignmentIdempotencyKey(httpRequest, out Guid idempotencyKey, out ProblemHttpResult? keyProblem))
        {
            return keyProblem!;
        }

        JsonRead read = await TryReadJsonObjectAsync(
            httpRequest,
            DelegatePropertyNames,
            cancellationToken,
            ReconciliationApplicationErrors.ReviewCaseAssignmentInvalidRequest);
        if (read.Problem is not null)
        {
            return read.Problem;
        }

        using (read.Document)
        {
            if (!TryReadAssignmentToken(read.Properties!, out byte[]? rowVersion, out string? canonicalToken)
                || !TryReadRecipient(read.Properties!, out string? recipientActorId))
            {
                return Problem(ReconciliationApplicationErrors.ReviewCaseAssignmentInvalidRequest);
            }

            Result<ChangeReviewCaseDelegationResponse> result = await handler.Handle(
                new DelegateReviewCaseCommand(
                    new ReviewCaseId(reviewCaseId),
                    rowVersion!,
                    canonicalToken!,
                    idempotencyKey,
                    recipientActorId!),
                cancellationToken);
            if (result.IsFailure)
            {
                return Problem(result.Error);
            }

            return result.Value.Created
                ? TypedResults.Json(result.Value.Delegation, statusCode: StatusCodes.Status201Created)
                : TypedResults.Ok(result.Value.Delegation);
        }
    }

    private static async Task<Results<Ok<ReviewCaseDelegationRecipientsResponse>, ProblemHttpResult>> GetDelegationRecipientsAsync(
        HttpRequest httpRequest,
        GetReviewCaseDelegationRecipientsHandler handler,
        CancellationToken cancellationToken)
    {
        if (RejectUnexpectedAssignmentQuery(httpRequest) is ProblemHttpResult queryProblem)
        {
            return queryProblem;
        }

        Result<ReviewCaseDelegationRecipientsResponse> result = await handler.Handle(
            new GetReviewCaseDelegationRecipientsQuery(),
            cancellationToken);
        if (result.IsFailure)
        {
            return Problem(result.Error);
        }

        return TypedResults.Ok(result.Value);
    }

    private static bool TryReadRecipient(Dictionary<string, JsonElement> properties, out string? recipientActorId)
    {
        recipientActorId = null;
        if (!properties.TryGetValue("recipientActorId", out JsonElement element)
            || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string? value = element.GetString();
        if (value is null)
        {
            return false;
        }

        string trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Length > ReviewCaseAssignmentEntry.MaxActorIdLength)
        {
            return false;
        }

        recipientActorId = trimmed;
        return true;
    }

    private static async Task<Results<JsonHttpResult<ReviewCaseAssignmentResponse>, Ok<ReviewCaseAssignmentResponse>, ProblemHttpResult>> ChangeAssignmentAsync(
        string id,
        HttpRequest httpRequest,
        Func<Guid, byte[], string, Guid, CancellationToken, Task<Result<ChangeReviewCaseAssignmentResponse>>> execute,
        CancellationToken cancellationToken)
    {
        if (!TryReadAssignmentRouteId(id, out Guid reviewCaseId, out ProblemHttpResult? idProblem))
        {
            return idProblem!;
        }

        if (RejectUnexpectedAssignmentQuery(httpRequest) is ProblemHttpResult queryProblem)
        {
            return queryProblem;
        }

        if (!TryReadRequiredAssignmentIdempotencyKey(httpRequest, out Guid idempotencyKey, out ProblemHttpResult? keyProblem))
        {
            return keyProblem!;
        }

        JsonRead read = await TryReadJsonObjectAsync(
            httpRequest,
            AssignmentPropertyNames,
            cancellationToken,
            ReconciliationApplicationErrors.ReviewCaseAssignmentInvalidRequest);
        if (read.Problem is not null)
        {
            return read.Problem;
        }

        using (read.Document)
        {
            if (!TryReadAssignmentToken(read.Properties!, out byte[]? rowVersion, out string? canonicalToken))
            {
                return Problem(ReconciliationApplicationErrors.ReviewCaseAssignmentInvalidRequest);
            }

            Result<ChangeReviewCaseAssignmentResponse> result = await execute(
                reviewCaseId,
                rowVersion!,
                canonicalToken!,
                idempotencyKey,
                cancellationToken);
            if (result.IsFailure)
            {
                return Problem(result.Error);
            }

            return result.Value.Created
                ? TypedResults.Json(result.Value.Assignment, statusCode: StatusCodes.Status201Created)
                : TypedResults.Ok(result.Value.Assignment);
        }
    }

    private static async Task<Results<Ok<ReviewCaseAssignmentStateResponse>, ProblemHttpResult>> GetAssignmentAsync(
        string id,
        HttpRequest httpRequest,
        GetReviewCaseAssignmentHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryReadAssignmentRouteId(id, out Guid reviewCaseId, out ProblemHttpResult? idProblem))
        {
            return idProblem!;
        }

        if (RejectUnexpectedAssignmentQuery(httpRequest) is ProblemHttpResult queryProblem)
        {
            return queryProblem;
        }

        Result<ReviewCaseAssignmentStateResponse> result = await handler.Handle(
            new GetReviewCaseAssignmentQuery(new ReviewCaseId(reviewCaseId)),
            cancellationToken);
        if (result.IsFailure)
        {
            return Problem(result.Error);
        }

        return TypedResults.Ok(result.Value);
    }

    private static async Task<Results<Ok<ReviewCaseResponse>, ProblemHttpResult>> ResolveAsync(
        string id,
        HttpRequest httpRequest,
        ResolveReviewCaseHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryReadRouteId(id, out Guid reviewCaseId, out ProblemHttpResult? idProblem))
        {
            return idProblem!;
        }

        if (RejectUnexpectedQuery(httpRequest, []) is ProblemHttpResult queryProblem)
        {
            return queryProblem;
        }

        JsonRead read = await TryReadJsonObjectAsync(httpRequest, ResolvePropertyNames, cancellationToken);
        if (read.Problem is not null)
        {
            return read.Problem;
        }

        using (read.Document)
        {
            if (!TryReadResolve(
                    read.Properties!,
                    reviewCaseId,
                    out ResolveReviewCaseCommand? command,
                    out ProblemHttpResult? requestProblem))
            {
                return requestProblem!;
            }

            Result<ReviewCaseResponse> result = await handler.Handle(command!, cancellationToken);
            return result.IsFailure ? Problem(result.Error) : TypedResults.Ok(result.Value);
        }
    }

    private static async Task<Results<Ok<ReviewCaseResponse>, ProblemHttpResult>> DismissAsync(
        string id,
        HttpRequest httpRequest,
        DismissReviewCaseHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryReadRouteId(id, out Guid reviewCaseId, out ProblemHttpResult? idProblem))
        {
            return idProblem!;
        }

        if (RejectUnexpectedQuery(httpRequest, []) is ProblemHttpResult queryProblem)
        {
            return queryProblem;
        }

        JsonRead read = await TryReadJsonObjectAsync(httpRequest, DismissPropertyNames, cancellationToken);
        if (read.Problem is not null)
        {
            return read.Problem;
        }

        using (read.Document)
        {
            if (!TryReadDismiss(
                    read.Properties!,
                    reviewCaseId,
                    out DismissReviewCaseCommand? command,
                    out ProblemHttpResult? requestProblem))
            {
                return requestProblem!;
            }

            Result<ReviewCaseResponse> result = await handler.Handle(command!, cancellationToken);
            return result.IsFailure ? Problem(result.Error) : TypedResults.Ok(result.Value);
        }
    }

    private static async Task<JsonRead> TryReadJsonObjectAsync(
        HttpRequest httpRequest,
        string[] allowedNames,
        CancellationToken cancellationToken,
        Error? invalidRequest = null)
    {
        Error invalid = invalidRequest ?? ReconciliationApplicationErrors.ReviewCaseInvalidRequest;
        if (!IsJsonContentType(httpRequest.ContentType))
        {
            return JsonRead.Failed(Problem(ReconciliationApplicationErrors.ReviewCaseUnsupportedMediaType));
        }

        byte[]? body = await ReadBoundedBodyAsync(httpRequest.Body, cancellationToken);
        if (body is null)
        {
            return JsonRead.Failed(Problem(ReconciliationApplicationErrors.ReviewCaseRequestTooLarge));
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException exception) when (exception.InnerException is not OperationCanceledException)
        {
            return JsonRead.Failed(Problem(invalid));
        }

        if (!TryReadProperties(document.RootElement, allowedNames, out Dictionary<string, JsonElement>? properties))
        {
            document.Dispose();
            return JsonRead.Failed(Problem(invalid));
        }

        return JsonRead.Succeeded(document, properties!);
    }

    private static bool TryReadRequiredCommentIdempotencyKey(
        HttpRequest httpRequest,
        out Guid idempotencyKey,
        out ProblemHttpResult? problem)
    {
        idempotencyKey = Guid.Empty;
        problem = null;
        if (!httpRequest.Headers.TryGetValue("Idempotency-Key", out StringValues values))
        {
            problem = Problem(ReconciliationApplicationErrors.ReviewCaseCommentIdempotencyKeyRequired);
            return false;
        }

        if (values.Count != 1 || !IsExactIdempotencyKey(values[0], out idempotencyKey))
        {
            problem = Problem(ReconciliationApplicationErrors.ReviewCaseCommentIdempotencyKeyInvalid);
            return false;
        }

        return true;
    }

    private static bool TryReadRequiredAssignmentIdempotencyKey(
        HttpRequest httpRequest,
        out Guid idempotencyKey,
        out ProblemHttpResult? problem)
    {
        idempotencyKey = Guid.Empty;
        problem = null;
        if (!httpRequest.Headers.TryGetValue("Idempotency-Key", out StringValues values))
        {
            problem = Problem(ReconciliationApplicationErrors.ReviewCaseAssignmentIdempotencyKeyRequired);
            return false;
        }

        if (values.Count != 1 || !IsExactIdempotencyKey(values[0], out idempotencyKey))
        {
            problem = Problem(ReconciliationApplicationErrors.ReviewCaseAssignmentIdempotencyKeyInvalid);
            return false;
        }

        return true;
    }

    private static bool TryReadAssignmentRouteId(string id, out Guid reviewCaseId, out ProblemHttpResult? problem)
    {
        if (!Guid.TryParse(id, out reviewCaseId) || reviewCaseId == Guid.Empty)
        {
            problem = Problem(ReconciliationApplicationErrors.ReviewCaseAssignmentInvalidRequest);
            return false;
        }

        problem = null;
        return true;
    }

    private static ProblemHttpResult? RejectUnexpectedAssignmentQuery(HttpRequest httpRequest)
    {
        if (httpRequest.Query.Count == 0)
        {
            return null;
        }

        return Problem(ReconciliationApplicationErrors.ReviewCaseAssignmentInvalidRequest);
    }

    private static bool TryReadAssignmentToken(
        Dictionary<string, JsonElement> properties,
        out byte[]? rowVersion,
        out string? canonicalToken)
    {
        rowVersion = null;
        canonicalToken = null;
        if (!properties.TryGetValue("concurrencyToken", out JsonElement element)
            || element.ValueKind != JsonValueKind.String
            || !ReviewCaseConcurrencyToken.TryDecode(element.GetString(), out byte[] decoded))
        {
            return false;
        }

        rowVersion = decoded;
        canonicalToken = ReviewCaseConcurrencyToken.Encode(decoded);
        return canonicalToken.Length == ReviewCaseAssignmentEntry.CanonicalTokenLength;
    }

    private static bool TryReadCommentText(Dictionary<string, JsonElement> properties, out string? text)
    {
        text = null;
        if (!properties.TryGetValue("text", out JsonElement element)
            || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        text = element.GetString();
        return text is not null;
    }

    private static bool TryReadCommentRouteId(string id, out Guid reviewCaseId, out ProblemHttpResult? problem)
    {
        if (!Guid.TryParse(id, out reviewCaseId) || reviewCaseId == Guid.Empty)
        {
            problem = Problem(ReconciliationApplicationErrors.ReviewCaseCommentInvalidRequest);
            return false;
        }

        problem = null;
        return true;
    }

    private static bool TryBindCommentQuery(
        HttpRequest httpRequest,
        out int page,
        out int pageSize,
        out ProblemHttpResult? problem)
    {
        page = ReviewCaseCommentListPagination.DefaultPage;
        pageSize = ReviewCaseCommentListPagination.DefaultPageSize;
        problem = RejectUnexpectedCommentQuery(httpRequest, CommentQueryNames);
        if (problem is not null)
        {
            return false;
        }

        if (!TryReadCommentPage(
                httpRequest,
                "page",
                ReviewCaseCommentListPagination.DefaultPage,
                out page,
                out problem)
            || !TryReadCommentPage(
                httpRequest,
                "pageSize",
                ReviewCaseCommentListPagination.DefaultPageSize,
                out pageSize,
                out problem))
        {
            return false;
        }

        return true;
    }

    private static ProblemHttpResult? RejectUnexpectedCommentQuery(HttpRequest httpRequest, string[] allowedNames)
    {
        foreach (string key in httpRequest.Query.Keys)
        {
            if (!allowedNames.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                return Problem(ReconciliationApplicationErrors.ReviewCaseCommentInvalidRequest);
            }

            if (httpRequest.Query[key].Count != 1)
            {
                return Problem(ReconciliationApplicationErrors.ReviewCaseCommentInvalidPagination);
            }
        }

        return null;
    }

    private static bool TryReadCommentPage(
        HttpRequest httpRequest,
        string name,
        int defaultValue,
        out int page,
        out ProblemHttpResult? problem)
    {
        page = defaultValue;
        problem = null;
        if (!httpRequest.Query.TryGetValue(name, out StringValues values))
        {
            return true;
        }

        if (values.Count != 1
            || !int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out page))
        {
            problem = Problem(ReconciliationApplicationErrors.ReviewCaseCommentInvalidPagination);
            return false;
        }

        return true;
    }

    private static bool TryReadIdempotencyKey(
        HttpRequest httpRequest,
        out Guid? idempotencyKey,
        out ProblemHttpResult? problem)
    {
        idempotencyKey = null;
        problem = null;
        if (!httpRequest.Headers.TryGetValue("Idempotency-Key", out StringValues values))
        {
            return true;
        }

        if (values.Count != 1 || !IsExactIdempotencyKey(values[0], out Guid key))
        {
            problem = Problem(ReconciliationApplicationErrors.ReviewCaseIdempotencyKeyInvalid);
            return false;
        }

        idempotencyKey = key;
        return true;
    }

    // Guid.TryParseExact trims Char.IsWhiteSpace in .NET 10. The raw value is rejected first.
    private static bool IsExactIdempotencyKey(string? value, out Guid key)
    {
        key = Guid.Empty;
        if (string.IsNullOrEmpty(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1]))
        {
            return false;
        }

        return Guid.TryParseExact(value, "D", out key) && key != Guid.Empty;
    }

    private static bool TryReadOpen(
        Dictionary<string, JsonElement> properties,
        Guid? idempotencyKey,
        out OpenReviewCaseCommand? command,
        out ProblemHttpResult? problem)
    {
        command = null;
        problem = null;
        if (!TryReadRequiredString(properties, "reason", out string? reasonText)
            || !TryParseReason(reasonText, out ReviewCaseReason reason)
            || !TryReadOptionalGuid(properties, "invoiceId", out Guid? invoiceId)
            || !TryReadOptionalGuid(properties, "bankTransactionId", out Guid? bankTransactionId)
            || !TryReadOptionalString(properties, "sourceLabel", out string? sourceLabel)
            || !TryReadOptionalString(properties, "note", out string? note))
        {
            problem = Problem(ReconciliationApplicationErrors.ReviewCaseInvalidRequest);
            return false;
        }

        command = new OpenReviewCaseCommand(
            reason,
            invoiceId is null ? null : new InvoiceId(invoiceId.Value),
            bankTransactionId is null ? null : new BankTransactionId(bankTransactionId.Value),
            sourceLabel,
            note,
            idempotencyKey);
        return true;
    }

    private static bool TryReadResolve(
        Dictionary<string, JsonElement> properties,
        Guid reviewCaseId,
        out ResolveReviewCaseCommand? command,
        out ProblemHttpResult? problem)
    {
        command = null;
        problem = null;
        if (!TryReadConcurrencyToken(properties, out string? concurrencyToken, out ProblemHttpResult? tokenProblem))
        {
            problem = tokenProblem;
            return false;
        }

        if (!TryReadRequiredString(properties, "outcome", out string? outcomeText)
            || !TryParseOutcome(outcomeText, out ReviewCaseOutcome outcome)
            || !TryReadOptionalGuid(properties, "allocationId", out Guid? allocationId)
            || !TryReadOptionalString(properties, "resolutionNote", out string? resolutionNote))
        {
            problem = Problem(ReconciliationApplicationErrors.ReviewCaseInvalidRequest);
            return false;
        }

        command = new ResolveReviewCaseCommand(
            new ReviewCaseId(reviewCaseId),
            outcome,
            allocationId is null ? null : new AllocationId(allocationId.Value),
            resolutionNote,
            concurrencyToken!);
        return true;
    }

    private static bool TryReadDismiss(
        Dictionary<string, JsonElement> properties,
        Guid reviewCaseId,
        out DismissReviewCaseCommand? command,
        out ProblemHttpResult? problem)
    {
        command = null;
        problem = null;
        if (!TryReadConcurrencyToken(properties, out string? concurrencyToken, out ProblemHttpResult? tokenProblem))
        {
            problem = tokenProblem;
            return false;
        }

        if (!TryReadOptionalString(properties, "resolutionNote", out string? resolutionNote))
        {
            problem = Problem(ReconciliationApplicationErrors.ReviewCaseInvalidRequest);
            return false;
        }

        command = new DismissReviewCaseCommand(
            new ReviewCaseId(reviewCaseId),
            resolutionNote,
            concurrencyToken!);
        return true;
    }

    private static bool TryBindList(
        HttpRequest httpRequest,
        out ListReviewCasesQuery? query,
        out ProblemHttpResult? problem)
    {
        query = null;
        problem = RejectUnexpectedQuery(httpRequest, ListQueryNames);
        if (problem is not null)
        {
            return false;
        }

        if (!TryReadStatus(httpRequest, out ReviewCaseStatus? status, out problem)
            || !TryReadQueryGuid(httpRequest, "invoiceId", out Guid? invoiceId, out problem)
            || !TryReadQueryGuid(httpRequest, "bankTransactionId", out Guid? bankTransactionId, out problem)
            || !TryReadPage(httpRequest, "page", ReviewCaseListPagination.DefaultPage, out int page, out problem)
            || !TryReadPage(httpRequest, "pageSize", ReviewCaseListPagination.DefaultPageSize, out int pageSize, out problem))
        {
            return false;
        }

        query = new ListReviewCasesQuery(
            status,
            invoiceId is null ? null : new InvoiceId(invoiceId.Value),
            bankTransactionId is null ? null : new BankTransactionId(bankTransactionId.Value),
            page,
            pageSize);
        return true;
    }

    private static bool TryReadRouteId(string id, out Guid reviewCaseId, out ProblemHttpResult? problem)
    {
        if (!Guid.TryParse(id, out reviewCaseId) || reviewCaseId == Guid.Empty)
        {
            problem = Problem(ReconciliationApplicationErrors.ReviewCaseInvalidRequest);
            return false;
        }

        problem = null;
        return true;
    }

    private static bool TryReadAuditRouteId(string id, out Guid reviewCaseId, out ProblemHttpResult? problem)
    {
        if (!Guid.TryParse(id, out reviewCaseId) || reviewCaseId == Guid.Empty)
        {
            problem = Problem(ReconciliationApplicationErrors.ReviewCaseAuditInvalidRequest);
            return false;
        }

        problem = null;
        return true;
    }

    private static bool TryBindAuditQuery(
        HttpRequest httpRequest,
        out int page,
        out int pageSize,
        out ProblemHttpResult? problem)
    {
        page = ReviewCaseAuditListPagination.DefaultPage;
        pageSize = ReviewCaseAuditListPagination.DefaultPageSize;
        problem = RejectUnexpectedAuditQuery(httpRequest);
        if (problem is not null)
        {
            return false;
        }

        if (!TryReadAuditPage(
                httpRequest,
                "page",
                ReviewCaseAuditListPagination.DefaultPage,
                out page,
                out problem)
            || !TryReadAuditPage(
                httpRequest,
                "pageSize",
                ReviewCaseAuditListPagination.DefaultPageSize,
                out pageSize,
                out problem))
        {
            return false;
        }

        return true;
    }

    private static ProblemHttpResult? RejectUnexpectedAuditQuery(HttpRequest httpRequest)
    {
        foreach (string key in httpRequest.Query.Keys)
        {
            if (!AuditQueryNames.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                return Problem(ReconciliationApplicationErrors.ReviewCaseAuditInvalidRequest);
            }

            if (httpRequest.Query[key].Count != 1)
            {
                return Problem(ReconciliationApplicationErrors.ReviewCaseAuditInvalidPagination);
            }
        }

        return null;
    }

    private static bool TryReadAuditPage(
        HttpRequest httpRequest,
        string name,
        int defaultValue,
        out int page,
        out ProblemHttpResult? problem)
    {
        page = defaultValue;
        problem = null;
        if (!httpRequest.Query.TryGetValue(name, out StringValues values))
        {
            return true;
        }

        if (values.Count != 1
            || !int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out page))
        {
            problem = Problem(ReconciliationApplicationErrors.ReviewCaseAuditInvalidPagination);
            return false;
        }

        return true;
    }

    private static ProblemHttpResult? RejectUnexpectedQuery(HttpRequest httpRequest, string[] allowedNames)
    {
        foreach (string key in httpRequest.Query.Keys)
        {
            if (!allowedNames.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                return Problem(ReconciliationApplicationErrors.ReviewCaseInvalidRequest);
            }

            if (httpRequest.Query[key].Count != 1)
            {
                return key.Equals("page", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("pageSize", StringComparison.OrdinalIgnoreCase)
                    ? Problem(ReconciliationApplicationErrors.ReviewCaseInvalidPagination)
                    : Problem(ReconciliationApplicationErrors.ReviewCaseInvalidRequest);
            }
        }

        return null;
    }

    private static bool TryReadStatus(
        HttpRequest httpRequest,
        out ReviewCaseStatus? status,
        out ProblemHttpResult? problem)
    {
        status = null;
        problem = null;
        if (!httpRequest.Query.TryGetValue("status", out StringValues values))
        {
            return true;
        }

        if (values.Count != 1 || !TryParseStatus(values[0], out ReviewCaseStatus parsed))
        {
            problem = Problem(ReconciliationApplicationErrors.ReviewCaseInvalidRequest);
            return false;
        }

        status = parsed;
        return true;
    }

    private static bool TryReadQueryGuid(
        HttpRequest httpRequest,
        string name,
        out Guid? identifier,
        out ProblemHttpResult? problem)
    {
        identifier = null;
        problem = null;
        if (!httpRequest.Query.TryGetValue(name, out StringValues values))
        {
            return true;
        }

        if (values.Count != 1
            || !Guid.TryParse(values[0], out Guid parsed)
            || parsed == Guid.Empty)
        {
            problem = Problem(ReconciliationApplicationErrors.ReviewCaseInvalidRequest);
            return false;
        }

        identifier = parsed;
        return true;
    }

    private static bool TryReadPage(
        HttpRequest httpRequest,
        string name,
        int defaultValue,
        out int page,
        out ProblemHttpResult? problem)
    {
        page = defaultValue;
        problem = null;
        if (!httpRequest.Query.TryGetValue(name, out StringValues values))
        {
            return true;
        }

        if (values.Count != 1
            || !int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out page))
        {
            problem = Problem(ReconciliationApplicationErrors.ReviewCaseInvalidPagination);
            return false;
        }

        return true;
    }

    private static bool TryReadProperties(
        JsonElement root,
        string[] allowedNames,
        out Dictionary<string, JsonElement>? properties)
    {
        properties = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (root.ValueKind != JsonValueKind.Object)
        {
            properties = null;
            return false;
        }

        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!allowedNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase)
                || !properties.TryAdd(property.Name, property.Value))
            {
                properties = null;
                return false;
            }
        }

        return true;
    }

    private static bool TryReadRequiredString(
        Dictionary<string, JsonElement> properties,
        string name,
        out string? value)
    {
        value = null;
        if (!properties.TryGetValue(name, out JsonElement element)
            || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return !string.IsNullOrEmpty(value);
    }

    private static bool TryReadOptionalString(
        Dictionary<string, JsonElement> properties,
        string name,
        out string? value)
    {
        value = null;
        if (!properties.TryGetValue(name, out JsonElement element))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return true;
    }

    private static bool TryReadOptionalGuid(
        Dictionary<string, JsonElement> properties,
        string name,
        out Guid? value)
    {
        value = null;
        if (!properties.TryGetValue(name, out JsonElement element))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String
            || !Guid.TryParse(element.GetString(), out Guid parsed)
            || parsed == Guid.Empty)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryReadConcurrencyToken(
        Dictionary<string, JsonElement> properties,
        out string? token,
        out ProblemHttpResult? problem)
    {
        token = null;
        problem = null;
        if (!properties.TryGetValue("concurrencyToken", out JsonElement element)
            || element.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(element.GetString()))
        {
            problem = Problem(ReconciliationApplicationErrors.ReviewCaseInvalidConcurrencyToken);
            return false;
        }

        token = element.GetString();
        return true;
    }

    private static bool TryParseReason(string? value, out ReviewCaseReason reason)
    {
        if (value == nameof(ReviewCaseReason.CompetingMatch))
        {
            reason = ReviewCaseReason.CompetingMatch;
            return true;
        }

        if (value == nameof(ReviewCaseReason.NeedsInvestigation))
        {
            reason = ReviewCaseReason.NeedsInvestigation;
            return true;
        }

        reason = default;
        return false;
    }

    private static bool TryParseOutcome(string? value, out ReviewCaseOutcome outcome)
    {
        if (value == nameof(ReviewCaseOutcome.AllocationRecorded))
        {
            outcome = ReviewCaseOutcome.AllocationRecorded;
            return true;
        }

        if (value == nameof(ReviewCaseOutcome.NoActionRequired))
        {
            outcome = ReviewCaseOutcome.NoActionRequired;
            return true;
        }

        if (value == nameof(ReviewCaseOutcome.HandledOutside))
        {
            outcome = ReviewCaseOutcome.HandledOutside;
            return true;
        }

        outcome = default;
        return false;
    }

    private static bool TryParseStatus(string? value, out ReviewCaseStatus status)
    {
        if (value == nameof(ReviewCaseStatus.Open))
        {
            status = ReviewCaseStatus.Open;
            return true;
        }

        if (value == nameof(ReviewCaseStatus.Resolved))
        {
            status = ReviewCaseStatus.Resolved;
            return true;
        }

        if (value == nameof(ReviewCaseStatus.Dismissed))
        {
            status = ReviewCaseStatus.Dismissed;
            return true;
        }

        status = default;
        return false;
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

    private static Task DocumentOpenOperation(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        operation.RequestBody = JsonBody(
            "Reason is CompetingMatch or NeedsInvestigation. CompetingMatch requires invoiceId and bankTransactionId. NeedsInvestigation requires exactly one of them. sourceLabel is manual, exact-1, or smart-1.",
            CreateOpenRequestSchema());
        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "Idempotency-Key",
            In = ParameterLocation.Header,
            Required = false,
            Description = "Optional. Absence keeps open-subject repetition. A present value is one UUID in D format with no surrounding whitespace and is not trimmed.",
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Format = "uuid",
            },
        });
        DescribeCreatedLocation(operation);
        AnnotateReviewCaseSchemas(context);
        return Task.CompletedTask;
    }

    private static Task DocumentListOperation(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        operation.Parameters ??= [];
        operation.Parameters.Add(QueryParameter(
            "status",
            "Open, Resolved, or Dismissed. Omitted returns every status.",
            StringEnum(null, "Open", "Resolved", "Dismissed")));
        operation.Parameters.Add(QueryParameter(
            "invoiceId",
            "Optional invoice filter. An empty GUID is rejected.",
            new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Format = "uuid",
            }));
        operation.Parameters.Add(QueryParameter(
            "bankTransactionId",
            "Optional bank transaction filter. An empty GUID is rejected.",
            new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Format = "uuid",
            }));
        operation.Parameters.Add(QueryParameter(
            "page",
            "Page number. Defaults to 1. The zero-based offset must fit in a 32-bit integer.",
            new OpenApiSchema
            {
                Type = JsonSchemaType.Integer,
                Default = JsonValue.Create(ReviewCaseListPagination.DefaultPage),
                Minimum = "1",
            }));
        operation.Parameters.Add(QueryParameter(
            "pageSize",
            "Page size. Defaults to 50. Maximum is 100.",
            new OpenApiSchema
            {
                Type = JsonSchemaType.Integer,
                Default = JsonValue.Create(ReviewCaseListPagination.DefaultPageSize),
                Minimum = "1",
                Maximum = "100",
            }));
        AnnotateReviewCaseSchemas(context);
        return Task.CompletedTask;
    }

    private static Task DocumentSummaryOperation(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Document?.Components?.Schemas is not null
            && context.Document.Components.Schemas.TryGetValue(nameof(ReviewCaseSummaryResponse), out IOpenApiSchema? schema))
        {
            AnnotateSummarySchema(schema);
        }

        return Task.CompletedTask;
    }

    private static Task DocumentGetOperation(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        DescribeRouteId(operation);
        AnnotateReviewCaseSchemas(context);
        return Task.CompletedTask;
    }

    private static Task DocumentAuditOperation(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        DescribeRouteId(operation);
        operation.Parameters ??= [];
        operation.Parameters.Add(QueryParameter(
            "page",
            "Page number. Defaults to 1. The zero-based offset must fit in a 32-bit integer.",
            new OpenApiSchema
            {
                Type = JsonSchemaType.Integer,
                Default = JsonValue.Create(ReviewCaseAuditListPagination.DefaultPage),
                Minimum = "1",
            }));
        operation.Parameters.Add(QueryParameter(
            "pageSize",
            "Page size. Defaults to 50. Maximum is 100.",
            new OpenApiSchema
            {
                Type = JsonSchemaType.Integer,
                Default = JsonValue.Create(ReviewCaseAuditListPagination.DefaultPageSize),
                Minimum = "1",
                Maximum = "100",
            }));
        return Task.CompletedTask;
    }

    private static Task DocumentCommentOperation(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        DescribeRouteId(operation);
        operation.RequestBody = JsonBody(
            "text is required. Blank text is rejected. The stored value is the .NET trim of the submitted text.",
            CreateCommentRequestSchema());
        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "Idempotency-Key",
            In = ParameterLocation.Header,
            Required = true,
            Description = "Required. One non-empty UUID in D format with no surrounding whitespace. The value is not trimmed into a valid key.",
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Format = "uuid",
            },
        });
        return Task.CompletedTask;
    }

    private static Task DocumentCommentsOperation(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        DescribeRouteId(operation);
        operation.Parameters ??= [];
        operation.Parameters.Add(QueryParameter(
            "page",
            "Page number. Defaults to 1. The zero-based offset must fit in a 32-bit integer.",
            new OpenApiSchema
            {
                Type = JsonSchemaType.Integer,
                Default = JsonValue.Create(ReviewCaseCommentListPagination.DefaultPage),
                Minimum = "1",
            }));
        operation.Parameters.Add(QueryParameter(
            "pageSize",
            "Page size. Defaults to 50. Maximum is 100.",
            new OpenApiSchema
            {
                Type = JsonSchemaType.Integer,
                Default = JsonValue.Create(ReviewCaseCommentListPagination.DefaultPageSize),
                Minimum = "1",
                Maximum = "100",
            }));
        return Task.CompletedTask;
    }

    private static Task DocumentResolveOperation(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        DescribeRouteId(operation);
        operation.RequestBody = JsonBody(
            "AllocationRecorded requires allocationId. NoActionRequired and HandledOutside forbid allocationId.",
            CreateResolveRequestSchema());
        AnnotateReviewCaseSchemas(context);
        return Task.CompletedTask;
    }

    private static Task DocumentDismissOperation(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        DescribeRouteId(operation);
        operation.RequestBody = JsonBody(
            "Dismissal accepts an optional resolution note and the concurrency token. outcome and allocationId are rejected.",
            CreateDismissRequestSchema());
        AnnotateReviewCaseSchemas(context);
        return Task.CompletedTask;
    }

    private static OpenApiRequestBody JsonBody(string description, OpenApiSchema schema)
    {
        return new OpenApiRequestBody
        {
            Required = true,
            Description = description,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new OpenApiMediaType
                {
                    Schema = schema,
                },
            },
        };
    }

    private static OpenApiParameter QueryParameter(string name, string description, OpenApiSchema schema)
    {
        return new OpenApiParameter
        {
            Name = name,
            In = ParameterLocation.Query,
            Required = false,
            Description = description,
            Schema = schema,
        };
    }

    private static void DescribeRouteId(OpenApiOperation operation)
    {
        operation.Parameters ??= [];
        foreach (IOpenApiParameter parameter in operation.Parameters)
        {
            if (string.Equals(parameter.Name, "id", StringComparison.OrdinalIgnoreCase)
                && parameter.In == ParameterLocation.Path
                && parameter is OpenApiParameter pathParameter)
            {
                pathParameter.Description = "Review case id. An empty or malformed GUID is rejected.";
                pathParameter.Schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Format = "uuid",
                };
                return;
            }
        }
    }

    private static void DescribeCreatedLocation(OpenApiOperation operation)
    {
        if (operation.Responses?.TryGetValue("201", out IOpenApiResponse? created) != true
            || created is not OpenApiResponse createdResponse)
        {
            return;
        }

        createdResponse.Headers ??= new Dictionary<string, IOpenApiHeader>();
        createdResponse.Headers["Location"] = new OpenApiHeader
        {
            Description = "URL of GET /api/review-cases/{id} for the created case.",
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
            },
        };
    }

    private static void AnnotateReviewCaseSchemas(OpenApiOperationTransformerContext context)
    {
        if (context.Document?.Components?.Schemas is null)
        {
            return;
        }

        if (context.Document.Components.Schemas.TryGetValue(nameof(ReviewCaseResponse), out IOpenApiSchema? schema))
        {
            AnnotateCaseSchema(schema);
        }
    }

    private static void AnnotateSummarySchema(IOpenApiSchema schema)
    {
        if (schema is not OpenApiSchema target)
        {
            return;
        }

        string[] names =
        [
            "totalCount",
            "openCount",
            "resolvedCount",
            "dismissedCount",
            "openCompetingMatchCount",
            "openNeedsInvestigationCount",
        ];
        target.Required = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (string name in names)
        {
            if (!TryGetProperty(target, name, out OpenApiSchema? property))
            {
                continue;
            }

            property.Type = JsonSchemaType.Integer;
            property.Format = "int64";
            property.Minimum = "0";
        }
    }

    private static void AnnotateCaseSchema(IOpenApiSchema schema)
    {
        if (schema is not OpenApiSchema target)
        {
            return;
        }

        SetStringEnum(
            target,
            "reason",
            "Why the case was opened.",
            nameof(ReviewCaseReason.CompetingMatch),
            nameof(ReviewCaseReason.NeedsInvestigation));
        SetStringEnum(
            target,
            "status",
            "Open, Resolved, or Dismissed.",
            nameof(ReviewCaseStatus.Open),
            nameof(ReviewCaseStatus.Resolved),
            nameof(ReviewCaseStatus.Dismissed));
        SetStringEnum(
            target,
            "outcome",
            "Present after resolution. A dismissed case has no outcome.",
            nameof(ReviewCaseOutcome.AllocationRecorded),
            nameof(ReviewCaseOutcome.NoActionRequired),
            nameof(ReviewCaseOutcome.HandledOutside));
        SetStringEnum(
            target,
            "sourceLabel",
            "Optional provenance label. It is not a match result.",
            ReviewCaseSource.Manual.Value,
            ReviewCaseSource.Exact1.Value,
            ReviewCaseSource.Smart1.Value);
        if (TryGetProperty(target, "concurrencyToken", out OpenApiSchema? token))
        {
            token.Description = ConcurrencyTokenDescription;
            token.Type = JsonSchemaType.String;
        }
    }

    private static void SetStringEnum(
        OpenApiSchema schema,
        string name,
        string description,
        params string[] values)
    {
        if (!TryGetProperty(schema, name, out OpenApiSchema? property))
        {
            return;
        }

        property.Description = description;
        property.Type = JsonSchemaType.String;
        property.Enum = values.Select(static value => (JsonNode)JsonValue.Create(value)!).ToList();
    }

    private static bool TryGetProperty(OpenApiSchema schema, string name, out OpenApiSchema property)
    {
        property = null!;
        if (schema.Properties is null)
        {
            return false;
        }

        if (!schema.Properties.TryGetValue(name, out IOpenApiSchema? candidate)
            && !schema.Properties.TryGetValue(char.ToUpperInvariant(name[0]) + name[1..], out candidate))
        {
            return false;
        }

        if (candidate is not OpenApiSchema openApiSchema)
        {
            return false;
        }

        property = openApiSchema;
        return true;
    }

    private static OpenApiSchema CreateCommentRequestSchema()
    {
        return new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            AdditionalPropertiesAllowed = false,
            Required = new HashSet<string> { "text" },
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["text"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Description = "Required plain text. The server applies string.Trim() and then requires 1 to 500 UTF-16 code units. Surrounding whitespace is not part of that limit. U+FEFF is preserved. The raw value has no maxLength.",
                },
            },
        };
    }

    private static OpenApiSchema CreateOpenRequestSchema()
    {
        return new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            AdditionalPropertiesAllowed = false,
            Required = new HashSet<string> { "reason" },
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["reason"] = StringEnum(
                    "CompetingMatch requires both financial ids. NeedsInvestigation requires exactly one.",
                    nameof(ReviewCaseReason.CompetingMatch),
                    nameof(ReviewCaseReason.NeedsInvestigation)),
                ["invoiceId"] = Uuid("Optional invoice id. An empty GUID is rejected."),
                ["bankTransactionId"] = Uuid("Optional bank transaction id. An empty GUID is rejected."),
                ["sourceLabel"] = StringEnum(
                    "Optional provenance label.",
                    ReviewCaseSource.Manual.Value,
                    ReviewCaseSource.Exact1.Value,
                    ReviewCaseSource.Smart1.Value),
                ["note"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Description = "Optional opening note. Blank becomes null. Maximum length is 500 characters after trim.",
                    MaxLength = ReviewCase.MaxNoteLength,
                },
            },
        };
    }

    private static OpenApiSchema CreateResolveRequestSchema()
    {
        return new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            AdditionalPropertiesAllowed = false,
            Required = new HashSet<string> { "outcome", "concurrencyToken" },
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["outcome"] = StringEnum(
                    "AllocationRecorded requires allocationId. The other outcomes forbid it.",
                    nameof(ReviewCaseOutcome.AllocationRecorded),
                    nameof(ReviewCaseOutcome.NoActionRequired),
                    nameof(ReviewCaseOutcome.HandledOutside)),
                ["allocationId"] = Uuid("Required only for AllocationRecorded. An empty GUID is rejected."),
                ["resolutionNote"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Description = "Optional resolution note. Blank becomes null. Maximum length is 500 characters after trim.",
                    MaxLength = ReviewCase.MaxNoteLength,
                },
                ["concurrencyToken"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Description = ConcurrencyTokenDescription,
                },
            },
        };
    }

    private static OpenApiSchema CreateDismissRequestSchema()
    {
        return new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            AdditionalPropertiesAllowed = false,
            Required = new HashSet<string> { "concurrencyToken" },
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["resolutionNote"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Description = "Optional dismissal note. Blank becomes null. Maximum length is 500 characters after trim.",
                    MaxLength = ReviewCase.MaxNoteLength,
                },
                ["concurrencyToken"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Description = ConcurrencyTokenDescription,
                },
            },
        };
    }

    private static Task DocumentAssignmentOperation(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        operation.RequestBody = JsonBody(
            "concurrencyToken is the Base64 rowversion read with the case. Equivalent Base64 whitespace is the same token. The response does not return the resulting token.",
            new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                AdditionalPropertiesAllowed = false,
                Required = new HashSet<string> { "concurrencyToken" },
                Properties = new Dictionary<string, IOpenApiSchema>
                {
                    ["concurrencyToken"] = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String,
                        Description = ConcurrencyTokenDescription,
                    },
                },
            });
        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "Idempotency-Key",
            In = ParameterLocation.Header,
            Required = true,
            Description = "Required. One non-empty UUID in D format with no surrounding whitespace. Claim and release share this key.",
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Format = "uuid",
            },
        });
        return Task.CompletedTask;
    }

    private static Task DocumentDelegateOperation(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        operation.RequestBody = JsonBody(
            "concurrencyToken is the Base64 rowversion read with the case. recipientActorId is trimmed and must name another person on the current organization list.",
            new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                AdditionalPropertiesAllowed = false,
                Required = new HashSet<string> { "concurrencyToken", "recipientActorId" },
                Properties = new Dictionary<string, IOpenApiSchema>
                {
                    ["concurrencyToken"] = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String,
                        Description = ConcurrencyTokenDescription,
                    },
                    ["recipientActorId"] = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String,
                        Description = "Normalized recipient id. The server checks it again for a new command.",
                    },
                },
            });
        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "Idempotency-Key",
            In = ParameterLocation.Header,
            Required = true,
            Description = "Required. One non-empty UUID in D format with no surrounding whitespace. Claim, release, and delegate share this key.",
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Format = "uuid",
            },
        });
        return Task.CompletedTask;
    }

    private static Task DocumentAssignmentReadOperation(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static OpenApiSchema StringEnum(string? description, params string[] values)
    {
        return new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            Description = description,
            Enum = values.Select(static value => (JsonNode)JsonValue.Create(value)!).ToList(),
        };
    }

    private static OpenApiSchema Uuid(string description)
    {
        return new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            Format = "uuid",
            Description = description,
        };
    }

    private static ProblemHttpResult Problem(Error error)
    {
        return TypedResults.Problem(
            detail: error.Message,
            statusCode: StatusCodeFor(error),
            title: TitleFor(error),
            extensions: new Dictionary<string, object?>
            {
                ["code"] = error.Code,
            });
    }

    private static int StatusCodeFor(Error error)
    {
        if (error.Code == ReconciliationApplicationErrors.ReviewCaseInvalidRequest.Code
            || error.Code == ReconciliationApplicationErrors.ReviewCaseIdempotencyKeyInvalid.Code
            || error.Code == ReconciliationApplicationErrors.ReviewCaseAuditInvalidRequest.Code
            || error.Code == ReconciliationApplicationErrors.ReviewCaseAuditInvalidPagination.Code
            || error.Code == ReconciliationApplicationErrors.ReviewCaseInvalidConcurrencyToken.Code
            || error.Code == ReconciliationApplicationErrors.ReviewCaseInvalidPagination.Code
            || error.Code == ReconciliationErrors.ReviewCaseAllocationRequired.Code
            || error.Code == ReconciliationErrors.ReviewCaseAllocationForbidden.Code
            || error.Code == ReconciliationApplicationErrors.ReviewCaseCommentInvalidRequest.Code
            || error.Code == ReconciliationApplicationErrors.ReviewCaseCommentIdempotencyKeyRequired.Code
            || error.Code == ReconciliationApplicationErrors.ReviewCaseCommentIdempotencyKeyInvalid.Code
            || error.Code == ReconciliationApplicationErrors.ReviewCaseCommentInvalidPagination.Code
            || error.Code == ReconciliationApplicationErrors.ReviewCaseAssignmentInvalidRequest.Code
            || error.Code == ReconciliationApplicationErrors.ReviewCaseAssignmentIdempotencyKeyRequired.Code
            || error.Code == ReconciliationApplicationErrors.ReviewCaseAssignmentIdempotencyKeyInvalid.Code)
        {
            return StatusCodes.Status400BadRequest;
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseNotFound.Code
            || error.Code == ReconciliationApplicationErrors.InvoiceNotFound.Code
            || error.Code == ReconciliationApplicationErrors.BankTransactionNotFound.Code)
        {
            return StatusCodes.Status404NotFound;
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseConcurrencyConflict.Code
            || error.Code == ReconciliationErrors.ReviewCaseTerminalConflict.Code
            || error.Code == ReconciliationApplicationErrors.ReviewCaseAllocationMismatch.Code
            || error.Code == ReconciliationApplicationErrors.ReviewCaseSubjectConflict.Code
            || error.Code == ReconciliationApplicationErrors.ReviewCaseIdempotencyPayloadConflict.Code
            || error.Code == ReconciliationApplicationErrors.ReviewCaseOpenRequestConflict.Code
            || error.Code == ReconciliationApplicationErrors.ReviewCaseCommentIdempotencyPayloadConflict.Code
            || error.Code == ReconciliationApplicationErrors.ReviewCaseCommentConcurrencyConflict.Code
            || error.Code == ReconciliationApplicationErrors.ReviewCaseAssignmentTerminal.Code
            || error.Code == ReconciliationApplicationErrors.ReviewCaseAssignmentAlreadyAssigned.Code
            || error.Code == ReconciliationApplicationErrors.ReviewCaseAssignmentNotAssigned.Code
            || error.Code == ReconciliationApplicationErrors.ReviewCaseAssignmentNotAssignee.Code
            || error.Code == ReconciliationApplicationErrors.ReviewCaseAssignmentIdempotencyPayloadConflict.Code
            || error.Code == ReconciliationApplicationErrors.ReviewCaseAssignmentConcurrencyConflict.Code
            || error.Code == ReconciliationApplicationErrors.ReviewCaseAssignmentDelegateeUnavailable.Code)
        {
            return StatusCodes.Status409Conflict;
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseDelegationUnavailable.Code)
        {
            return StatusCodes.Status403Forbidden;
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseInconsistentLinkedMoney.Code)
        {
            return StatusCodes.Status500InternalServerError;
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseUnsupportedMediaType.Code)
        {
            return StatusCodes.Status415UnsupportedMediaType;
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseRequestTooLarge.Code)
        {
            return StatusCodes.Status413PayloadTooLarge;
        }

        if (error.Code == ReconciliationApplicationErrors.ActorRequired.Code)
        {
            return StatusCodes.Status403Forbidden;
        }

        throw new InvalidOperationException(
            $"Unexpected review case failure '{error.Code}'.");
    }

    private static string TitleFor(Error error)
    {
        if (error.Code == ReconciliationApplicationErrors.ActorRequired.Code)
        {
            return "A valid authenticated actor is required.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseInvalidRequest.Code)
        {
            return "Review case request is invalid.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseIdempotencyKeyInvalid.Code)
        {
            return "Idempotency-Key is invalid.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseInvalidConcurrencyToken.Code)
        {
            return "Concurrency token is invalid.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseInvalidPagination.Code
            || error.Code == ReconciliationApplicationErrors.ReviewCaseAuditInvalidPagination.Code
            || error.Code == ReconciliationApplicationErrors.ReviewCaseCommentInvalidPagination.Code)
        {
            return "Invalid pagination.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseAuditInvalidRequest.Code)
        {
            return "Review case audit request is invalid.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseCommentInvalidRequest.Code)
        {
            return "Review case comment request is invalid.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseCommentIdempotencyKeyRequired.Code)
        {
            return "Idempotency-Key is required.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseCommentIdempotencyKeyInvalid.Code)
        {
            return "Idempotency-Key is invalid.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseCommentIdempotencyPayloadConflict.Code)
        {
            return "This idempotency key was already used for a different comment.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseCommentConcurrencyConflict.Code)
        {
            return "The review case comment could not be recovered.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseAssignmentInvalidRequest.Code)
        {
            return "Review case assignment request is invalid.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseAssignmentIdempotencyKeyRequired.Code)
        {
            return "Idempotency-Key is required.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseAssignmentIdempotencyKeyInvalid.Code)
        {
            return "Idempotency-Key is invalid.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseAssignmentTerminal.Code)
        {
            return "The review case is closed.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseAssignmentAlreadyAssigned.Code)
        {
            return "The review case is already assigned.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseAssignmentNotAssigned.Code)
        {
            return "The review case is not assigned.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseAssignmentNotAssignee.Code)
        {
            return "The assignment belongs to a different actor.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseAssignmentDelegateeUnavailable.Code)
        {
            return "The recipient cannot take this review case.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseDelegationUnavailable.Code)
        {
            return "Delegation recipients are not available.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseAssignmentIdempotencyPayloadConflict.Code)
        {
            return "This idempotency key was already used for a different assignment.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseAssignmentConcurrencyConflict.Code)
        {
            return "The review case assignment could not be recovered.";
        }

        if (error.Code == ReconciliationErrors.ReviewCaseAllocationRequired.Code)
        {
            return "An allocation id is required.";
        }

        if (error.Code == ReconciliationErrors.ReviewCaseAllocationForbidden.Code)
        {
            return "This outcome cannot reference an allocation.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseNotFound.Code)
        {
            return "Review case was not found.";
        }

        if (error.Code == ReconciliationApplicationErrors.InvoiceNotFound.Code)
        {
            return "Invoice was not found.";
        }

        if (error.Code == ReconciliationApplicationErrors.BankTransactionNotFound.Code)
        {
            return "Bank transaction was not found.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseConcurrencyConflict.Code)
        {
            return "The review case was modified.";
        }

        if (error.Code == ReconciliationErrors.ReviewCaseTerminalConflict.Code)
        {
            return "The review case already has a terminal decision.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseAllocationMismatch.Code)
        {
            return "The allocation does not cover the review case.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseSubjectConflict.Code)
        {
            return "The open review case could not be recovered.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseIdempotencyPayloadConflict.Code)
        {
            return "Idempotency key was used for a different request.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseOpenRequestConflict.Code)
        {
            return "The open review case stores a different request.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseInconsistentLinkedMoney.Code)
        {
            return "Stored financial amounts are inconsistent.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseUnsupportedMediaType.Code)
        {
            return "Unsupported media type.";
        }

        if (error.Code == ReconciliationApplicationErrors.ReviewCaseRequestTooLarge.Code)
        {
            return "Review case request is too large.";
        }

        throw new InvalidOperationException(
            $"Unexpected review case failure '{error.Code}'.");
    }

    private readonly record struct JsonRead(
        JsonDocument? Document,
        Dictionary<string, JsonElement>? Properties,
        ProblemHttpResult? Problem)
    {
        public static JsonRead Failed(ProblemHttpResult problem)
        {
            return new JsonRead(null, null, problem);
        }

        public static JsonRead Succeeded(JsonDocument document, Dictionary<string, JsonElement> properties)
        {
            return new JsonRead(document, properties, null);
        }
    }
}