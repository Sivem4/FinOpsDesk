using System.Text.RegularExpressions;

using Microsoft.Playwright;

namespace FinOpsDesk.E2ETests;

internal static class E2EHistory
{
    public static async Task<ILocator> OpenReverseAsync(IPage page, E2EPair pair, Guid allocationId)
    {
        await page.GetByRole(AriaRole.Link, new() { Name = "Allocation history", Exact = true }).ClickAsync();
        ILocator table = page.GetByRole(AriaRole.Table, new() { Name = "Allocation history" });
        await Assertions.Expect(table.GetByText(pair.InvoiceNumber, new() { Exact = true })).ToHaveCountAsync(2);
        await table.GetByRole(
            AriaRole.Button,
            new() { Name = "Reverse allocation " + allocationId.ToString("D") }).ClickAsync();
        ILocator dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Reverse allocation" });
        await Assertions.Expect(dialog.GetByText("Allocation " + allocationId.ToString("D"))).ToBeVisibleAsync();
        await Assertions.Expect(dialog.GetByText("Invoice number " + pair.InvoiceNumber)).ToBeVisibleAsync();
        await Assertions.Expect(dialog.GetByText("Status Confirmed")).ToBeVisibleAsync();
        await Assertions.Expect(dialog.GetByText(new Regex(@"Original amount\s+40\.25(00)? PLN"))).ToBeVisibleAsync();
        return dialog;
    }

    public static async Task ExpectReversedAsync(IPage page, E2EPair pair, Guid allocationId, string reason)
    {
        await page.GetByRole(AriaRole.Link, new() { Name = "Allocation history", Exact = true }).ClickAsync();
        ILocator table = page.GetByRole(AriaRole.Table, new() { Name = "Allocation history" });
        await Assertions.Expect(table.GetByText(pair.InvoiceNumber, new() { Exact = true })).ToHaveCountAsync(2);
        await Assertions.Expect(table.GetByRole(AriaRole.Cell, new() { Name = "Reversed", Exact = true })).ToHaveCountAsync(1);
        await Assertions.Expect(table.GetByText(new Regex(@"40\.25(00)? PLN"))).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(
            AriaRole.Button,
            new() { Name = "Reverse allocation " + allocationId.ToString("D") })).ToHaveCountAsync(0);
        await table.GetByRole(AriaRole.Button, new() { Name = "View audit" }).ClickAsync();
        ILocator audit = page.GetByRole(AriaRole.Table, new() { NameRegex = new Regex("^Audit entries for allocation ") });
        await Assertions.Expect(audit.GetByRole(AriaRole.Cell, new() { Name = "Confirmation", Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(audit.GetByRole(AriaRole.Cell, new() { Name = "Exact match", Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(audit.GetByRole(AriaRole.Cell, new() { Name = "Reversal", Exact = true })).ToHaveCountAsync(2);
        await Assertions.Expect(audit.GetByRole(AriaRole.Cell, new() { Name = E2EAccessToken.ActorId, Exact = true })).ToHaveCountAsync(2);
        await Assertions.Expect(audit.GetByRole(AriaRole.Cell, new() { Name = reason, Exact = true })).ToBeVisibleAsync();
    }
}