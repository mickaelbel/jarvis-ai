using System.Text.Json;

namespace JarvisAI.Web.Services;

public sealed record DebugModeState(bool Enabled, bool Unlocked);

public sealed class DebugModeStore
{
    public const string DebugPassword = "C@line09!";

    private readonly object _lock = new();
    private readonly string _filePath;
    private bool _enabled;
    private bool _unlocked;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public DebugModeStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI",
            "debug-mode.json");
        Load();
    }

    public event EventHandler? Changed;

    public bool Enabled
    {
        get { lock (_lock) return _enabled; }
        set
        {
            lock (_lock)
            {
                if (_enabled == value) return;
                _enabled = value;
                Save();
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool Unlocked
    {
        get { lock (_lock) return _unlocked; }
        set
        {
            lock (_lock)
            {
                if (_unlocked == value) return;
                _unlocked = value;
                Save();
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public DebugModeState GetState()
    {
        lock (_lock) return new DebugModeState(_enabled, _unlocked);
    }

    public bool TryUnlock(string? password)
    {
        if (string.IsNullOrEmpty(password) || password != DebugPassword)
            return false;
        Unlocked = true;
        return true;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            var state = JsonSerializer.Deserialize<DebugModeState>(json, JsonOptions);
            if (state is null) return;
            _enabled = state.Enabled;
            _unlocked = state.Unlocked;
        }
        catch
        {
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(GetState(), JsonOptions));
        }
        catch
        {
        }
    }
}
