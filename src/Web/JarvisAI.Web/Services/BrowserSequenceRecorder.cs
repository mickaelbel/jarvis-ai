using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface IBrowserSequenceRecorder
{
    bool IsRecording { get; }
    void StartRecording(string name);
    void StopRecording();
    void RecordAction(BrowserAction action);
    Task<List<BrowserSequence>> GetSequencesAsync(CancellationToken ct = default);
    Task<bool> ReplaySequenceAsync(string sequenceId, IBrowserTabManager tabManager, CancellationToken ct = default);
    Task DeleteSequenceAsync(string sequenceId, CancellationToken ct = default);
}

public sealed class BrowserSequenceRecorder : IBrowserSequenceRecorder
{
    private readonly ILogger<BrowserSequenceRecorder> _logger;
    private readonly string _storagePath;
    private List<BrowserSequence> _sequences = new();
    private BrowserSequence? _current;

    public bool IsRecording => _current is not null;

    public BrowserSequenceRecorder(ILogger<BrowserSequenceRecorder> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "browser_sequences.json");
        LoadSequences();
    }

    public void StartRecording(string name)
    {
        _current = new BrowserSequence
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            CreatedAt = DateTime.UtcNow,
            Actions = new List<BrowserAction>()
        };
        _logger.LogInformation("[BrowserSeq] Recording started: {Name}", name);
    }

    public void StopRecording()
    {
        if (_current is null) return;
        _sequences.Add(_current);
        SaveSequences();
        _logger.LogInformation("[BrowserSeq] Recording stopped: {Name} ({Count} actions)",
            _current.Name, _current.Actions.Count);
        _current = null;
    }

    public void RecordAction(BrowserAction action)
    {
        if (_current is null) return;
        action.Timestamp = DateTime.UtcNow;
        _current.Actions.Add(action);
    }

    public async Task<List<BrowserSequence>> GetSequencesAsync(CancellationToken ct = default)
    {
        return await Task.FromResult(_sequences.ToList());
    }

    public async Task<bool> ReplaySequenceAsync(string sequenceId, IBrowserTabManager tabManager, CancellationToken ct = default)
    {
        var seq = _sequences.FirstOrDefault(s => s.Id == sequenceId);
        if (seq is null) return false;

        _logger.LogInformation("[BrowserSeq] Replaying {Name} ({Count} actions)",
            seq.Name, seq.Actions.Count);

        foreach (var action in seq.Actions)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                await ReplayActionAsync(action, tabManager, ct);
                await Task.Delay(action.DelayMs > 0 ? action.DelayMs : 200, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[BrowserSeq] Action failed: {Type}", action.Type);
            }
        }

        return true;
    }

    public async Task DeleteSequenceAsync(string sequenceId, CancellationToken ct = default)
    {
        _sequences.RemoveAll(s => s.Id == sequenceId);
        SaveSequences();
        await Task.CompletedTask;
    }

    private async Task ReplayActionAsync(BrowserAction action, IBrowserTabManager tabManager, CancellationToken ct)
    {
        switch (action.Type)
        {
            case "navigate":
                _logger.LogDebug("[BrowserSeq] Navigate to {Url}", action.Value);
                break;
            case "click":
                _logger.LogDebug("[BrowserSeq] Click at ({X}, {Y})", action.X, action.Y);
                break;
            case "type":
                _logger.LogDebug("[BrowserSeq] Type: {Text}", action.Value);
                break;
            case "scroll":
                _logger.LogDebug("[BrowserSeq] Scroll: {Delta}", action.Value);
                break;
            case "wait":
                var waitMs = int.TryParse(action.Value, out var ms) ? ms : 1000;
                await Task.Delay(waitMs, ct);
                break;
        }
    }

    private void LoadSequences()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                _sequences = JsonSerializer.Deserialize<List<BrowserSequence>>(json) ?? new();
            }
        }
        catch
        {
            _sequences = new();
        }
    }

    private void SaveSequences()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_sequences, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BrowserSeq] Failed to save sequences");
        }
    }
}

public sealed class BrowserSequence
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public List<BrowserAction> Actions { get; set; } = new();
}

public sealed class BrowserAction
{
    public string Type { get; set; } = "";
    public string? Value { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }
    public int DelayMs { get; set; }
    public DateTime Timestamp { get; set; }
}
