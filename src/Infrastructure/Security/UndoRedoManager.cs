using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Security;

public interface IUndoRedoManager
{
    void RecordAction(UndoableAction action);
    bool CanUndo { get; }
    bool CanRedo { get; }
    UndoableAction? Undo();
    UndoableAction? Redo();
    IReadOnlyList<UndoableAction> GetHistory(int maxCount = 50);
    void Clear();
}

public sealed class UndoRedoManager : IUndoRedoManager
{
    private readonly ILogger<UndoRedoManager> _logger;
    private readonly List<UndoableAction> _undoStack = new();
    private readonly List<UndoableAction> _redoStack = new();
    private const int MaxStack = 100;

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public UndoRedoManager(ILogger<UndoRedoManager> logger)
    {
        _logger = logger;
    }

    public void RecordAction(UndoableAction action)
    {
        action.Timestamp = DateTime.UtcNow;
        lock (_undoStack)
        {
            _undoStack.Add(action);
            if (_undoStack.Count > MaxStack)
                _undoStack.RemoveAt(0);
            _redoStack.Clear();
        }

        _logger.LogDebug("[UndoRedo] Recorded: {Type} - {Description}", action.Type, action.Description);
    }

    public UndoableAction? Undo()
    {
        lock (_undoStack)
        {
            if (_undoStack.Count == 0) return null;

            var action = _undoStack[^1];
            _undoStack.RemoveAt(_undoStack.Count - 1);
            _redoStack.Add(action);

            _logger.LogInformation("[UndoRedo] Undo: {Description}", action.Description);
            return action;
        }
    }

    public UndoableAction? Redo()
    {
        lock (_redoStack)
        {
            if (_redoStack.Count == 0) return null;

            var action = _redoStack[^1];
            _redoStack.RemoveAt(_redoStack.Count - 1);
            _undoStack.Add(action);

            _logger.LogInformation("[UndoRedo] Redo: {Description}", action.Description);
            return action;
        }
    }

    public IReadOnlyList<UndoableAction> GetHistory(int maxCount = 50)
    {
        lock (_undoStack)
        {
            return _undoStack.TakeLast(maxCount).Reverse().ToList();
        }
    }

    public void Clear()
    {
        lock (_undoStack)
        {
            _undoStack.Clear();
            _redoStack.Clear();
        }
    }
}

public sealed class UndoableAction
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Type { get; set; } = "";
    public string Description { get; set; } = "";
    public string? FilePath { get; set; }
    public string? OldContent { get; set; }
    public string? NewContent { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public DateTime Timestamp { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();

    public async Task<bool> RevertAsync(CancellationToken ct = default)
    {
        try
        {
            if (!string.IsNullOrEmpty(FilePath) && OldContent is not null)
            {
                await File.WriteAllTextAsync(FilePath, OldContent, ct);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> ReapplyAsync(CancellationToken ct = default)
    {
        try
        {
            if (!string.IsNullOrEmpty(FilePath) && NewContent is not null)
            {
                await File.WriteAllTextAsync(FilePath, NewContent, ct);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }
}
