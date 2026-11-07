using System.Data;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Repositories;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Xunit.Abstractions;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class AllocationDisputeEndpointTests
{
    private static readonly DateTimeOffset ConfirmedAt = new(2026, 11, 6, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(20);

    private readonly SqlServerFixture _fixture;
    private readonly ITestOutputHelper _output;

    public AllocationDisputeEndpointTests(SqlServerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task Registration_preserves_the_allocation_balances_and_audit()
    {
        OrganizationId organizationId = OrganizationId.New();
        Seeded seeded = await SeedAsync(organizationId, 40m);
        using FinOpsDeskApiFactory factory = new(_fixture.ConnectionString);
        using HttpClient client = Client(factory, organizationId, "operator-a");

        HttpResponseMessage response = await client.SendAsync(
            DisputeRequest(seeded.AllocationId.Value, "  The customer challenges this payment.  ", Guid.NewGuid()));

        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Null(response.Headers.Location);
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        Assert.Equal(seeded.AllocationId.Value, root.GetProperty("allocationId").GetGuid());
        Assert.Equal("The customer challenges this payment.", root.GetProperty("statement").GetString());
        Assert.Equal("developmentJwt", root.GetProperty("actorSource").GetString());
        Assert.Equal("operator-a", root.GetProperty("actorId").GetString());
        Assert.NotEqual(default, root.GetProperty("recordedAt").GetDateTimeOffset());
        Assert.False(root.TryGetProperty("organizationId", out _));
        FinancialState after = await ReadStateAsync(seeded.AllocationId);
        Assert.Equal(seeded.RowVersion, after.RowVersion);
        Assert.Equal(AllocationStatus.Confirmed, after.Status);
        Assert.Equal(seeded.State.InvoiceAllocated, after.InvoiceAllocated);
        Assert.Equal(seeded.State.InvoiceOutstanding, after.InvoiceOutstanding);
        Assert.Equal(seeded.State.BankAllocated, after.BankAllocated);
        Assert.Equal(seeded.State.BankUnapplied, after.BankUnapplied);
        Assert.Equal(seeded.State.AuditCount, after.AuditCount);
        Assert.Equal(1, await CountDisputesAsync(organizationId));
    }

    [Fact]
    public async Task A_new_key_registers_another_dispute_and_the_same_key_is_scoped_to_the_organization()
    {
        OrganizationId firstOrganization = OrganizationId.New();
        OrganizationId secondOrganization = OrganizationId.New();
        Seeded first = await SeedAsync(firstOrganization, 15m);
        Seeded second = await SeedAsync(secondOrganization, 18m);
        Guid sharedKey = Guid.NewGuid();
        using FinOpsDeskApiFactory factory = new(_fixture.ConnectionString);
        using HttpClient firstClient = Client(factory, firstOrganization, "operator-a");
        using HttpClient secondClient = Client(factory, secondOrganization, "operator-b");

        HttpResponseMessage firstCreated = await firstClient.SendAsync(
            DisputeRequest(first.AllocationId.Value, "Same words", sharedKey));
        HttpResponseMessage secondCreated = await secondClient.SendAsync(
            DisputeRequest(second.AllocationId.Value, "Same words", sharedKey));
        HttpResponseMessage another = await firstClient.SendAsync(
            DisputeRequest(first.AllocationId.Value, "Same words", Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Created, firstCreated.StatusCode);
        Assert.Equal(HttpStatusCode.Created, secondCreated.StatusCode);
        Assert.Equal(HttpStatusCode.Created, another.StatusCode);
        Assert.Equal(2, await CountDisputesAsync(firstOrganization));
        Assert.Equal(1, await CountDisputesAsync(secondOrganization));

        HttpResponseMessage foreign = await firstClient.SendAsync(
            DisputeRequest(second.AllocationId.Value, "Same words", Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(
            "reconciliation.allocation.not_found",
            await ReadCodeAsync(foreign));
        Assert.Equal(2, await CountDisputesAsync(firstOrganization));
        Assert.Equal(1, await CountDisputesAsync(secondOrganization));
    }

    [Fact]
    public async Task Statement_validation_uses_the_trimmed_text_and_keeps_a_leading_bom()
    {
        OrganizationId organizationId = OrganizationId.New();
        Seeded seeded = await SeedAsync(organizationId, 11m);
        using FinOpsDeskApiFactory factory = new(_fixture.ConnectionString);
        using HttpClient client = Client(factory, organizationId, "operator-a");

        HttpResponseMessage blank = await client.SendAsync(
            DisputeRequest(seeded.AllocationId.Value, "   ", Guid.NewGuid()));
        HttpResponseMessage missingKey = await client.SendAsync(
            DisputeRaw(seeded.AllocationId.Value, "{\"statement\":\"ok\"}", null));
        HttpResponseMessage paddedKey = await client.SendAsync(
            DisputeRaw(seeded.AllocationId.Value, "{\"statement\":\"ok\"}", " " + Guid.NewGuid().ToString("D")));
        HttpResponseMessage emptyKey = await client.SendAsync(
            DisputeRaw(seeded.AllocationId.Value, "{\"statement\":\"ok\"}", Guid.Empty.ToString("D")));
        HttpResponseMessage tooLong = await client.SendAsync(
            DisputeRequest(seeded.AllocationId.Value, new string('a', 501), Guid.NewGuid()));
        HttpResponseMessage extra = await client.SendAsync(
            DisputeRaw(seeded.AllocationId.Value, "{\"statement\":\"ok\",\"actorId\":\"spoof\"}", Guid.NewGuid().ToString("D")));
        HttpResponseMessage bom = await client.SendAsync(
            DisputeRequest(seeded.AllocationId.Value, "  \uFEFFcustomer challenge  ", Guid.NewGuid()));

        Assert.Equal("reconciliation.allocation_dispute.statement_required", await ReadCodeAsync(blank));
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Equal("reconciliation.allocation_dispute.idempotency_key_required", await ReadCodeAsync(missingKey));
        Assert.Equal("reconciliation.allocation_dispute.idempotency_key_invalid", await ReadCodeAsync(paddedKey));
        Assert.Equal("reconciliation.allocation_dispute.idempotency_key_invalid", await ReadCodeAsync(emptyKey));
        Assert.Equal("reconciliation.allocation_dispute.statement_too_long", await ReadCodeAsync(tooLong));
        Assert.Equal("reconciliation.allocation_dispute.invalid_request", await ReadCodeAsync(extra));
        Assert.Equal(HttpStatusCode.Created, bom.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await bom.Content.ReadAsStringAsync());
        Assert.Equal("\uFEFFcustomer challenge", document.RootElement.GetProperty("statement").GetString());
        Assert.Equal(1, await CountDisputesAsync(organizationId));
    }

    [Fact]
    public async Task Idempotency_key_aliases_are_rejected_and_canonical_case_is_one_dispute()
    {
        OrganizationId organizationId = OrganizationId.New();
        Seeded seeded = await SeedAsync(organizationId, 13m);
        using FinOpsDeskApiFactory factory = new(_fixture.ConnectionString);
        using HttpClient client = Client(factory, organizationId, "operator-a");
        const string plusAlias = "+0000001-0000-0000-0000-000000000000";
        const string hexAlias = "0x000001-0000-0000-0000-000000000000";

        HttpResponseMessage plus = await client.SendAsync(
            DisputeRaw(seeded.AllocationId.Value, "{\"statement\":\"alias\"}", plusAlias));
        HttpResponseMessage hex = await client.SendAsync(
            DisputeRaw(seeded.AllocationId.Value, "{\"statement\":\"alias\"}", hexAlias));

        Assert.Equal(HttpStatusCode.BadRequest, plus.StatusCode);
        Assert.Equal("reconciliation.allocation_dispute.idempotency_key_invalid", await ReadCodeAsync(plus));
        Assert.Equal(HttpStatusCode.BadRequest, hex.StatusCode);
        Assert.Equal("reconciliation.allocation_dispute.idempotency_key_invalid", await ReadCodeAsync(hex));
        Assert.Equal(0, await CountDisputesAsync(organizationId));

        string canonical = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee").ToString("D");
        HttpResponseMessage created = await client.SendAsync(
            DisputeRaw(seeded.AllocationId.Value, "{\"statement\":\"Canonical key\"}", canonical.ToUpperInvariant()));
        HttpResponseMessage replay = await client.SendAsync(
            DisputeRaw(seeded.AllocationId.Value, "{\"statement\":\"Canonical key\"}", canonical));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using JsonDocument createdDocument = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        using JsonDocument replayDocument = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(
            createdDocument.RootElement.GetProperty("disputeId").GetGuid(),
            replayDocument.RootElement.GetProperty("disputeId").GetGuid());
        Assert.Equal(1, await CountDisputesAsync(organizationId));
    }

    [Fact]
    public async Task Malformed_unicode_is_a_bad_request_and_a_surrogate_pair_is_stored()
    {
        OrganizationId organizationId = OrganizationId.New();
        Seeded seeded = await SeedAsync(organizationId, 14m);
        using FinOpsDeskApiFactory factory = new(_fixture.ConnectionString);
        using HttpClient client = Client(factory, organizationId, "operator-a");

        HttpResponseMessage value = await client.SendAsync(
            DisputeRaw(seeded.AllocationId.Value, "{\"statement\":\"\\uD800\"}", Guid.NewGuid().ToString("D")));
        HttpResponseMessage name = await client.SendAsync(
            DisputeRaw(seeded.AllocationId.Value, "{\"\\uD800\":\"ok\"}", Guid.NewGuid().ToString("D")));

        await AssertProblemAsync(value, "reconciliation.allocation_dispute.invalid_request");
        await AssertProblemAsync(name, "reconciliation.allocation_dispute.invalid_request");
        Assert.Equal(0, await CountDisputesAsync(organizationId));

        HttpResponseMessage pair = await client.SendAsync(
            DisputeRaw(seeded.AllocationId.Value, "{\"statement\":\"\\uD83D\\uDE00\"}", Guid.NewGuid().ToString("D")));
        Assert.Equal(HttpStatusCode.Created, pair.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await pair.Content.ReadAsStringAsync());
        Assert.Equal("\uD83D\uDE00", document.RootElement.GetProperty("statement").GetString());
        Assert.Equal(1, await CountDisputesAsync(organizationId));
    }

    [Fact]
    public async Task A_missing_statement_is_required_and_a_malformed_object_is_invalid()
    {
        OrganizationId organizationId = OrganizationId.New();
        Seeded seeded = await SeedAsync(organizationId, 16m);
        using FinOpsDeskApiFactory factory = new(_fixture.ConnectionString);
        using HttpClient client = Client(factory, organizationId, "operator-a");
        Guid allocationId = seeded.AllocationId.Value;

        HttpResponseMessage missing = await client.SendAsync(
            DisputeRaw(allocationId, "{}", Guid.NewGuid().ToString("D")));
        HttpResponseMessage blank = await client.SendAsync(
            DisputeRaw(allocationId, "{\"statement\":\"   \"}", Guid.NewGuid().ToString("D")));
        HttpResponseMessage duplicate = await client.SendAsync(
            DisputeRaw(allocationId, "{\"statement\":\"one\",\"statement\":\"two\"}", Guid.NewGuid().ToString("D")));
        HttpResponseMessage foreign = await client.SendAsync(
            DisputeRaw(allocationId, "{\"statement\":\"ok\",\"actorId\":\"spoof\"}", Guid.NewGuid().ToString("D")));
        HttpResponseMessage number = await client.SendAsync(
            DisputeRaw(allocationId, "{\"statement\":1}", Guid.NewGuid().ToString("D")));

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("reconciliation.allocation_dispute.statement_required", await ReadCodeAsync(missing));
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Equal("reconciliation.allocation_dispute.statement_required", await ReadCodeAsync(blank));
        Assert.Equal("reconciliation.allocation_dispute.invalid_request", await ReadCodeAsync(duplicate));
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.Equal("reconciliation.allocation_dispute.invalid_request", await ReadCodeAsync(foreign));
        Assert.Equal("reconciliation.allocation_dispute.invalid_request", await ReadCodeAsync(number));
        Assert.Equal(0, await CountDisputesAsync(organizationId));
    }

    [Fact]
    public async Task A_missing_actor_is_rejected_before_replay_and_another_actor_receives_the_original_confirmation()
    {
        OrganizationId organizationId = OrganizationId.New();
        Seeded seeded = await SeedAsync(organizationId, 21m);
        Guid key = Guid.NewGuid();
        using FinOpsDeskApiFactory factory = new(_fixture.ConnectionString);
        using HttpClient author = Client(factory, organizationId, "operator-a");
        using HttpClient anonymous = Client(factory, organizationId, null);
        using HttpClient other = Client(factory, organizationId, "operator-b");

        HttpResponseMessage created = await author.SendAsync(
            DisputeRequest(seeded.AllocationId.Value, "Original challenge", key));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using JsonDocument createdDocument = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Guid disputeId = createdDocument.RootElement.GetProperty("disputeId").GetGuid();
        string recordedAt = createdDocument.RootElement.GetProperty("recordedAt").GetString()!;

        HttpResponseMessage denied = await anonymous.SendAsync(
            DisputeRequest(seeded.AllocationId.Value, "Original challenge", key));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("reconciliation.actor.required", await ReadCodeAsync(denied));

        HttpResponseMessage replay = await other.SendAsync(
            DisputeRequest(seeded.AllocationId.Value, "Original challenge", key));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using JsonDocument replayDocument = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(disputeId, replayDocument.RootElement.GetProperty("disputeId").GetGuid());
        Assert.Equal("operator-a", replayDocument.RootElement.GetProperty("actorId").GetString());
        Assert.Equal(recordedAt, replayDocument.RootElement.GetProperty("recordedAt").GetString());
        Assert.Equal(1, await CountDisputesAsync(organizationId));
    }

    [Theory]
    [InlineData("match")]
    [InlineData("statement")]
    [InlineData("unknown")]
    public async Task A_paused_miss_classifies_the_key_after_registration_and_reversal(string shape)
    {
        OrganizationId organizationId = OrganizationId.New();
        Seeded seeded = await SeedAsync(organizationId, 33m);
        Guid key = Guid.NewGuid();
        DisputeLookupPause pause = new() { PauseFirstMiss = true };
        await using FinOpsDeskApiFactory factory = CreateFactory(pause, save: null);
        using HttpClient client = Client(factory, organizationId, "operator-a");
        string original = "The customer challenges this payment.";
        HttpRequestMessage paused = shape switch
        {
            "match" => DisputeRequest(seeded.AllocationId.Value, original, key),
            "statement" => DisputeRequest(seeded.AllocationId.Value, "A different challenge.", key),
            _ => DisputeRequest(Guid.NewGuid(), original, key),
        };
        Task<HttpResponseMessage> pending = client.SendAsync(paused);
        await pause.Entered.Task.WaitAsync(WaitLimit);

        HttpResponseMessage created = await client.SendAsync(
            DisputeRequest(seeded.AllocationId.Value, original, key));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using JsonDocument createdDocument = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        HttpResponseMessage reversed = await client.SendAsync(
            ReverseRequest(seeded.AllocationId.Value, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, reversed.StatusCode);
        pause.Resume.TrySetResult();
        HttpResponseMessage continued = await pending.WaitAsync(WaitLimit);

        if (shape == "match")
        {
            Assert.Equal(HttpStatusCode.OK, continued.StatusCode);
            using JsonDocument document = JsonDocument.Parse(await continued.Content.ReadAsStringAsync());
            Assert.Equal(createdDocument.RootElement.GetProperty("disputeId").GetGuid(), document.RootElement.GetProperty("disputeId").GetGuid());
            Assert.Equal(original, document.RootElement.GetProperty("statement").GetString());
            Assert.Equal("operator-a", document.RootElement.GetProperty("actorId").GetString());
            Assert.Equal(
                createdDocument.RootElement.GetProperty("recordedAt").GetString(),
                document.RootElement.GetProperty("recordedAt").GetString());
        }
        else
        {
            Assert.Equal(HttpStatusCode.Conflict, continued.StatusCode);
            Assert.Equal(
                "reconciliation.allocation_dispute.idempotency_payload_conflict",
                await ReadCodeAsync(continued));
        }

        Assert.Equal(1, await CountDisputesAsync(organizationId));
    }

    [Fact]
    public async Task A_paused_miss_of_an_unrecorded_key_returns_not_confirmed_after_reversal()
    {
        OrganizationId organizationId = OrganizationId.New();
        Seeded seeded = await SeedAsync(organizationId, 19m);
        DisputeLookupPause pause = new() { PauseFirstMiss = true };
        await using FinOpsDeskApiFactory factory = CreateFactory(pause, save: null);
        using HttpClient client = Client(factory, organizationId, "operator-a");
        Task<HttpResponseMessage> pending = client.SendAsync(
            DisputeRequest(seeded.AllocationId.Value, "Nobody stored this key.", Guid.NewGuid()));
        await pause.Entered.Task.WaitAsync(WaitLimit);

        HttpResponseMessage reversed = await client.SendAsync(
            ReverseRequest(seeded.AllocationId.Value, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, reversed.StatusCode);
        pause.Resume.TrySetResult();
        HttpResponseMessage continued = await pending.WaitAsync(WaitLimit);

        Assert.Equal(HttpStatusCode.Conflict, continued.StatusCode);
        Assert.Equal(
            "reconciliation.allocation_dispute.allocation_not_confirmed",
            await ReadCodeAsync(continued));
        Assert.Equal(0, await CountDisputesAsync(organizationId));
    }

    [Fact]
    public async Task Named_index_collision_releases_the_losing_transaction_before_the_winner_is_read()
    {
        OrganizationId organizationId = OrganizationId.New();
        Seeded seeded = await SeedAsync(organizationId, 27m);
        Guid key = Guid.NewGuid();
        AllocationDisputeSavePause save = new();
        DisputeLookupPause lookup = new();
        CollisionObservation collision = new();
        await using FinOpsDeskApiFactory factory = CreateFactory(lookup, save, collision);
        using HttpClient client = Client(factory, organizationId, "operator-a");
        Task<HttpResponseMessage> first = client.SendAsync(
            DisputeRequest(seeded.AllocationId.Value, "One challenge.", key));
        int registrationSession = await save.PausedSession.Task.WaitAsync(WaitLimit);
        Task<HttpResponseMessage> second = client.SendAsync(
            DisputeRequest(seeded.AllocationId.Value, "One challenge.", key));
        int waiter = await WaitForBlockedSessionAsync(registrationSession);
        _output.WriteLine(await DescribeWaitingAllocationLockAsync(waiter, registrationSession));
        collision.SetWaiterSession(waiter);
        save.Resume.TrySetResult();

        HttpResponseMessage created = await first.WaitAsync(WaitLimit);
        HttpResponseMessage replay = await second.WaitAsync(WaitLimit);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(1, collision.RecognizedCollisions);
        Assert.Equal(1, collision.RecoveryReads);
        Assert.Equal(1, collision.LockChecks);
        Assert.Equal(waiter, collision.CheckedSession);
        Assert.Null(collision.LockViolation);
        using JsonDocument createdDocument = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        using JsonDocument replayDocument = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(
            createdDocument.RootElement.GetProperty("disputeId").GetGuid(),
            replayDocument.RootElement.GetProperty("disputeId").GetGuid());
        Assert.Equal(1, await CountDisputesAsync(organizationId));
    }

    [Fact]
    public async Task Registration_waits_for_an_allocation_lock_and_then_rejects_the_reversed_row()
    {
        OrganizationId organizationId = OrganizationId.New();
        Seeded seeded = await SeedAsync(organizationId, 12m);
        using FinOpsDeskApiFactory factory = new(_fixture.ConnectionString);
        using HttpClient client = Client(factory, organizationId, "operator-a");
        await using SqlConnection held = new(_fixture.ConnectionString);
        await held.OpenAsync();
        await using SqlTransaction transaction = (SqlTransaction)await held.BeginTransactionAsync();
        int holder = await LockAllocationAsync(held, transaction, organizationId, seeded.AllocationId);
        Task<HttpResponseMessage> pending = client.SendAsync(
            DisputeRequest(seeded.AllocationId.Value, "Waiting on the allocation.", Guid.NewGuid()));
        int waiter = await WaitForBlockedSessionAsync(holder);
        _output.WriteLine(await DescribeWaitingAllocationLockAsync(waiter, holder));

        await using (SqlCommand update = held.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE [reconciliation].[Allocations]
                SET [Status] = 2,
                    [ReversedAt] = SYSDATETIMEOFFSET(),
                    [ReversalReason] = N'held lock'
                WHERE [OrganizationId] = @organizationId AND [Id] = @allocationId
                """;
            AddAllocationParameters(update, organizationId, seeded.AllocationId);
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        await transaction.CommitAsync();
        HttpResponseMessage response = await pending.WaitAsync(WaitLimit);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            "reconciliation.allocation_dispute.allocation_not_confirmed",
            await ReadCodeAsync(response));
        Assert.Equal(0, await CountDisputesAsync(organizationId));
    }

    [Fact]
    public async Task Reversal_waits_on_the_registration_transaction_and_then_restores_the_balances()
    {
        OrganizationId organizationId = OrganizationId.New();
        Seeded seeded = await SeedAsync(organizationId, 44m);
        AllocationDisputeSavePause save = new();
        await using FinOpsDeskApiFactory factory = CreateFactory(lookup: null, save);
        using HttpClient client = Client(factory, organizationId, "operator-a");
        Guid reversalKey = Guid.NewGuid();
        Task<HttpResponseMessage> registration = client.SendAsync(
            DisputeRequest(seeded.AllocationId.Value, "Hold the allocation until commit.", Guid.NewGuid()));
        int registrationSession = await save.PausedSession.Task.WaitAsync(WaitLimit);
        Task<HttpResponseMessage> reversal = client.SendAsync(
            ReverseRequest(seeded.AllocationId.Value, reversalKey));
        int waiter = await WaitForBlockedSessionAsync(registrationSession);
        _output.WriteLine(await DescribeWaitingAllocationLockAsync(waiter, registrationSession));
        save.Resume.TrySetResult();

        HttpResponseMessage registered = await registration.WaitAsync(WaitLimit);
        HttpResponseMessage reversed = await reversal.WaitAsync(WaitLimit);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        Assert.Equal(HttpStatusCode.OK, reversed.StatusCode);
        FinancialState after = await ReadStateAsync(seeded.AllocationId);
        Assert.Equal(AllocationStatus.Reversed, after.Status);
        Assert.Equal(seeded.State.InvoiceAllocated - 44m, after.InvoiceAllocated);
        Assert.Equal(seeded.State.InvoiceOutstanding + 44m, after.InvoiceOutstanding);
        Assert.Equal(seeded.State.BankAllocated - 44m, after.BankAllocated);
        Assert.Equal(seeded.State.BankUnapplied + 44m, after.BankUnapplied);
        Assert.Equal(1, await CountDisputesAsync(organizationId));
        await using ReconciliationDbContext context = _fixture.CreateContext();
        FinancialAuditEntry audit = await context.FinancialAuditEntries.SingleAsync(entry =>
            entry.AllocationId == seeded.AllocationId && entry.Action == FinancialAuditAction.Reversal);
        Assert.Equal("operator-a", audit.ActorId);
        Assert.Equal(reversalKey, audit.IdempotencyKey);
    }

    private FinOpsDeskApiFactory CreateFactory(
        DisputeLookupPause? lookup,
        AllocationDisputeSavePause? save,
        CollisionObservation? collision = null)
    {
        return new FinOpsDeskApiFactory(_fixture.ConnectionString, services =>
        {
            if (lookup is not null)
            {
                services.AddSingleton(lookup);
                services.RemoveAll<IAllocationDisputeLookup>();
                services.AddScoped<IAllocationDisputeLookup>(provider => new PausingAllocationDisputeLookup(
                    provider.GetRequiredService<AllocationDisputeStore>(),
                    provider.GetRequiredService<DisputeLookupPause>(),
                    collision,
                    ReadSessionWriteLocksAsync));
            }

            if (save is not null)
            {
                services.AddSingleton(save);
                services.RemoveAll<IUnitOfWork>();
                services.AddScoped<IUnitOfWork>(provider => new AllocationDisputeSaveGate(
                    provider.GetRequiredService<ReconciliationDbContext>(),
                    provider.GetRequiredService<AllocationDisputeSavePause>()));
            }

            if (collision is not null)
            {
                services.AddSingleton(collision);
                services.RemoveAll<IAllocationDisputeConflict>();
                services.AddScoped<IAllocationDisputeConflict>(_ => new ObservingAllocationDisputeConflict(
                    new AllocationDisputeConflict(),
                    collision));
            }
        });
    }

    private static HttpClient Client(FinOpsDeskApiFactory factory, OrganizationId organizationId, string? subject)
    {
        return factory.CreateApiClient(TestAccessTokens.CreateForActor(organizationId.Value, subject));
    }

    private async Task<Seeded> SeedAsync(OrganizationId organizationId, decimal amount)
    {
        string number = "INV-" + Guid.NewGuid().ToString("N")[..8];
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, amount, number);
        BankTransaction bank = PersistenceTestData.CreateBankTransaction(organizationId, amount, reference: number);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.AddRange(invoice, bank);
        await context.SaveChangesAsync();
        Invoice storedInvoice = await context.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        BankTransaction storedBank = await context.BankTransactions.SingleAsync(candidate => candidate.Id == bank.Id);
        Assert.True(AllocationConfirmation.Apply(
            storedInvoice,
            storedBank,
            new Money(amount, PersistenceTestData.Pln)).IsSuccess);
        Allocation allocation = new(
            AllocationId.New(),
            organizationId,
            storedInvoice.Id,
            storedBank.Id,
            new Money(amount, PersistenceTestData.Pln),
            ConfirmedAt,
            Guid.NewGuid(),
            AllocationIdempotencyOperation.ExplicitAmount);
        context.Allocations.Add(allocation);
        await context.SaveChangesAsync();
        await context.Entry(allocation).ReloadAsync();
        byte[] rowVersion = context.Entry(allocation)
            .Property<byte[]>(AllocationConfiguration.RowVersionPropertyName)
            .CurrentValue!
            .ToArray();
        return new Seeded(allocation.Id, rowVersion, await ReadStateAsync(allocation.Id));
    }

    private async Task<FinancialState> ReadStateAsync(AllocationId allocationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Allocation allocation = await context.Allocations.SingleAsync(candidate => candidate.Id == allocationId);
        Invoice invoice = await context.Invoices.SingleAsync(candidate => candidate.Id == allocation.InvoiceId);
        BankTransaction bank = await context.BankTransactions.SingleAsync(
            candidate => candidate.Id == allocation.BankTransactionId);
        int audits = await context.FinancialAuditEntries.CountAsync(entry => entry.AllocationId == allocationId);
        byte[] rowVersion = context.Entry(allocation)
            .Property<byte[]>(AllocationConfiguration.RowVersionPropertyName)
            .CurrentValue!
            .ToArray();
        return new FinancialState(
            allocation.Status,
            invoice.AllocatedAmount.Amount,
            invoice.OutstandingAmount.Amount,
            bank.AllocatedAmount.Amount,
            bank.UnappliedAmount.Amount,
            audits,
            rowVersion);
    }

    private async Task<int> CountDisputesAsync(OrganizationId organizationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.AllocationDisputes.CountAsync(dispute => dispute.OrganizationId == organizationId);
    }

    private async Task<int> WaitForBlockedSessionAsync(int blockingSessionId)
    {
        using CancellationTokenSource timeout = new(WaitLimit);
        while (!timeout.IsCancellationRequested)
        {
            await using SqlConnection connection = new(_fixture.ConnectionString);
            await connection.OpenAsync(timeout.Token);
            await using SqlCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT session_id
                FROM sys.dm_exec_requests
                WHERE blocking_session_id = @blockingSessionId
                """;
            command.Parameters.Add(new SqlParameter("@blockingSessionId", blockingSessionId));
            object? value = await command.ExecuteScalarAsync(timeout.Token);
            if (value is not null and not DBNull)
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }

            await Task.Yield();
        }

        throw new TimeoutException(
            "No request was blocked by session " + blockingSessionId.ToString(CultureInfo.InvariantCulture)
            + ". " + await ReadLockSnapshotAsync());
    }

    private async Task<string> DescribeWaitingAllocationLockAsync(int sessionId, int blockingSessionId)
    {
        await using SqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT l.resource_type, l.request_mode, l.request_status,
                   COALESCE(OBJECT_NAME(p.object_id), OBJECT_NAME(l.resource_associated_entity_id))
            FROM sys.dm_tran_locks AS l
            LEFT JOIN sys.partitions AS p
                ON l.resource_associated_entity_id = p.hobt_id
                OR l.resource_associated_entity_id = p.partition_id
            WHERE l.resource_database_id = DB_ID()
              AND l.request_session_id = @sessionId
              AND l.request_status = N'WAIT'
              AND l.resource_type IN (N'KEY', N'RID', N'PAGE', N'OBJECT')
              AND COALESCE(p.object_id, l.resource_associated_entity_id) = OBJECT_ID(N'reconciliation.Allocations')
            """;
        command.Parameters.Add(new SqlParameter("@sessionId", sessionId));
        await using SqlDataReader reader = await command.ExecuteReaderAsync();
        StringBuilder waiting = new();
        while (await reader.ReadAsync())
        {
            waiting.Append(reader.GetString(0)).Append(' ')
                .Append(reader.GetString(1)).Append(' ')
                .Append(reader.GetString(2)).Append(' ')
                .Append(reader.IsDBNull(3) ? "?" : reader.GetString(3))
                .Append("; ");
        }

        string description = "session " + sessionId.ToString(CultureInfo.InvariantCulture)
            + " blocking_session_id " + blockingSessionId.ToString(CultureInfo.InvariantCulture)
            + " allocation locks: " + waiting;
        Assert.True(waiting.Length > 0, description + await ReadLockSnapshotAsync());
        return description;
    }

    private async Task<string?> ReadSessionWriteLocksAsync(int sessionId)
    {
        if (sessionId == 0)
        {
            return "waiter session was not captured";
        }

        await using SqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT l.resource_type, l.request_mode, l.request_status
            FROM sys.dm_tran_locks AS l
            LEFT JOIN sys.partitions AS p
                ON l.resource_associated_entity_id = p.hobt_id
                OR l.resource_associated_entity_id = p.partition_id
            WHERE l.resource_database_id = DB_ID()
              AND l.request_session_id = @sessionId
              AND l.request_mode IN (N'U', N'X', N'IU', N'IX')
              AND COALESCE(
                    p.object_id,
                    CASE WHEN l.resource_type = N'OBJECT' THEN l.resource_associated_entity_id ELSE NULL END)
                  IN (
                      OBJECT_ID(N'reconciliation.Allocations'),
                      OBJECT_ID(N'reconciliation.AllocationDisputes'))
            """;
        command.Parameters.Add(new SqlParameter("@sessionId", sessionId));
        await using SqlDataReader reader = await command.ExecuteReaderAsync();
        StringBuilder snapshot = new();
        while (await reader.ReadAsync())
        {
            snapshot.Append(reader.GetString(0)).Append(' ')
                .Append(reader.GetString(1)).Append(' ')
                .Append(reader.GetString(2)).Append("; ");
        }

        return snapshot.Length == 0
            ? null
            : "session " + sessionId.ToString(CultureInfo.InvariantCulture) + " still holds " + snapshot;
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(code, await ReadCodeAsync(response));
    }

    private async Task<string> ReadLockSnapshotAsync()
    {
        await using SqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT resource_type, request_mode, request_status, request_session_id, resource_description
            FROM sys.dm_tran_locks
            WHERE resource_database_id = DB_ID()
              AND request_session_id <> @@SPID
              AND request_status = N'WAIT'
            """;
        await using SqlDataReader reader = await command.ExecuteReaderAsync();
        StringBuilder snapshot = new();
        while (await reader.ReadAsync())
        {
            snapshot.Append(reader.GetString(0)).Append(' ')
                .Append(reader.GetString(1)).Append(' ')
                .Append(reader.GetString(2)).Append(' ')
                .Append(Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture))
                .Append(' ')
                .Append(reader.IsDBNull(4) ? "" : reader.GetString(4))
                .Append("; ");
        }

        return snapshot.Length == 0 ? "no locks" : snapshot.ToString();
    }

    private static async Task<int> LockAllocationAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        OrganizationId organizationId,
        AllocationId allocationId)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT [Status]
            FROM [reconciliation].[Allocations] WITH (UPDLOCK, ROWLOCK)
            WHERE [OrganizationId] = @organizationId AND [Id] = @allocationId;
            SELECT @@SPID;
            """;
        AddAllocationParameters(command, organizationId, allocationId);
        await using SqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        return Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
    }

    private static void AddAllocationParameters(
        SqlCommand command,
        OrganizationId organizationId,
        AllocationId allocationId)
    {
        command.Parameters.Add(new SqlParameter("@organizationId", SqlDbType.UniqueIdentifier)
        {
            Value = organizationId.Value,
        });
        command.Parameters.Add(new SqlParameter("@allocationId", SqlDbType.UniqueIdentifier)
        {
            Value = allocationId.Value,
        });
    }

    private static async Task<string> ReadCodeAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString() ?? string.Empty;
    }

    private static HttpRequestMessage DisputeRequest(Guid allocationId, string statement, Guid idempotencyKey)
    {
        return DisputeRaw(
            allocationId,
            "{\"statement\":" + JsonSerializer.Serialize(statement) + "}",
            idempotencyKey.ToString("D"));
    }

    private static HttpRequestMessage DisputeRaw(Guid allocationId, string json, string? idempotencyKey)
    {
        HttpRequestMessage request = new(
            HttpMethod.Post,
            "/api/allocations/" + allocationId.ToString("D") + "/disputes")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        return request;
    }

    private static HttpRequestMessage ReverseRequest(Guid allocationId, Guid idempotencyKey)
    {
        HttpRequestMessage request = new(
            HttpMethod.Post,
            "/api/allocations/" + allocationId.ToString("D") + "/reverse")
        {
            Content = new StringContent("{\"reason\":\"customer refund\"}", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey.ToString("D"));
        return request;
    }

    private sealed record Seeded(AllocationId AllocationId, byte[] RowVersion, FinancialState State);

    private sealed record FinancialState(
        AllocationStatus Status,
        decimal InvoiceAllocated,
        decimal InvoiceOutstanding,
        decimal BankAllocated,
        decimal BankUnapplied,
        int AuditCount,
        byte[] RowVersion);

    private sealed class DisputeLookupPause
    {
        private int _misses;

        public bool PauseFirstMiss { get; init; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ShouldPauseAfterMiss()
        {
            return PauseFirstMiss && Interlocked.Increment(ref _misses) == 1;
        }
    }

    private sealed class AllocationDisputeSavePause
    {
        private int _paused;

        public TaskCompletionSource<int> PausedSession { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryMarkFirstPause()
        {
            return Interlocked.Increment(ref _paused) == 1;
        }
    }

    private sealed class CollisionObservation
    {
        private int _recognized;
        private int _recoveryReads;
        private int _lockChecks;
        private int _waiterSession;
        private int _checkedSession;

        public int RecognizedCollisions => Volatile.Read(ref _recognized);

        public int RecoveryReads => Volatile.Read(ref _recoveryReads);

        public int LockChecks => Volatile.Read(ref _lockChecks);

        public int CheckedSession => Volatile.Read(ref _checkedSession);

        public int WaiterSession => Volatile.Read(ref _waiterSession);

        public string? LockViolation { get; private set; }

        public void SetWaiterSession(int sessionId)
        {
            Volatile.Write(ref _waiterSession, sessionId);
        }

        public void Recognize(Exception exception)
        {
            if (IsNamedIndexCollision(exception))
            {
                Interlocked.Increment(ref _recognized);
            }
        }

        public bool HasRecognizedCollision => Volatile.Read(ref _recognized) > 0;

        public void RecordWinnerRead()
        {
            Interlocked.Increment(ref _recoveryReads);
        }

        public void RecordLockCheck(int sessionId, string? violation)
        {
            Volatile.Write(ref _checkedSession, sessionId);
            LockViolation = violation;
            Interlocked.Increment(ref _lockChecks);
        }

        private static bool IsNamedIndexCollision(Exception exception)
        {
            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                if (current is SqlException sqlException
                    && sqlException.Number is 2601 or 2627
                    && sqlException.Message.Contains(
                        "UX_AllocationDisputes_OrganizationId_IdempotencyKey",
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private sealed class ObservingAllocationDisputeConflict : IAllocationDisputeConflict
    {
        private readonly AllocationDisputeConflict _inner;
        private readonly CollisionObservation _observation;

        public ObservingAllocationDisputeConflict(
            AllocationDisputeConflict inner,
            CollisionObservation observation)
        {
            _inner = inner;
            _observation = observation;
        }

        public bool IsIdempotencyKeyConflict(Exception exception)
        {
            bool recognized = _inner.IsIdempotencyKeyConflict(exception);
            if (recognized)
            {
                _observation.Recognize(exception);
            }

            return recognized;
        }
    }

    private sealed class PausingAllocationDisputeLookup : IAllocationDisputeLookup
    {
        private readonly AllocationDisputeStore _inner;
        private readonly DisputeLookupPause _pause;
        private readonly CollisionObservation? _collision;
        private readonly Func<int, Task<string?>> _readSessionWriteLocks;

        public PausingAllocationDisputeLookup(
            AllocationDisputeStore inner,
            DisputeLookupPause pause,
            CollisionObservation? collision,
            Func<int, Task<string?>> readSessionWriteLocks)
        {
            _inner = inner;
            _pause = pause;
            _collision = collision;
            _readSessionWriteLocks = readSessionWriteLocks;
        }

        public async Task<AllocationDispute?> FindByIdempotencyKeyAsync(
            OrganizationId organizationId,
            Guid idempotencyKey,
            CancellationToken cancellationToken)
        {
            bool recovery = _collision is not null && _collision.HasRecognizedCollision;
            if (recovery)
            {
                int waiter = _collision!.WaiterSession;
                _collision.RecordLockCheck(waiter, await _readSessionWriteLocks(waiter));
            }

            AllocationDispute? found = await _inner.FindByIdempotencyKeyAsync(
                organizationId,
                idempotencyKey,
                cancellationToken);
            if (recovery && found is not null)
            {
                _collision!.RecordWinnerRead();
            }

            if (found is null && _pause.ShouldPauseAfterMiss())
            {
                _pause.Entered.TrySetResult();
                await _pause.Resume.Task.WaitAsync(cancellationToken);
            }

            return found;
        }
    }

    private sealed class AllocationDisputeSaveGate : IUnitOfWork
    {
        private readonly ReconciliationDbContext _context;
        private readonly AllocationDisputeSavePause _pause;

        public AllocationDisputeSaveGate(ReconciliationDbContext context, AllocationDisputeSavePause pause)
        {
            _context = context;
            _pause = pause;
        }

        public async Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            bool disputeInsert = _context.ChangeTracker
                .Entries<AllocationDispute>()
                .Any(entry => entry.State == EntityState.Added);
            if (disputeInsert && _pause.TryMarkFirstPause())
            {
                int sessionId = await ReadSessionIdAsync(cancellationToken);
                _pause.PausedSession.TrySetResult(sessionId);
                await _pause.Resume.Task.WaitAsync(cancellationToken);
            }

            await ((IUnitOfWork)_context).SaveChangesAsync(cancellationToken);
        }

        private async Task<int> ReadSessionIdAsync(CancellationToken cancellationToken)
        {
            System.Data.Common.DbConnection connection = _context.Database.GetDbConnection();
            await using System.Data.Common.DbCommand command = connection.CreateCommand();
            command.Transaction = _context.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = "SELECT @@SPID";
            object? value = await command.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
    }
}