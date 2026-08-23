using JarvisAI.Core;
using JarvisAI.Core.Engine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureJarvis()
    .Build();

var engine = host.Services.GetRequiredService<CoreEngine>();
var logger = host.Services.GetRequiredService<ILogger<Program>>();

using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    await engine.StartAsync(cts.Token);
    await host.WaitForShutdownAsync(cts.Token);
}
catch (OperationCanceledException)
{
    logger.LogInformation("Shutdown requested");
}
finally
{
    await engine.StopAsync();
    logger.LogInformation("Jarvis AI terminated.");
}
