using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface IMeetingTranscriptionService
{
    Task<TranscriptionResult> TranscribeAudioAsync(string audioFilePath, CancellationToken ct = default);
    IReadOnlyList<Transcription> GetTranscriptions();
    Transcription? GetTranscription(string transcriptionId);
    void DeleteTranscription(string transcriptionId);
    string GenerateSummary(string transcriptionId);
    IReadOnlyList<ActionItem> ExtractActionItems(string transcriptionId);
}

public sealed class MeetingTranscriptionService : IMeetingTranscriptionService
{
    private readonly ILogger<MeetingTranscriptionService> _logger;
    private readonly string _storagePath;
    private readonly List<Transcription> _transcriptions = new();

    public MeetingTranscriptionService(ILogger<MeetingTranscriptionService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "transcriptions.json");
        Load();
    }

    public async Task<TranscriptionResult> TranscribeAudioAsync(string audioFilePath, CancellationToken ct = default)
    {
        if (!File.Exists(audioFilePath))
            return new TranscriptionResult { Success = false, Error = "Fichier audio non trouvé" };

        // Simulate transcription (in real app, use Whisper API or similar)
        var transcription = new Transcription
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            SourceFile = audioFilePath,
            Content = "Transcription simulée: Discussion sur le projet, next steps identifiés.",
            Language = "fr",
            Duration = TimeSpan.FromMinutes(30),
            CreatedAt = DateTime.UtcNow,
            Speakers = new() { "Speaker 1", "Speaker 2" }
        };

        _transcriptions.Add(transcription);
        Save();

        _logger.LogInformation("[Meeting] Transcribed: {File} ({Duration})", Path.GetFileName(audioFilePath), transcription.Duration);

        return new TranscriptionResult
        {
            Success = true,
            TranscriptionId = transcription.Id,
            Preview = transcription.Content[..Math.Min(200, transcription.Content.Length)]
        };
    }

    public IReadOnlyList<Transcription> GetTranscriptions()
        => _transcriptions.OrderByDescending(t => t.CreatedAt).ToList();

    public Transcription? GetTranscription(string transcriptionId)
        => _transcriptions.FirstOrDefault(t => t.Id == transcriptionId);

    public void DeleteTranscription(string transcriptionId)
    {
        _transcriptions.RemoveAll(t => t.Id == transcriptionId);
        Save();
    }

    public string GenerateSummary(string transcriptionId)
    {
        var transcription = GetTranscription(transcriptionId);
        if (transcription is null) return "Transcription non trouvée";

        return $"## Résumé de la réunion\n\n" +
               $"**Date:** {transcription.CreatedAt:dd/MM/yyyy HH:mm}\n" +
               $"**Durée:** {transcription.Duration.TotalMinutes:F0} minutes\n" +
               $"**Participants:** {string.Join(", ", transcription.Speakers)}\n\n" +
               $"**Points clés:**\n" +
               $"- Discussion sur l'avancement du projet\n" +
               $"- Décisions prises sur les prochaines étapes\n" +
               $"- Suivi des actions en cours";
    }

    public IReadOnlyList<ActionItem> ExtractActionItems(string transcriptionId)
    {
        return new List<ActionItem>
        {
            new() { Id = "1", Description = "Terminer la documentation API", Assignee = "Speaker 1", DueDate = DateTime.UtcNow.AddDays(3), Status = ActionItemStatus.Pending },
            new() { Id = "2", Description = "Revoir les tests unitaires", Assignee = "Speaker 2", DueDate = DateTime.UtcNow.AddDays(5), Status = ActionItemStatus.Pending }
        };
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var loaded = JsonSerializer.Deserialize<List<Transcription>>(json);
                if (loaded is not null) _transcriptions.AddRange(loaded);
            }
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_transcriptions, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class TranscriptionResult
{
    public bool Success { get; set; }
    public string? TranscriptionId { get; set; }
    public string? Preview { get; set; }
    public string? Error { get; set; }
}

public sealed class Transcription
{
    public string Id { get; set; } = "";
    public string SourceFile { get; set; } = "";
    public string Content { get; set; } = "";
    public string Language { get; set; } = "fr";
    public TimeSpan Duration { get; set; }
    public List<string> Speakers { get; set; } = new();
    public DateTime CreatedAt { get; set; }
}

public sealed class ActionItem
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
    public string Assignee { get; set; } = "";
    public DateTime DueDate { get; set; }
    public ActionItemStatus Status { get; set; }
}

public enum ActionItemStatus { Pending, InProgress, Completed }
