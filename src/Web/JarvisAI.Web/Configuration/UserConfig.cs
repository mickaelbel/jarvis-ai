using System.Text;

namespace JarvisAI.Web.Configuration;

/// <summary>
/// Configuration utilisateur de Jarvis, stockée dans
/// %LOCALAPPDATA%\JarvisAI\appsettings.json.
///
/// L'installateur (MSI) ne dépose plus aucun fichier de configuration : au tout
/// premier lancement, ce fichier est créé à partir des valeurs par défaut
/// embarquées ci-dessous, puis le wizard /setup le complète. Il n'est jamais
/// écrasé par une mise à jour ni par une réinstallation.
/// </summary>
public static class UserConfig
{
    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JarvisAI");

    public static string FilePath => Path.Combine(DirectoryPath, "appsettings.json");

    /// <summary>Valeurs par défaut minimales (aucun secret).</summary>
    internal const string DefaultJson = """
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "JarvisAI": "Debug"
    }
  },
  "AllowedHosts": "localhost;127.0.0.1;[::1]",
  "AI": {
    "DefaultProvider": "Ollama",
    "DefaultModel": "llama3.1:latest",
    "FastModel": "llama3.1:latest",
    "ReasoningModel": "qwen3.5:8b",
    "CodeModel": "qwen2.5-coder:7b",
    "Temperature": 0.7,
    "MaxTokens": 2048,
    "MaxToolRounds": 5,
    "OllamaBaseUrl": "http://localhost:11434",
    "OpenAIBaseUrl": "https://api.openai.com",
    "OpenAIApiKey": ""
  },
  "JarvisAI": {
    "Ollama": {
      "BaseUrl": "http://localhost:11434",
      "DefaultModel": "llama3.1",
      "TimeoutMinutes": 30
    },
    "OpenAI": {
      "BaseUrl": "https://api.openai.com/v1",
      "ApiKey": "",
      "DefaultModel": "gpt-4o-mini",
      "Models": [ "gpt-4o-mini", "gpt-4o" ]
    },
    "Security": {
      "Mode": "Autonomous",
      "RequireConfirmationForHighRisk": false,
      "RequireConfirmationForMediumRisk": false,
      "VoiceConfirmationEnabled": false,
      "AllowDisableConfirmation": true,
      "MaxActionsPerMinute": 120,
      "N3RequiresConfirmation": true
    },
    "Presence": {
      "PhoneIp": "",
      "IntervalSeconds": 30,
      "AbsenceThresholdSeconds": 600,
      "Enabled": false
    },
    "Budget": {
      "DailyCapUsd": null,
      "MonthlyCapUsd": null,
      "AlertPercent": 80,
      "AutoSwitchToLocalAtCap": true,
      "LocalFallbackModel": "llama3.3:latest"
    },
    "Obs": {
      "Enabled": true,
      "Url": "ws://127.0.0.1:4455",
      "Password": ""
    },
    "Hue": {
      "BridgeIp": "",
      "AppKey": ""
    },
    "Wol": {
      "TargetMac": ""
    },
    "IPhoneToken": "",
    "Personality": {
      "Current": "neutre"
    }
  },
  "Update": {
    "Owner": "mickaelbel",
    "Repository": "jarvis-ai",
    "AutoInstall": true
  }
}
""";

    /// <summary>
    /// Crée le fichier de configuration utilisateur s'il n'existe pas encore.
    /// Sans effet (et sans exception) si le fichier est déjà présent.
    /// </summary>
    public static string EnsureCreated()
    {
        try
        {
            System.IO.Directory.CreateDirectory(DirectoryPath);
            if (!File.Exists(FilePath))
                File.WriteAllText(FilePath, DefaultJson, new UTF8Encoding(false));
        }
        catch
        {
            // Lecture seule / droits restreints : on laisse l'app démarrer avec
            // les valeurs par défaut du code plutôt que d'empêcher le lancement.
        }
        return FilePath;
    }
}
