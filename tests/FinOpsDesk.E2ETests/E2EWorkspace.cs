using System.Text;
using System.Text.RegularExpressions;

using Microsoft.Playwright;

namespace FinOpsDesk.E2ETests;

internal sealed record E2EPair(string InvoiceNumber, string InvoiceExternalId, string BankExternalId);

internal sealed record E2EExplicitDialog(ILocator Dialog, Guid InvoiceId, Guid BankTransactionId);

internal static class E2EWorkspace
{
    public const decimal OpenAmount = 40.2500m;

    public const decimal PartialAmount = 10.0000m;

    public const decimal RemainingAmount = 30.2500m;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static IReadOnlyList<E2EPair> WritePairs(string directory, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        List<E2EPair> pairs = [];
        StringBuilder invoices = new("ExternalId,InvoiceNumber,Amount,Currency\n");
        StringBuilder banks = new("ExternalId,BookingDate,Amount,Currency,Reference\n");
        for (int index = 0; index < count; index++)
        {
            string token = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
            E2EPair pair = new("E2E" + token, "inv-" + token, "txn-" + token);
            pairs.Add(pair);
            invoices.Append(pair.InvoiceExternalId).Append(',').Append(pair.InvoiceNumber).Append(",40.2500,PLN\n");
            banks.Append(pair.BankExternalId).Append(",2026-10-31,40.2500,PLN,").Append(pair.InvoiceNumber).Append('\n');
        }

        File.WriteAllText(Path.Combine(directory, "invoices.csv"), invoices.ToString(), Utf8);
        File.WriteAllText(Path.Combine(directory, "bank.csv"), banks.ToString(), Utf8);
        return pairs;
    }

    public static async Task ImportAsync(IPage page, string directory, int pairCount)
    {
        await ImportFileAsync(
            page,
            "Invoices",
            Path.Combine(directory, "invoices.csv"),
            pairCount);
        await ImportFileAsync(
            page,
            "Bank transactions",
            Path.Combine(directory, "bank.csv"),
            pairCount);
    }

    public static async Task<(Guid InvoiceId, Guid BankTransactionId)> ConfirmExactAsync(
        IPage page,
        string invoiceNumber)
    {
        await page.GetByRole(AriaRole.Link, new() { Name = "Reconciliation", Exact = true }).ClickAsync();
        ILocator region = page.GetByRole(AriaRole.Region, new() { Name = "Exact matches" });
        ILocator row = region.GetByRole(AriaRole.Row).Filter(new() { HasText = invoiceNumber });
        await Assertions.Expect(row).ToHaveCountAsync(1);
        ILocator confirm = row.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Confirm exact match for invoice ") });
        string label = await confirm.GetAttributeAsync("aria-label")
            ?? throw new InvalidOperationException("Exact-1 confirm button has no accessible name.");
        Match match = Regex.Match(
            label,
            "^Confirm exact match for invoice (?<invoice>[0-9a-fA-F-]{36}) and bank transaction (?<bank>[0-9a-fA-F-]{36})$");
        Assert.True(match.Success, "Exact-1 confirm button did not name one invoice and one bank transaction.");
        Guid invoiceId = Guid.Parse(match.Groups["invoice"].Value);
        Guid bankTransactionId = Guid.Parse(match.Groups["bank"].Value);

        await confirm.ClickAsync();
        ILocator dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Confirm exact match" });
        await Assertions.Expect(dialog.GetByText("Invoice " + invoiceId.ToString("D"))).ToBeVisibleAsync();
        await Assertions.Expect(dialog.GetByText("Bank transaction " + bankTransactionId.ToString("D"))).ToBeVisibleAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Confirm allocation" }).ClickAsync();
        await Assertions.Expect(dialog.GetByText("Allocation recorded.")).ToBeVisibleAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Close" }).ClickAsync();
        await Assertions.Expect(dialog).ToBeHiddenAsync();
        ILocator done = page.GetByRole(AriaRole.Button, new() { Name = "Done", Exact = true });
        await Assertions.Expect(done).ToBeVisibleAsync();
        await done.ClickAsync();
        await Assertions.Expect(done).ToBeHiddenAsync();
        return (invoiceId, bankTransactionId);
    }

