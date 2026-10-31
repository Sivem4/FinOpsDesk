using System.Text.RegularExpressions;

using Microsoft.Playwright;

namespace FinOpsDesk.E2ETests;

internal static class E2ERecovery
{
    public static async Task<E2ECapturedRequest> LoseReversalAsync(
        E2EHost host,
        E2EPair pair,
        Guid allocationId,
        string reason)
    {
        ILocator dialog = await E2EHistory.OpenReverseAsync(host.Page, pair, allocationId);
        await dialog.GetByLabel("Reversal reason").FillAsync(reason);
        E2ECapturedRequest captured;
        await using (E2ELossSession loss = await E2ELossSession.ArmAsync(
            host.Page,
            E2ELostResponse.ReversalUrl,
            () => E2EFinance.IsAllocationReversedAsync(host.ConnectionString, host.OrganizationId, allocationId),
            (request, response) => E2ELostResponse.ProveReversalCommitAsync(
                host,
                request,
                response,
                allocationId,
                reason)))
        {
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Reverse this allocation" }).ClickAsync();
            captured = await loss.WaitAsync();
            Assert.Equal(1, loss.PostCount);
            Assert.Equal(0, loss.UnexpectedPosts);
            ILocator outcome = host.Page.GetByRole(AriaRole.Dialog, new() { Name = "Recover reversal attempt" });
            await Assertions.Expect(outcome.GetByText(E2ELostResponse.UnknownReversal)).ToBeVisibleAsync();
            await Assertions.Expect(outcome.GetByText("Reversal recorded.")).ToHaveCountAsync(0);
            await E2EBrowser.RequireStoredRequestAsync(host.Page, E2EBrowser.ReversalAttemptKey, captured);
            Assert.Equal(0, loss.UnexpectedPosts);
            await outcome.GetByRole(AriaRole.Button, new() { Name = "Close" }).ClickAsync();
            await Assertions.Expect(outcome).ToBeHiddenAsync();
        }
        return captured;
    }

