using Microsoft.Playwright;

namespace FinOpsDesk.E2ETests;

[Collection(E2ECollection.Name)]
public sealed class RecoveryIsolationTests
{
    [Fact]
    public async Task Other_organization_cannot_retry_a_stored_reversal()
    {
        await E2ETestRun.RunAsync(TimeSpan.FromSeconds(240), async host =>
        {
            E2EPair pair = E2EWorkspace.WritePairs(host.FixtureDirectory, 1)[0];
            await host.SignInAsync();
            await E2EWorkspace.ImportAsync(host.Page, host.FixtureDirectory, 1);
            (Guid invoiceId, Guid bankTransactionId) = await E2EWorkspace.ConfirmExactAsync(host.Page, pair.InvoiceNumber);
            Guid allocationId = await E2EFinance.ConfirmedAllocationIdAsync(
                host.ConnectionString,
                host.OrganizationId,
                invoiceId,
                bankTransactionId);
            string reason = E2ERecovery.NewReason();
            E2ECapturedRequest first = await E2ERecovery.LoseReversalAsync(host, pair, allocationId, reason);
            string? stored = await E2EBrowser.ReadSessionAsync(host.Page, E2EBrowser.ReversalAttemptKey);
            Guid organizationB = Guid.NewGuid();
            string organizationBToken = host.CreateAccessToken(organizationB);

            await using (E2EPostWatch watch = await E2EPostWatch.StartAsync(host.Page, E2ELostResponse.ReversalUrl))
            {
                int beforeSwitch = watch.Count;
                await host.ReplaceTokenAsync(organizationBToken);
                await ExpectForeignReversalHiddenAsync(host.Page, pair, allocationId, reason);
                await host.Page.GetByRole(AriaRole.Link, new() { Name = "Reconciliation", Exact = true }).ClickAsync();
                await Assertions.Expect(host.Page.GetByText(
                    "A stored reversal belongs to another organization. Its details stay hidden.")).ToBeVisibleAsync();
                await ExpectRecoveryActionsHiddenAsync(host.Page);
                E2EBrowser.RequireUnchanged(
                    stored,
                    await E2EBrowser.ReadSessionAsync(host.Page, E2EBrowser.ReversalAttemptKey),
                    "The stored reversal");
                await E2EFinance.RequireNoFinancialRowsAsync(host.ConnectionString, organizationB);
                await E2EFinance.RequireReversedAsync(
                    host.ConnectionString,
                    host.OrganizationId,
                    allocationId,
                    E2EWorkspace.OpenAmount,
                    reason,
                    allocationCount: 1);
                Assert.Equal(beforeSwitch, watch.Count);

                await host.ReplaceWithCurrentOrganizationAsync();
                await host.Page.GetByRole(AriaRole.Link, new() { Name = "Allocation history", Exact = true }).ClickAsync();
                await Assertions.Expect(host.Page.GetByRole(
                    AriaRole.Button,
                    new() { Name = "Review reversal attempt" })).ToBeVisibleAsync();
                E2EBrowser.RequireUnchanged(
                    stored,
                    await E2EBrowser.ReadSessionAsync(host.Page, E2EBrowser.ReversalAttemptKey),
                    "The stored reversal");
                Assert.Equal(beforeSwitch, watch.Count);
            }

            await E2ERecovery.ReplayReversalAsync(host.Page, first, allocationId, reason);
            await E2EHistory.ExpectReversedAsync(host.Page, pair, allocationId, reason);
            await E2EFinance.RequireReversedAsync(
                host.ConnectionString,
                host.OrganizationId,
                allocationId,
                E2EWorkspace.OpenAmount,
                reason,
                allocationCount: 1);
            host.AssertBrowserIsQuiet(allowAbortedRequests: true);
        });
    }