    public static async Task<E2EExplicitDialog> OpenExplicitAmountDialogAsync(IPage page, string invoiceNumber)
    {
        await page.GetByRole(AriaRole.Link, new() { Name = "Reconciliation", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Choose records" }).ClickAsync();
        ILocator chooser = page.GetByRole(AriaRole.Dialog, new() { Name = "Choose records" });
        ILocator invoiceButton = chooser.GetByRole(
            AriaRole.Button,
            new() { NameRegex = new Regex("^Select invoice " + Regex.Escape(invoiceNumber) + " ") });
        Guid invoiceId = ReadTrailingGuid(await invoiceButton.InnerTextAsync());
        await invoiceButton.ClickAsync();
        ILocator bankButton = chooser.GetByRole(
            AriaRole.Button,
            new() { NameRegex = new Regex("^Select bank transaction " + Regex.Escape(invoiceNumber) + " ") });
        Guid bankTransactionId = ReadTrailingGuid(await bankButton.InnerTextAsync());
        await bankButton.ClickAsync();
        await chooser.GetByRole(AriaRole.Button, new() { Name = "Review balances" }).ClickAsync();
        await Assertions.Expect(chooser).ToBeHiddenAsync();
        ILocator dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Allocate an amount" });
        await Assertions.Expect(dialog.GetByText(new Regex(@"Invoice outstanding\s+40\.25(00)? PLN"))).ToBeVisibleAsync();
        await dialog.GetByLabel("Allocation amount").FillAsync("10.0000");
        ILocator submit = dialog.GetByRole(AriaRole.Button, new() { Name = "Allocate this amount" });
        await Assertions.Expect(submit).ToBeEnabledAsync();
        return new E2EExplicitDialog(dialog, invoiceId, bankTransactionId);
    }

    private static Guid ReadTrailingGuid(string label)
    {
        Match match = Regex.Match(label, @"([0-9a-fA-F-]{36})\s*$");
        Assert.True(match.Success, "The record button did not end with an id.");
        return Guid.Parse(match.Groups[1].Value);
    }

    private static async Task ImportFileAsync(IPage page, string importKind, string file, int pairCount)
    {
        await page.GetByRole(AriaRole.Link, new() { Name = "Imports", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Radio, new() { Name = importKind }).CheckAsync();
        await page.GetByRole(AriaRole.Textbox, new() { Name = "File source" }).FillAsync("E2E");
        await page.Locator("#import-file").SetInputFilesAsync(file);
        await page.GetByRole(AriaRole.Button, new() { Name = "Import file" }).ClickAsync();
        await Assertions.Expect(page.GetByText("Import complete")).ToBeVisibleAsync();
        await Assertions.Expect(Count(page, "Added")).ToHaveTextAsync(pairCount.ToString());
        await Assertions.Expect(Count(page, "Needs attention")).ToHaveTextAsync("0");
        await Assertions.Expect(Count(page, "Rows in file")).ToHaveTextAsync(pairCount.ToString());
        await ExpandTechnicalDetailsAsync(page);
        await Assertions.Expect(page.GetByText("Status: Completed", new() { Exact = true })).ToBeVisibleAsync();
    }

    private static ILocator Count(IPage page, string term)
    {
        return page.Locator("dt").Filter(new() { HasText = term }).Locator("xpath=following-sibling::dd[1]");
    }

    private static async Task ExpandTechnicalDetailsAsync(IPage page)
    {
        ILocator summary = page.GetByRole(AriaRole.Button, new() { Name = "Technical details" });
        if (await summary.GetAttributeAsync("aria-expanded") != "true")
        {
            await summary.ClickAsync();
        }
    }
}