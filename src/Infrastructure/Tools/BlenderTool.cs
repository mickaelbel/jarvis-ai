using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Tools;

public sealed class BlenderTool : ITool
{
    private readonly ILogger<BlenderTool> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _apiUrl = "http://127.0.0.1:7777";

    public string Name => "blender";
    public string Description =>
        "Contrôle Blender via l'addon JarvisAI. Actions : scene (infos scène), objects (liste objets), " +
        "exec (exécute du code Python), add_object (ajoute un objet), delete_object (supprime), " +
        "modify (modifie position/rotation/scale), render (rendu caméra), save (enregistre). " +
        "Nécessite l'addon JarvisAI installé dans Blender.";
    public string Category => "bureau";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Medium;
    public string? WaitingPhrase => "J'agis sur Blender...";

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "Action : scene, objects, exec, add_object, delete_object, modify, render, save, new_scene", typeof(string), required: true),
        new ToolParameter("code", "Code Python à exécuter (pour action=exec)", typeof(string), required: false),
        new ToolParameter("name", "Nom de l'objet (pour delete_object, modify)", typeof(string), required: false),
        new ToolParameter("type", "Type d'objet : CUBE, SPHERE, CYLINDER, PLANE, LIGHT, CAMERA (pour add_object)", typeof(string), required: false),
        new ToolParameter("location", "Position [x,y,z] (pour add_object, modify)", typeof(string), required: false),
        new ToolParameter("rotation", "Rotation [x,y,z] (pour modify)", typeof(string), required: false),
        new ToolParameter("scale", "Échelle [x,y,z] (pour modify)", typeof(string), required: false),
        new ToolParameter("path", "Chemin d'enregistrement (pour save, render)", typeof(string), required: false),
        new ToolParameter("camera", "Nom de la caméra (pour render)", typeof(string), required: false),
    };

    public BlenderTool(ILogger<BlenderTool> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("action", out var action);

        if (string.IsNullOrWhiteSpace(action))
            return ToolResult.Failed("Paramètre 'action' requis.");

        // Vérifier que le serveur est accessible
        if (!await IsServerRunningAsync())
        {
            return ToolResult.Failed(
                "Le serveur JarvisAI n'est pas actif dans Blender. " +
                "Ouvre Blender → Sidebar (N) → JarvisAI → Démarrer Serveur. " +
                "Ou installe l'addon via Plugins → Installer.");
        }

        return action.ToLowerInvariant() switch
        {
            "scene" => await GetSceneInfoAsync(),
            "objects" => await GetObjectsAsync(),
            "exec" => await ExecCodeAsync(parameters),
            "add_object" => await AddObjectAsync(parameters),
            "delete_object" => await DeleteObjectAsync(parameters),
            "modify" => await ModifyObjectAsync(parameters),
            "render" => await RenderAsync(parameters),
            "save" => await SaveAsync(parameters),
            "new_scene" => await NewSceneAsync(parameters),
            _ => ToolResult.Failed($"Action inconnue : {action}")
        };
    }

    private async Task<bool> IsServerRunningAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_apiUrl}/health");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<ToolResult> GetSceneInfoAsync()
    {
        var result = await GetAsync("/scene");
        return result;
    }

    private async Task<ToolResult> GetObjectsAsync()
    {
        var result = await GetAsync("/objects");
        return result;
    }

    private async Task<ToolResult> ExecCodeAsync(IReadOnlyDictionary<string, string> parameters)
    {
        parameters.TryGetValue("code", out var code);
        if (string.IsNullOrWhiteSpace(code))
            return ToolResult.Failed("Paramètre 'code' requis pour action=exec");

        var body = JsonSerializer.Serialize(new { code });
        var result = await PostAsync("/exec", body);
        return result;
    }

    private async Task<ToolResult> AddObjectAsync(IReadOnlyDictionary<string, string> parameters)
    {
        parameters.TryGetValue("type", out var type);
        parameters.TryGetValue("name", out var name);
        parameters.TryGetValue("location", out var location);

        var data = new Dictionary<string, object>
        {
            ["type"] = type ?? "CUBE",
            ["name"] = name ?? "JarvisAI_Object"
        };

        if (!string.IsNullOrEmpty(location))
            data["location"] = ParseVector3(location);

        var body = JsonSerializer.Serialize(data);
        return await PostAsync("/add_object", body);
    }

    private async Task<ToolResult> DeleteObjectAsync(IReadOnlyDictionary<string, string> parameters)
    {
        parameters.TryGetValue("name", out var name);
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult.Failed("Paramètre 'name' requis pour delete_object");

        var body = JsonSerializer.Serialize(new { name });
        return await PostAsync("/delete_object", body);
    }

    private async Task<ToolResult> ModifyObjectAsync(IReadOnlyDictionary<string, string> parameters)
    {
        parameters.TryGetValue("name", out var name);
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult.Failed("Paramètre 'name' requis pour modify");

        var data = new Dictionary<string, object> { ["name"] = name };

        parameters.TryGetValue("location", out var location);
        if (!string.IsNullOrEmpty(location))
            data["location"] = ParseVector3(location);

        parameters.TryGetValue("rotation", out var rotation);
        if (!string.IsNullOrEmpty(rotation))
            data["rotation"] = ParseVector3(rotation);

        parameters.TryGetValue("scale", out var scale);
        if (!string.IsNullOrEmpty(scale))
            data["scale"] = ParseVector3(scale);

        var body = JsonSerializer.Serialize(data);
        return await PostAsync("/modify_object", body);
    }

    private async Task<ToolResult> RenderAsync(IReadOnlyDictionary<string, string> parameters)
    {
        parameters.TryGetValue("camera", out var camera);
        parameters.TryGetValue("path", out var path);

        var url = "/render";
        if (!string.IsNullOrEmpty(camera))
            url += $"?camera={Uri.EscapeDataString(camera)}";

        var result = await GetAsync(url);
        return result;
    }

    private async Task<ToolResult> SaveAsync(IReadOnlyDictionary<string, string> parameters)
    {
        parameters.TryGetValue("path", out var path);
        var body = JsonSerializer.Serialize(new { path = path ?? "" });
        return await PostAsync("/save", body);
    }

    private async Task<ToolResult> NewSceneAsync(IReadOnlyDictionary<string, string> parameters)
    {
        var body = JsonSerializer.Serialize(new { clear_default = true });
        return await PostAsync("/new_scene", body);
    }

    private async Task<ToolResult> GetAsync(string endpoint)
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_apiUrl}{endpoint}");
            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("error", out var error))
                return ToolResult.Failed(error.GetString() ?? "Erreur inconnue");

            return ToolResult.Succeeded(json);
        }
        catch (Exception ex)
        {
            return ToolResult.Failed($"Erreur de connexion : {ex.Message}");
        }
    }

    private async Task<ToolResult> PostAsync(string endpoint, string body)
    {
        try
        {
            var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync($"{_apiUrl}{endpoint}", content);
            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("error", out var error))
                return ToolResult.Failed(error.GetString() ?? "Erreur inconnue");

            return ToolResult.Succeeded(json);
        }
        catch (Exception ex)
        {
            return ToolResult.Failed($"Erreur de connexion : {ex.Message}");
        }
    }

    private static float[] ParseVector3(string input)
    {
        var parts = input.Trim('[', ']', ' ').Split(',', StringSplitOptions.RemoveEmptyEntries);
        var result = new float[3];
        for (int i = 0; i < Math.Min(3, parts.Length); i++)
        {
            if (float.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var val))
                result[i] = val;
        }
        return result;
    }
}