    public static async Task ReplayReversalAsync(
        IPage page,
        E2ECapturedRequest original,
        Guid allocationId,
        string reason)
    {
        await page.GetByRole(AriaRole.Link, new() { Name = "Allocation history", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Review reversal attempt" }).ClickAsync();
        ILocator dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Recover reversal attempt" });
        await Assertions.Expect(dialog.GetByText("Allocation " + allocationId.ToString("D"))).ToBeVisibleAsync();
        await E2ELostResponse.ReplayAsync(
            page,
            E2ELostResponse.ReversalUrl,
            original,
            () => dialog.GetByRole(AriaRole.Button, new() { Name = "Send the same reversal again" }).ClickAsync(),
            response => E2ELostResponse.RequireReplayAsync(
                response,
                allocationId,
                E2EWorkspace.OpenAmount,
                "reversed",
                reason));
        ILocator completed = page.GetByRole(AriaRole.Dialog);
        await Assertions.Expect(completed.GetByText(
            "This reversal was already recorded for the same request.")).ToBeVisibleAsync();
        await Assertions.Expect(completed.GetByText("Reversal recorded.", new() { Exact = true })).ToHaveCountAsync(0);
        await Assertions.Expect(completed.GetByRole(AriaRole.Button, new() { Name = "Reverse this allocation" })).ToHaveCountAsync(0);
        Assert.Null(await E2EBrowser.ReadSessionAsync(page, E2EBrowser.ReversalAttemptKey));
        await completed.GetByRole(AriaRole.Button, new() { Name = "Close" }).ClickAsync();
        await Assertions.Expect(completed).ToBeHiddenAsync();
    }

    public static string NewReason()
    {
        return "E2E reversal " + Guid.NewGuid().ToString("N");
    }

    public static async Task<(E2ECapturedRequest Request, Guid AllocationId, Guid InvoiceId, Guid BankTransactionId)> LoseExplicitAmountAsync(
        E2EHost host,
        E2EPair pair)
    {
        E2EExplicitDialog opened = await E2EWorkspace.OpenExplicitAmountDialogAsync(host.Page, pair.InvoiceNumber);
        Guid allocationId = Guid.Empty;
        E2ECapturedRequest captured;
        await using (E2ELossSession loss = await E2ELossSession.ArmAsync(
            host.Page,
            E2ELostResponse.ExplicitAmountUrl,
            () => E2EFinance.HasPartialAllocationAsync(
                host.ConnectionString,
                host.OrganizationId,
                opened.InvoiceId,
                E2EWorkspace.PartialAmount),
            async (request, response) =>
            {
                allocationId = await E2ELostResponse.ProveExplicitCommitAsync(
                    host,
                    request,
                    response,
                    opened.InvoiceId,
                    opened.BankTransactionId);
            }))
        {
            await opened.Dialog.GetByRole(AriaRole.Button, new() { Name = "Allocate this amount" }).ClickAsync();
            captured = await loss.WaitAsync();
            Assert.Equal(1, loss.PostCount);
            Assert.Equal(0, loss.UnexpectedPosts);
            Assert.NotEqual(Guid.Empty, allocationId);
            ILocator outcome = host.Page.GetByRole(AriaRole.Dialog, new() { Name = "Recover allocation attempt" });
            await Assertions.Expect(outcome.GetByText(E2ELostResponse.UnknownAllocation)).ToBeVisibleAsync();
            await Assertions.Expect(outcome.GetByText("Allocation recorded.", new() { Exact = true })).ToHaveCountAsync(0);
            await E2EBrowser.RequireStoredRequestAsync(host.Page, E2EBrowser.ExplicitAmountAttemptKey, captured);
            Assert.Equal(0, loss.UnexpectedPosts);
            await outcome.GetByRole(AriaRole.Button, new() { Name = "Close" }).ClickAsync();
            await Assertions.Expect(outcome).ToBeHiddenAsync();
        }
        return (captured, allocationId, opened.InvoiceId, opened.BankTransactionId);
    }

    public static async Task ReplayExplicitAmountAsync(
        IPage page,
        E2ECapturedRequest original,
        Guid allocationId)
    {
        await page.GetByRole(AriaRole.Link, new() { Name = "Reconciliation", Exact = true }).ClickAsync();
        ILocator banner = page.GetByRole(AriaRole.Alert).Filter(new()
        {
            HasText = "An allocation attempt is stored for this organization",
        });
        await banner.GetByRole(AriaRole.Button, new() { Name = "Review" }).ClickAsync();
        ILocator dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Recover allocation attempt" });
        await E2ELostResponse.ReplayAsync(
            page,
            E2ELostResponse.ExplicitAmountUrl,
            original,
            () => dialog.GetByRole(AriaRole.Button, new() { Name = "Send the same allocation again" }).ClickAsync(),
            response => E2ELostResponse.RequireReplayAsync(
                response,
                allocationId,
                E2EWorkspace.PartialAmount,
                status: null,
                reason: null));
        ILocator completed = page.GetByRole(AriaRole.Dialog);
        await Assertions.Expect(completed.GetByText("This allocation was already recorded.")).ToBeVisibleAsync();
        await Assertions.Expect(completed.GetByText("Allocation recorded.", new() { Exact = true })).ToHaveCountAsync(0);
        await Assertions.Expect(completed.GetByRole(AriaRole.Button, new() { Name = "Allocate this amount" })).ToHaveCountAsync(0);
        Assert.Null(await E2EBrowser.ReadSessionAsync(page, E2EBrowser.ExplicitAmountAttemptKey));
        await completed.GetByRole(AriaRole.Button, new() { Name = "Close" }).ClickAsync();
        await Assertions.Expect(completed).ToBeHiddenAsync();
    }

    public static async Task<E2EPostWatch> WatchReloadAndSignInAsync(E2EHost host, params Regex[] urls)
    {
        E2EPostWatch watch = await E2EPostWatch.StartAsync(host.Page, urls);
        int baseline = watch.Count;
        await host.Page.ReloadAsync();
        await Assertions.Expect(host.Page.GetByLabel("Development access token")).ToBeVisibleAsync();
        Assert.Equal(0, await E2EBrowser.LocalStorageLengthAsync(host.Page));
        await host.SignInThroughGateAsync();
        Assert.Equal(baseline, watch.Count);
        return watch;
    }
}