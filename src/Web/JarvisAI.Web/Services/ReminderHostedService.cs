using JarvisAI.Application.Reminders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Web.Services;

/// <summary>
/// Poll les rappels arrivés à échéance et les annonce vocalement (via
/// ProactiveAnnouncer) sans interrompre une conversation en cours.
/// </summary>
public sealed class ReminderHostedService : BackgroundService
{
    private readonly IReminderService _reminders;
    private readonly ProactiveAnnouncer _announcer;
    private readonly ILogger<ReminderHostedService> _logger;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    public ReminderHostedService(
        IReminderService reminders,
        ProactiveAnnouncer announcer,
        ILogger<ReminderHostedService> logger)
    {
        _reminders = reminders;
        _announcer = announcer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var due = _reminders.GetDue(DateTime.Now);
                foreach (var reminder in due)
                {
                    _announcer.Enqueue($"Rappel : {reminder.Text}");
                    _reminders.MarkNotified(reminder.Id);
                    _logger.LogInformation("[Reminders] Announced reminder {Id}: {Text}", reminder.Id, reminder.Text);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Reminders] Poll failed");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
