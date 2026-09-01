namespace JarvisAI.Application.AI;

public sealed class AIOptions
{
    public string DefaultProvider { get; set; } = "Ollama";
    public string DefaultModel { get; set; } = "qwen3:8b";
    public string SystemPrompt { get; set; } = "You are Jarvis, a helpful AI assistant. You can use tools to answer questions. Be concise and accurate.";
    public float Temperature { get; set; } = 0.3f;
    public int MaxTokens { get; set; } = 2048;
    public int MaxToolRounds { get; set; } = 5;
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";
    public string OpenAIBaseUrl { get; set; } = "https://api.openai.com";
    public string OpenAIApiKey { get; set; } = string.Empty;
    public bool HiddenPlanningEnabled { get; set; } = true;
    public int HiddenPlanningMinChars { get; set; } = 120;
    public int HiddenPlanningMaxUserTurns { get; set; } = 3;
    public bool SelfVerificationEnabled { get; set; } = false;
    public bool ToolPruningEnabled { get; set; } = true;
}
