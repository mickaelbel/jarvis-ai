namespace JarvisAI.Infrastructure.AI;

/// <summary>Configuration d'un fournisseur IA (clé API, endpoint, modèles proposés).</summary>
public sealed class AiProviderSettings
{
    public bool Enabled { get; set; } = true;
    public string DisplayName { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string DefaultModel { get; set; } = "";
    public List<string> Models { get; set; } = new();
}

/// <summary>
/// Catalogue des fournisseurs connus (gratuits ou payants) et de leurs modèles,
/// utilisés pour la sélection vocale/chat et le routage. Les clés API ne sont
/// jamais stockées ici : elles vivent dans AiProviderSettingsStore.
/// </summary>
public sealed record AiProviderCatalogEntry(
    string Key,
    string DisplayName,
    string Description,
    bool RequiresKey,
    string DefaultBaseUrl,
    string ChatPath,
    string DefaultModel,
    IReadOnlyList<string> Models,
    IReadOnlyList<string> ModelPrefixes);

public static class AiProviderCatalog
{
    public static readonly AiProviderCatalogEntry[] All =
    {
        new(
            Key: "openai",
            DisplayName: "OpenAI",
            Description: "gpt-4o-mini (API payante, crédit de départ)",
            RequiresKey: true,
            DefaultBaseUrl: "https://api.openai.com/v1",
            ChatPath: "/chat/completions",
            DefaultModel: "gpt-4o-mini",
            Models: new[] { "gpt-4o-mini", "gpt-4o" },
            ModelPrefixes: new[] { "gpt-", "o1", "o3", "o4", "chatgpt-" }),
        new(
            Key: "groq",
            DisplayName: "Groq (gratuit, rapide)",
            Description: "API gratuite, très rapide. Clé sur console.groq.com",
            RequiresKey: true,
            DefaultBaseUrl: "https://api.groq.com/openai/v1",
            ChatPath: "/chat/completions",
            DefaultModel: "llama-3.3-70b-versatile",
            Models: new[]
            {
                "llama-3.3-70b-versatile",
                "llama-3.1-8b-instant",
                "llama3-70b-8192",
                "qwen-2.5-32b",
                "gemma2-9b-it",
                "mixtral-8x7b-32768",
                "deepseek-r1-distill-llama-70b",
                "llama-3.2-3b-preview"
            },
            ModelPrefixes: Array.Empty<string>()),
        new(
            Key: "gemini",
            DisplayName: "Google Gemini (offre gratuite)",
            Description: "Gemini Flash gratuit via l'API OpenAI-compatible de Google",
            RequiresKey: true,
            DefaultBaseUrl: "https://generativelanguage.googleapis.com/v1beta/openai",
            ChatPath: "/chat/completions",
            DefaultModel: "gemini-2.0-flash",
            Models: new[] { "gemini-2.5-flash", "gemini-2.0-flash", "gemini-2.0-flash-lite" },
            ModelPrefixes: new[] { "gemini-" }),
        new(
            Key: "openrouter",
            DisplayName: "OpenRouter (modèles gratuits)",
            Description: "Accès à des dizaines de modèles 100% gratuits (:free)",
            RequiresKey: true,
            DefaultBaseUrl: "https://openrouter.ai/api/v1",
            ChatPath: "/chat/completions",
            DefaultModel: "meta-llama/llama-3.3-70b-instruct:free",
            Models: new[]
            {
                "meta-llama/llama-3.3-70b-instruct:free",
                "meta-llama/llama-3.1-8b-instruct:free",
                "qwen/qwen-2.5-72b-instruct:free",
                "mistralai/mistral-7b-instruct:free",
                "google/gemma-2-9b-it:free",
                "deepseek/deepseek-chat-v3-0324:free",
                "microsoft/phi-3-medium-128k-instruct:free"
            },
            ModelPrefixes: new[] { ":free" }),
        new(
            Key: "huggingface",
            DisplayName: "Hugging Face (inférence gratuite)",
            Description: "Router HF, token gratuit sur huggingface.co",
            RequiresKey: true,
            DefaultBaseUrl: "https://router.huggingface.co/v1",
            ChatPath: "/chat/completions",
            DefaultModel: "meta-llama/Llama-3.1-8B-Instruct",
            Models: new[]
            {
                "meta-llama/Llama-3.1-8B-Instruct",
                "Qwen/Qwen2.5-72B-Instruct",
                "mistralai/Mistral-7B-Instruct-v0.3",
                "google/gemma-2-9b-it"
            },
            ModelPrefixes: Array.Empty<string>()),
        new(
            Key: "localserver",
            DisplayName: "Serveur local (LM Studio / llama.cpp)",
            Description: "Endroit OpenAI-compatible local, gratuit et illimité",
            RequiresKey: false,
            DefaultBaseUrl: "http://localhost:1234/v1",
            ChatPath: "/chat/completions",
            DefaultModel: "",
            Models: Array.Empty<string>(),
            ModelPrefixes: Array.Empty<string>()),
    };

    public static AiProviderCatalogEntry? Find(string key)
        => All.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
}
