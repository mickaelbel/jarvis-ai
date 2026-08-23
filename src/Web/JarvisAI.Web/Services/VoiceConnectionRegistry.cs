namespace JarvisAI.Web.Services;

public sealed class VoiceConnectionRegistry
{
    private readonly object _lock = new();
    private readonly HashSet<string> _ids = new();

    public IReadOnlyList<string> All
    {
        get
        {
            lock (_lock) return _ids.ToList();
        }
    }

    public void Add(string connectionId)
    {
        lock (_lock) _ids.Add(connectionId);
    }

    public void Remove(string connectionId)
    {
        lock (_lock) _ids.Remove(connectionId);
    }
}
