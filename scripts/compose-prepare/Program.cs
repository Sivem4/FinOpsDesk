if (args.Length == 3 && string.Equals(args[0], "check", StringComparison.Ordinal))
{
    return FinOpsDesk.ComposePrepare.ComposeSecretLocation.Check(args[1], args[2], Console.Error);
}

if (args.Length != 2
    || string.IsNullOrWhiteSpace(args[0])
    || string.IsNullOrWhiteSpace(args[1]))
{
    Console.Error.WriteLine("Compose secret directory is required.");
    return 2;
}

int location = FinOpsDesk.ComposePrepare.ComposeSecretLocation.Check(args[0], args[1], Console.Error);
if (location != 0)
{
    return location;
}

return await FinOpsDesk.ComposePrepare.ComposeSecretSet.ExecuteAsync(
    args[0],
    Console.Out,
    Console.Error);