namespace JarvisAI.Web.Services;

public interface IAppLifecycleService
{
    void Reload();
    void Restart();
    void Stop();
}

public sealed class NoopAppLifecycle : IAppLifecycleService
{
    public void Reload() { }
    public void Restart() { }
    public void Stop() { }
}
