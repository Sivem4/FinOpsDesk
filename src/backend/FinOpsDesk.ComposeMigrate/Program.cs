using FinOpsDesk.ComposeMigrate;

using Microsoft.Extensions.Configuration;

IConfiguration configuration = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .Build();

try
{
    return await ComposeMigrator.RunAsync(configuration, Console.Error, CancellationToken.None);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Migration cancelled.");
    return 1;
}