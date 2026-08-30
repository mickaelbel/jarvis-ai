using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface INoteService
{
    Task<IReadOnlyList<Note>> SearchAsync(string? query = null, string? tag = null, int maxCount = 50, CancellationToken ct = default);
    Task<Note?> GetNoteAsync(string noteId, CancellationToken ct = default);
    Task<string> CreateNoteAsync(string title, string content, IReadOnlyList<string>? tags = null, CancellationToken ct = default);
    Task UpdateNoteAsync(string noteId, string title, string content, IReadOnlyList<string>? tags = null, CancellationToken ct = default);
    Task DeleteNoteAsync(string noteId, CancellationToken ct = default);
    Task PinNoteAsync(string noteId, CancellationToken ct = default);
    IReadOnlyList<string> GetAllTags();
}

public sealed class NoteService : INoteService
{
    private readonly ILogger<NoteService> _logger;
    private readonly string _storagePath;
    private readonly List<Note> _notes = new();

    public NoteService(ILogger<NoteService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "notes.json");
        Load();
    }

    public async Task<IReadOnlyList<Note>> SearchAsync(string? query = null, string? tag = null, int maxCount = 50, CancellationToken ct = default)
    {
        lock (_notes)
        {
            IEnumerable<Note> results = _notes;

            if (!string.IsNullOrWhiteSpace(query))
            {
                var lower = query.ToLowerInvariant();
                results = results.Where(n =>
                    n.Title.Contains(lower, StringComparison.OrdinalIgnoreCase) ||
                    n.Content.Contains(lower, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(tag))
            {
                results = results.Where(n => n.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
            }

            return results
                .OrderByDescending(n => n.IsPinned)
                .ThenByDescending(n => n.UpdatedAt)
                .Take(maxCount)
                .ToList();
        }
    }

    public async Task<Note?> GetNoteAsync(string noteId, CancellationToken ct = default)
    {
        lock (_notes)
        {
            return _notes.FirstOrDefault(n => n.Id == noteId);
        }
    }

    public async Task<string> CreateNoteAsync(string title, string content, IReadOnlyList<string>? tags = null, CancellationToken ct = default)
    {
        var note = new Note
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Title = title,
            Content = content,
            Tags = tags?.ToList() ?? new List<string>(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        lock (_notes)
        {
            _notes.Add(note);
        }

        Save();
        _logger.LogInformation("[Notes] Created: {Title}", title);
        return note.Id;
    }

    public async Task UpdateNoteAsync(string noteId, string title, string content, IReadOnlyList<string>? tags = null, CancellationToken ct = default)
    {
        lock (_notes)
        {
            var note = _notes.FirstOrDefault(n => n.Id == noteId);
            if (note is not null)
            {
                note.Title = title;
                note.Content = content;
                note.Tags = tags?.ToList() ?? note.Tags;
                note.UpdatedAt = DateTime.UtcNow;
            }
        }
        Save();
        await Task.CompletedTask;
    }

    public async Task DeleteNoteAsync(string noteId, CancellationToken ct = default)
    {
        lock (_notes)
        {
            _notes.RemoveAll(n => n.Id == noteId);
        }
        Save();
        await Task.CompletedTask;
    }

    public async Task PinNoteAsync(string noteId, CancellationToken ct = default)
    {
        lock (_notes)
        {
            var note = _notes.FirstOrDefault(n => n.Id == noteId);
            if (note is not null)
            {
                note.IsPinned = !note.IsPinned;
            }
        }
        Save();
        await Task.CompletedTask;
    }

    public IReadOnlyList<string> GetAllTags()
    {
        lock (_notes)
        {
            return _notes.SelectMany(n => n.Tags).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t).ToList();
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var loaded = JsonSerializer.Deserialize<List<Note>>(json);
                if (loaded is not null) _notes.AddRange(loaded);
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

            lock (_notes)
            {
                var json = JsonSerializer.Serialize(_notes, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_storagePath, json);
            }
        }
        catch { }
    }
}

public sealed class Note
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public bool IsPinned { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int WordCount => Content.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
}
