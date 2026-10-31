using Microsoft.Playwright;

namespace FinOpsDesk.E2ETests;

[Collection(E2ECollection.Name)]
public sealed class ReversalRecoveryTests
{
    [Fact]
    public async Task Confirmed_allocation_reverses_once()
    {
        await E2ETestRun.RunAsync(TimeSpan.FromSeconds(180), async host =>
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
            ILocator dialog = await E2EHistory.OpenReverseAsync(host.Page, pair, allocationId);
            await dialog.GetByLabel("Reversal reason").FillAsync(reason);
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Reverse this allocation" }).ClickAsync();
            await Assertions.Expect(dialog.GetByText("Reversal recorded.")).ToBeVisibleAsync();
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Close" }).ClickAsync();
            await Assertions.Expect(dialog).ToBeHiddenAsync();
            await E2EHistory.ExpectReversedAsync(host.Page, pair, allocationId, reason);
            await E2EFinance.RequireReversedAsync(
                host.ConnectionString,
                host.OrganizationId,
                allocationId,
                E2EWorkspace.OpenAmount,
                reason,
                allocationCount: 1);
            host.AssertBrowserIsQuiet();
        });
    }

    [Fact]
    public async Task Committed_reversal_survives_a_lost_response_and_replays()
    {
        await E2ETestRun.RunAsync(TimeSpan.FromSeconds(180), async host =>
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
            await using (E2EPostWatch watch = await E2ERecovery.WatchReloadAndSignInAsync(host, E2ELostResponse.ReversalUrl))
            {
                await Assertions.Expect(host.Page.GetByText(
                    "An unresolved reversal attempt is stored for this organization.")).ToBeVisibleAsync();
                await E2EBrowser.RequireStoredRequestAsync(host.Page, E2EBrowser.ReversalAttemptKey, first);
                Assert.Equal(0, watch.Count);
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
}