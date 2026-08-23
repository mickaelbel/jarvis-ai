using JarvisAI.Application.ComputerUse;
using JarvisAI.Application.Security;
using JarvisAI.Application.Tools;
using JarvisAI.Application.Vision;
using JarvisAI.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace JarvisAI.LiveValidation;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!args.Contains("--now"))
        {
            Console.WriteLine("JarvisAI Live Validation: this harness will move the real mouse, type text,");
            Console.WriteLine("open/close real applications (notepad, calculator, explorer) and write temp files.");
            Console.WriteLine("Please leave this machine idle. Starting in 5 seconds (pass --now to skip)...");
            await Task.Delay(5000);
        }

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddInfrastructure();
        services.RemoveAll<IUserConfirmationService>();
        services.AddSingleton<IUserConfirmationService, AutoAcceptConfirmationService>();

        await using var provider = services.BuildServiceProvider();

        var harness = new Harness(
            provider.GetRequiredService<IComputerController>(),
            provider.GetRequiredService<IOcrService>(),
            provider.GetRequiredService<IComputerUseService>(),
            provider.GetRequiredService<IVisionService>(),
            provider.GetRequiredService<IToolExecutor>());

        var results = await harness.RunAllAsync();

        Console.WriteLine();
        Console.WriteLine("=== RESULTS ===");
        foreach (var result in results)
            Console.WriteLine("  " + result);

        var passed = results.Count(r => r.Outcome == Outcome.Pass);
        var failed = results.Count(r => r.Outcome == Outcome.Fail);
        var skipped = results.Count(r => r.Outcome == Outcome.Skip);

        Console.WriteLine();
        Console.WriteLine($"Passed: {passed}, Failed: {failed}, Skipped: {skipped} (total {results.Count})");
        return failed == 0 ? 0 : 1;
    }
}
