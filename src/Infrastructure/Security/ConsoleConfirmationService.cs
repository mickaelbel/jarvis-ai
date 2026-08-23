using JarvisAI.Application.Security;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Security;

public sealed class ConsoleConfirmationService : IUserConfirmationService
{
    private readonly ILogger<ConsoleConfirmationService> _logger;
    private readonly TimeSpan _timeout;

    public ConsoleConfirmationService(ILogger<ConsoleConfirmationService> logger, SecurityOptions? options = null)
    {
        _logger = logger;
        var seconds = options?.DangerousToolTimeoutSeconds ?? 0;
        _timeout = seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromSeconds(30);
    }

    public async Task<ConfirmationResult> RequestConfirmationAsync(ConfirmationRequest request, CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        _logger.LogInformation("[ConsoleConfirmation] Confirmation requested for tool: {ToolName} (Risk: {RiskLevel}, Timeout: {TimeoutSeconds}s)",
            request.ToolName, request.RiskLevel, _timeout.TotalSeconds);

        Console.WriteLine();
        Console.WriteLine("=== SECURITY CONFIRMATION ===");
        Console.WriteLine($"Tool: {request.ToolName}");
        Console.WriteLine($"Risk Level: {request.RiskLevel}");
        Console.WriteLine($"Description: {request.Description}");
        if (request.Parameters.Count > 0)
        {
            Console.WriteLine("Parameters:");
            foreach (var param in request.Parameters)
                Console.WriteLine($"  {param.Key}: {param.Value}");
        }
        Console.Write("Confirm? (yes/no): ");

        try
        {
            var input = await ReadLineWithTimeoutAsync(cancellationToken);
            sw.Stop();

            var confirmed = input is "yes" or "y" or "oui" or "o";

            _logger.LogInformation("[ConsoleConfirmation] User response: {Input} (Confirmed={Confirmed}, Time={Elapsed}ms)",
                input, confirmed, sw.ElapsedMilliseconds);

            return confirmed
                ? ConfirmationResult.Accepted(ConfirmationMethod.Text, sw.Elapsed, input)
                : ConfirmationResult.Denied(ConfirmationMethod.Text, sw.Elapsed, input);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            _logger.LogInformation("[ConsoleConfirmation] Timed out or cancelled after {Elapsed}ms", sw.ElapsedMilliseconds);
            return ConfirmationResult.Denied(ConfirmationMethod.Text, sw.Elapsed, "timed out or cancelled");
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "[ConsoleConfirmation] Failed to read user input");
            return ConfirmationResult.Denied(ConfirmationMethod.Text, sw.Elapsed, "error reading input");
        }
    }

    private async Task<string?> ReadLineWithTimeoutAsync(CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);

        var readTask = Task.Run(() => Console.ReadLine()?.Trim().ToLowerInvariant(), CancellationToken.None);
        var completed = await Task.WhenAny(readTask, Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token));
        if (completed != readTask)
        {
            timeoutCts.Cancel();
            return null;
        }

        return await readTask;
    }
}
