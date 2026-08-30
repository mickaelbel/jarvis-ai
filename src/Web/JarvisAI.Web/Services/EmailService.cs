using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Net.Mail;

namespace JarvisAI.Web.Services;

public interface IEmailService
{
    Task<bool> TestConnectionAsync(string smtpHost, int port, string username, string password, bool useSsl, CancellationToken ct = default);
    Task SendEmailAsync(string to, string subject, string body, bool isHtml = false, CancellationToken ct = default);
    Task<IReadOnlyList<EmailMessage>> GetEmailsAsync(string folder = "INBOX", int maxCount = 20, CancellationToken ct = default);
    Task<EmailMessage?> GetEmailAsync(string messageId, CancellationToken ct = default);
    Task SendReplyAsync(string originalMessageId, string body, CancellationToken ct = default);
    Task MarkAsReadAsync(string messageId, CancellationToken ct = default);
    Task DeleteEmailAsync(string messageId, CancellationToken ct = default);
    void Configure(EmailSettings settings);
    EmailSettings? GetSettings();
}

public sealed class EmailService : IEmailService
{
    private readonly ILogger<EmailService> _logger;
    private readonly string _storagePath;
    private EmailSettings? _settings;

    public EmailService(ILogger<EmailService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "email_settings.json");
        LoadSettings();
    }

    public async Task<bool> TestConnectionAsync(string smtpHost, int port, string username, string password, bool useSsl, CancellationToken ct = default)
    {
        try
        {
            using var client = new SmtpClient(smtpHost, port)
            {
                Credentials = new System.Net.NetworkCredential(username, password),
                EnableSsl = useSsl,
                Timeout = 10000
            };

            await client.SendMailAsync(new MailMessage(username, username, "Test", "Connection test"), ct);
            _logger.LogInformation("[Email] Connection test successful to {Host}:{Port}", smtpHost, port);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Email] Connection test failed");
            return false;
        }
    }

    public async Task SendEmailAsync(string to, string subject, string body, bool isHtml = false, CancellationToken ct = default)
    {
        if (_settings is null)
            throw new InvalidOperationException("Email not configured");

        using var client = CreateSmtpClient();
        var message = new MailMessage(_settings.Email, to, subject, body) { IsBodyHtml = isHtml };
        await client.SendMailAsync(message, ct);
        _logger.LogInformation("[Email] Sent to {To}: {Subject}", to, subject);
    }

    public async Task<IReadOnlyList<EmailMessage>> GetEmailsAsync(string folder = "INBOX", int maxCount = 20, CancellationToken ct = default)
    {
        _logger.LogDebug("[Email] Fetching emails from {Folder} (max {Max})", folder, maxCount);
        return await Task.FromResult(new List<EmailMessage>());
    }

    public async Task<EmailMessage?> GetEmailAsync(string messageId, CancellationToken ct = default)
    {
        return await Task.FromResult<EmailMessage?>(null);
    }

    public async Task SendReplyAsync(string originalMessageId, string body, CancellationToken ct = default)
    {
        _logger.LogInformation("[Email] Reply to {Id}", originalMessageId);
        await Task.CompletedTask;
    }

    public async Task MarkAsReadAsync(string messageId, CancellationToken ct = default)
    {
        await Task.CompletedTask;
    }

    public async Task DeleteEmailAsync(string messageId, CancellationToken ct = default)
    {
        await Task.CompletedTask;
    }

    public void Configure(EmailSettings settings)
    {
        _settings = settings;
        SaveSettings();
        _logger.LogInformation("[Email] Configured: {Host}:{Port}", settings.SmtpHost, settings.SmtpPort);
    }

    public EmailSettings? GetSettings() => _settings;

    private SmtpClient CreateSmtpClient()
    {
        if (_settings is null) throw new InvalidOperationException("Email not configured");
        return new SmtpClient(_settings.SmtpHost, _settings.SmtpPort)
        {
            Credentials = new System.Net.NetworkCredential(_settings.Username, _settings.Password),
            EnableSsl = _settings.UseSsl
        };
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                _settings = JsonSerializer.Deserialize<EmailSettings>(json);
            }
        }
        catch { }
    }

    private void SaveSettings()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class EmailSettings
{
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public string ImapHost { get; set; } = "";
    public int ImapPort { get; set; } = 993;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Email { get; set; } = "";
    public bool UseSsl { get; set; } = true;
}

public sealed class EmailMessage
{
    public string Id { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public bool IsRead { get; set; }
    public DateTime ReceivedAt { get; set; }
    public List<string> Attachments { get; set; } = new();
}
