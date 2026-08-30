using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace JarvisAI.Web.Services;

public interface IHotkeyManager
{
    IReadOnlyList<HotkeyBinding> GetBindings();
    string AddBinding(string name, string keys, string action, string? parameters = null);
    void RemoveBinding(string bindingId);
    void EnableBinding(string bindingId);
    void DisableBinding(string bindingId);
    bool ProcessHotkey(string keys);
    event EventHandler<HotkeyTriggeredEventArgs>? HotkeyTriggered;
}

public sealed class HotkeyManager : IHotkeyManager
{
    private readonly ILogger<HotkeyManager> _logger;
    private readonly string _storagePath;
    private readonly List<HotkeyBinding> _bindings = new();

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public event EventHandler<HotkeyTriggeredEventArgs>? HotkeyTriggered;

    public HotkeyManager(ILogger<HotkeyManager> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "hotkeys.json");
        Load();
    }

    public IReadOnlyList<HotkeyBinding> GetBindings() => _bindings.ToList();

    public string AddBinding(string name, string keys, string action, string? parameters = null)
    {
        var binding = new HotkeyBinding
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            Keys = keys,
            Action = action,
            Parameters = parameters ?? "",
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow
        };

        _bindings.Add(binding);
        Save();
        _logger.LogInformation("[Hotkey] Added: {Name} ({Keys}) → {Action}", name, keys, action);
        return binding.Id;
    }

    public void RemoveBinding(string bindingId)
    {
        _bindings.RemoveAll(b => b.Id == bindingId);
        Save();
    }

    public void EnableBinding(string bindingId)
    {
        var binding = _bindings.FirstOrDefault(b => b.Id == bindingId);
        if (binding is not null) { binding.IsEnabled = true; Save(); }
    }

    public void DisableBinding(string bindingId)
    {
        var binding = _bindings.FirstOrDefault(b => b.Id == bindingId);
        if (binding is not null) { binding.IsEnabled = false; Save(); }
    }

    public bool ProcessHotkey(string keys)
    {
        var binding = _bindings.FirstOrDefault(b =>
            b.IsEnabled && b.Keys.Equals(keys, StringComparison.OrdinalIgnoreCase));

        if (binding is null) return false;

        _logger.LogInformation("[Hotkey] Triggered: {Name} ({Keys}) → {Action}",
            binding.Name, binding.Keys, binding.Action);

        HotkeyTriggered?.Invoke(this, new HotkeyTriggeredEventArgs
        {
            Binding = binding,
            Timestamp = DateTime.UtcNow
        });

        binding.LastTriggered = DateTime.UtcNow;
        binding.TriggerCount++;
        Save();

        return true;
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var loaded = JsonSerializer.Deserialize<List<HotkeyBinding>>(json);
                if (loaded is not null) _bindings.AddRange(loaded);
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

            var json = JsonSerializer.Serialize(_bindings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class HotkeyBinding
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Keys { get; set; } = "";
    public string Action { get; set; } = "";
    public string Parameters { get; set; } = "";
    public bool IsEnabled { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastTriggered { get; set; }
    public int TriggerCount { get; set; }
}

public sealed class HotkeyTriggeredEventArgs : EventArgs
{
    public HotkeyBinding Binding { get; set; } = new();
    public DateTime Timestamp { get; set; }
}