    [Fact]
    public async Task Pending_reversal_and_explicit_amount_attempts_stay_independent()
    {
        await E2ETestRun.RunAsync(TimeSpan.FromSeconds(300), async host =>
        {
            IReadOnlyList<E2EPair> pairs = E2EWorkspace.WritePairs(host.FixtureDirectory, 3);
            E2EPair reversalPair = pairs[0];
            E2EPair explicitPair = pairs[1];
            E2EPair heldPair = pairs[2];
            await host.SignInAsync();
            await E2EWorkspace.ImportAsync(host.Page, host.FixtureDirectory, 3);
            Guid reversalAllocationId = await ConfirmAsync(host, reversalPair);
            Guid heldAllocationId = await ConfirmAsync(host, heldPair);
            (E2ECapturedRequest explicitRequest, Guid explicitAllocationId, Guid explicitInvoiceId, Guid explicitBankId) =
                await E2ERecovery.LoseExplicitAmountAsync(host, explicitPair);
            string reason = E2ERecovery.NewReason();
            E2ECapturedRequest reversalRequest = await E2ERecovery.LoseReversalAsync(
                host,
                reversalPair,
                reversalAllocationId,
                reason);
            string? explicitRaw = await E2EBrowser.ReadSessionAsync(host.Page, E2EBrowser.ExplicitAmountAttemptKey);
            string? reversalRaw = await E2EBrowser.ReadSessionAsync(host.Page, E2EBrowser.ReversalAttemptKey);
            Assert.False(string.IsNullOrEmpty(explicitRaw));
            Assert.False(string.IsNullOrEmpty(reversalRaw));
            Assert.NotEqual(explicitRaw, reversalRaw);

            await host.Page.GetByRole(AriaRole.Link, new() { Name = "Allocation history", Exact = true }).ClickAsync();
            await Assertions.Expect(host.Page.GetByText(
                "An unresolved reversal attempt is stored for this organization.")).ToBeVisibleAsync();
            await Assertions.Expect(host.Page.GetByText(
                "An unresolved allocation attempt is stored for this organization.")).ToBeVisibleAsync();
            await Assertions.Expect(host.Page.GetByRole(AriaRole.Link, new() { Name = "Open Reconciliation", Exact = true })).ToBeVisibleAsync();
            await host.Page.GetByRole(AriaRole.Link, new() { Name = "Reconciliation", Exact = true }).ClickAsync();
            await Assertions.Expect(host.Page.GetByText(
                "An unresolved reversal attempt is stored for this organization.")).ToBeVisibleAsync();
            await Assertions.Expect(host.Page.GetByRole(AriaRole.Link, new() { Name = "Open Allocation history", Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(host.Page.GetByText(
                "An allocation attempt is stored for this organization")).ToBeVisibleAsync();

            Guid organizationB = Guid.NewGuid();
            await using (E2EPostWatch watch = await E2EPostWatch.StartAsync(
                host.Page,
                E2ELostResponse.ReversalUrl,
                E2ELostResponse.ExplicitAmountUrl))
            {
                int beforeSwitch = watch.Count;
                await host.ReplaceTokenAsync(host.CreateAccessToken(organizationB));
                await host.Page.GetByRole(AriaRole.Link, new() { Name = "Allocation history", Exact = true }).ClickAsync();
                await ExpectBothForeignNoticesAsync(host.Page);
                await ExpectRecoveryActionsHiddenAsync(host.Page);
                await host.Page.GetByRole(AriaRole.Link, new() { Name = "Reconciliation", Exact = true }).ClickAsync();
                await ExpectBothForeignNoticesAsync(host.Page);
                E2EBrowser.RequireUnchanged(
                    explicitRaw,
                    await E2EBrowser.ReadSessionAsync(host.Page, E2EBrowser.ExplicitAmountAttemptKey),
                    "The stored allocation attempt");
                E2EBrowser.RequireUnchanged(
                    reversalRaw,
                    await E2EBrowser.ReadSessionAsync(host.Page, E2EBrowser.ReversalAttemptKey),
                    "The stored reversal");
                await E2EFinance.RequireNoFinancialRowsAsync(host.ConnectionString, organizationB);
                Assert.Equal(beforeSwitch, watch.Count);
                await host.ReplaceWithCurrentOrganizationAsync();
                Assert.Equal(beforeSwitch, watch.Count);
            }

            await E2ERecovery.ReplayReversalAsync(host.Page, reversalRequest, reversalAllocationId, reason);
            E2EBrowser.RequireUnchanged(
                explicitRaw,
                await E2EBrowser.ReadSessionAsync(host.Page, E2EBrowser.ExplicitAmountAttemptKey),
                "The stored allocation attempt");
            Assert.Null(await E2EBrowser.ReadSessionAsync(host.Page, E2EBrowser.ReversalAttemptKey));

            string heldReason = E2ERecovery.NewReason();
            await E2ERecovery.LoseReversalAsync(host, heldPair, heldAllocationId, heldReason);
            E2EBrowser.RequireUnchanged(
                explicitRaw,
                await E2EBrowser.ReadSessionAsync(host.Page, E2EBrowser.ExplicitAmountAttemptKey),
                "The stored allocation attempt");
            string? heldRaw = await E2EBrowser.ReadSessionAsync(host.Page, E2EBrowser.ReversalAttemptKey);
            Assert.False(string.IsNullOrEmpty(heldRaw));
            Assert.NotEqual(reversalRaw, heldRaw);

            await E2ERecovery.ReplayExplicitAmountAsync(host.Page, explicitRequest, explicitAllocationId);
            E2EBrowser.RequireUnchanged(
                heldRaw,
                await E2EBrowser.ReadSessionAsync(host.Page, E2EBrowser.ReversalAttemptKey),
                "The stored reversal");
            Assert.Null(await E2EBrowser.ReadSessionAsync(host.Page, E2EBrowser.ExplicitAmountAttemptKey));

            const string malformed = "{";
            await E2EBrowser.SetSessionAsync(host.Page, E2EBrowser.ExplicitAmountAttemptKey, malformed);
            await host.Page.GetByRole(AriaRole.Link, new() { Name = "Allocation history", Exact = true }).ClickAsync();
            await Assertions.Expect(host.Page.GetByText(
                "A stored allocation attempt cannot be read safely. It was not replaced.")).ToBeVisibleAsync();
            await Assertions.Expect(host.Page.GetByText(
                "An unresolved reversal attempt is stored for this organization.")).ToBeVisibleAsync();
            E2EBrowser.RequireUnchanged(
                malformed,
                await E2EBrowser.ReadSessionAsync(host.Page, E2EBrowser.ExplicitAmountAttemptKey),
                "The malformed allocation attempt");
            E2EBrowser.RequireUnchanged(
                heldRaw,
                await E2EBrowser.ReadSessionAsync(host.Page, E2EBrowser.ReversalAttemptKey),
                "The stored reversal");

            await host.Page.GetByRole(AriaRole.Link, new() { Name = "Reconciliation", Exact = true }).ClickAsync();
            await host.Page.GetByRole(AriaRole.Button, new() { Name = "Choose records" }).ClickAsync();
            ILocator dialog = host.Page.GetByRole(AriaRole.Dialog, new() { Name = "Recover allocation attempt" });
            await Assertions.Expect(dialog.GetByText(
                "The stored allocation attempt cannot be read safely. It was not sent.")).ToBeVisibleAsync();
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Close" }).ClickAsync();
            E2EBrowser.RequireUnchanged(
                malformed,
                await E2EBrowser.ReadSessionAsync(host.Page, E2EBrowser.ExplicitAmountAttemptKey),
                "The malformed allocation attempt");
            E2EBrowser.RequireUnchanged(
                heldRaw,
                await E2EBrowser.ReadSessionAsync(host.Page, E2EBrowser.ReversalAttemptKey),
                "The stored reversal");

            await E2EFinance.RequireReversedAsync(
                host.ConnectionString,
                host.OrganizationId,
                reversalAllocationId,
                E2EWorkspace.OpenAmount,
                reason,
                allocationCount: 3);
            await E2EFinance.RequirePartialAsync(
                host.ConnectionString,
                host.OrganizationId,
                explicitAllocationId,
                explicitInvoiceId,
                explicitBankId);
            await E2EFinance.RequireReversedAsync(
                host.ConnectionString,
                host.OrganizationId,
                heldAllocationId,
                E2EWorkspace.OpenAmount,
                heldReason,
                allocationCount: 3);
            host.AssertBrowserIsQuiet(allowAbortedRequests: true);
        });
    }

    private static async Task<Guid> ConfirmAsync(E2EHost host, E2EPair pair)
    {
        (Guid invoiceId, Guid bankTransactionId) = await E2EWorkspace.ConfirmExactAsync(host.Page, pair.InvoiceNumber);
        return await E2EFinance.ConfirmedAllocationIdAsync(
            host.ConnectionString,
            host.OrganizationId,
            invoiceId,
            bankTransactionId);
    }

    private static async Task ExpectForeignReversalHiddenAsync(
        IPage page,
        E2EPair pair,
        Guid allocationId,
        string reason)
    {
        await page.GetByRole(AriaRole.Link, new() { Name = "Allocation history", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByText(
            "A stored reversal belongs to another organization. Its details stay hidden.")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("No allocations are in this organization yet.")).ToBeVisibleAsync();
        await ExpectRecoveryActionsHiddenAsync(page);
        string text = await page.Locator("body").InnerTextAsync();
        Assert.DoesNotContain(pair.InvoiceNumber, text, StringComparison.Ordinal);
        Assert.DoesNotContain(allocationId.ToString("D"), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(reason, text, StringComparison.Ordinal);
    }

    private static async Task ExpectBothForeignNoticesAsync(IPage page)
    {
        await Assertions.Expect(page.GetByText(
            "A stored reversal belongs to another organization. Its details stay hidden.")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText(
            "A stored allocation attempt belongs to another organization. Its details stay hidden.")).ToBeVisibleAsync();
        await ExpectRecoveryActionsHiddenAsync(page);
    }

    private static async Task ExpectRecoveryActionsHiddenAsync(IPage page)
    {
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Review reversal attempt" })).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Send the same reversal again" })).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Remove local tracking" })).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Remove local tracking now" })).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Stop tracking this attempt" })).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Send the same allocation again" })).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Review" })).ToHaveCountAsync(0);
    }
}