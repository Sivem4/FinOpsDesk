using Microsoft.Playwright;

namespace FinOpsDesk.E2ETests;

[Collection(E2ECollection.Name)]
public sealed class ExplicitAmountRecoveryTests
{
    [Fact]
    public async Task Committed_explicit_amount_survives_a_lost_response_and_replays()
    {
        await E2ETestRun.RunAsync(TimeSpan.FromSeconds(240), async host =>
        {
            E2EPair pair = E2EWorkspace.WritePairs(host.FixtureDirectory, 1)[0];
            await host.SignInAsync();
            await E2EWorkspace.ImportAsync(host.Page, host.FixtureDirectory, 1);
            (E2ECapturedRequest first, Guid allocationId, Guid invoiceId, Guid bankTransactionId) =
                await E2ERecovery.LoseExplicitAmountAsync(host, pair);
            await using (E2EPostWatch watch = await E2ERecovery.WatchReloadAndSignInAsync(
                host,
                E2ELostResponse.ExplicitAmountUrl))
            {
                await Assertions.Expect(host.Page.GetByText(
                    "An allocation attempt is stored for this organization")).ToBeVisibleAsync();
                await E2EBrowser.RequireStoredRequestAsync(host.Page, E2EBrowser.ExplicitAmountAttemptKey, first);
                Assert.Equal(0, watch.Count);
            }

            await E2ERecovery.ReplayExplicitAmountAsync(host.Page, first, allocationId);
            await E2EFinance.RequirePartialAsync(
                host.ConnectionString,
                host.OrganizationId,
                allocationId,
                invoiceId,
                bankTransactionId,
                allocationCount: 1);
            host.AssertBrowserIsQuiet(allowAbortedRequests: true);
        });
    }
}