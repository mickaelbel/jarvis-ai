namespace JarvisAI.Application.AutoImprovement;

/// <summary>
/// Surface de « capabilities » exposée au code C# auto-créé. Chaque appel est
/// borné dans le temps et restreint à un périmètre sûr (workspace, HTTP public).
/// </summary>
public interface IToolHost
{
    string NowUtc();
    string ReadTextFile(string path);
    string WriteTextFile(string path, string content);
    string HttpGet(string url);
    string Log(string message);
}

/// <summary>Fabrique d'un host par exécution (chaque exécution a sa propre deadline).</summary>
public interface IToolHostFactory
{
    IToolHost Create();
}
