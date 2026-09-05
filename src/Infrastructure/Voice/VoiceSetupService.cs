using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Voice;

public interface IVoiceSetupService
{
    Task<VoiceSetupReport> CheckAndSetupAsync(CancellationToken ct = default);
    Task<bool> EnsurePythonDependenciesAsync(CancellationToken ct = default);
    Task<CudaInfo> CheckCudaAsync(CancellationToken ct = default);
    Task<bool> EnsureModelsAsync(CancellationToken ct = default);
    Task<bool> TestMicrophoneAsync(CancellationToken ct = default);
    VoiceSetupReport GetLastReport();
}

public sealed class VoiceSetupService : IVoiceSetupService
{
    private readonly ILogger<VoiceSetupService> _logger;
    private readonly string _voiceDir;
    private readonly string _pythonExe;
    private VoiceSetupReport _lastReport = new();

    // Required Python packages for voice
    private static readonly (string Package, string ImportName)[] RequiredPackages = new[]
    {
        ("edge-tts", "edge_tts"),
        ("fastapi", "fastapi"),
        ("uvicorn", "uvicorn"),
        ("openwakeword", "openwakeword"),
        ("numpy", "numpy"),
        ("pydantic", "pydantic"),
        ("websockets", "websockets"),
    };

    // STT packages (heavier, optional)
    private static readonly (string Package, string ImportName)[] SttPackages = new[]
    {
        ("faster-whisper", "faster_whisper"),
        ("torch", "torch"),
        ("torchaudio", "torchaudio"),
    };

    public VoiceSetupService(ILogger<VoiceSetupService> logger)
    {
        _logger = logger;
        _voiceDir = FindVoiceDirectory();
        _pythonExe = FindPythonExecutable();
    }

    public async Task<VoiceSetupReport> CheckAndSetupAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("[VoiceSetup] Début de la vérification complète...");

        var report = new VoiceSetupReport
        {
            StartedAt = DateTime.UtcNow,
            PythonFound = false,
            PythonPath = _pythonExe,
            VoiceDirectory = _voiceDir,
            Steps = new List<SetupStep>()
        };

        // Step 1: Check Python
        var pythonStep = await CheckPythonAsync(ct);
        report.Steps.Add(pythonStep);
        report.PythonFound = pythonStep.Success;

        if (!report.PythonFound)
        {
            report.Status = "failed";
            report.ErrorMessage = "Python non trouvé. Installez Python 3.10+ depuis python.org";
            _lastReport = report;
            return report;
        }

        // Step 2: Check/Install core dependencies
        var depsStep = await EnsureCoreDependenciesAsync(ct);
        report.Steps.Add(depsStep);
        report.DependenciesInstalled = depsStep.Success;

        // Step 3: Check CUDA
        var cudaStep = await CheckAndSetupCudaAsync(ct);
        report.Steps.Add(cudaStep);
        report.CudaInfo = cudaStep.CudaInfo ?? new CudaInfo { Available = false };

        // Step 4: Check/Install STT dependencies
        var sttStep = await EnsureSttDependenciesAsync(ct);
        report.Steps.Add(sttStep);

        // Step 5: Check models
        var modelsSuccess = await EnsureModelsAsync(ct);
        report.Steps.Add(new SetupStep
        {
            Name = "Modèles",
            Success = modelsSuccess,
            Message = modelsSuccess ? "Modèles disponibles" : "Erreur de téléchargement des modèles"
        });

        // Step 6: Check voice servers
        var serversStep = await CheckVoiceServersAsync(ct);
        report.Steps.Add(serversStep);

        report.CompletedAt = DateTime.UtcNow;
        report.Status = report.Steps.All(s => s.Success) ? "success" : "partial";
        report.Duration = report.CompletedAt.Value - report.StartedAt;

        _lastReport = report;
        _logger.LogInformation("[VoiceSetup] Vérification terminée: {Status} en {Duration}",
            report.Status, report.Duration);

        return report;
    }

    public async Task<bool> EnsurePythonDependenciesAsync(CancellationToken ct = default)
    {
        var result = await RunPythonCommandAsync(
            $"{_pythonExe} -m pip install --upgrade pip",
            ct);

        foreach (var (package, _) in RequiredPackages)
        {
            await RunPythonCommandAsync(
                $"{_pythonExe} -m pip install {package} --quiet",
                ct);
        }

        return true;
    }

    public async Task<CudaInfo> CheckCudaAsync(CancellationToken ct = default)
    {
        var cuda = new CudaInfo();

        try
        {
            // Check nvidia-smi
            var nvidiaSmi = await RunCommandAsync("nvidia-smi", "--query-gpu=name,memory.total,driver_version --format=csv,noheader", ct);
            if (!string.IsNullOrEmpty(nvidiaSmi))
            {
                cuda.Available = true;
                cuda.GpuName = nvidiaSmi.Split(',').FirstOrDefault()?.Trim() ?? "";
                cuda.MemoryMb = ParseMemory(nvidiaSmi);
                cuda.DriverVersion = nvidiaSmi.Split(',').LastOrDefault()?.Trim() ?? "";
            }
        }
        catch { }

        // Check torch CUDA
        try
        {
            var torchCheck = await RunPythonCommandAsync(
                $"{_pythonExe} -c \"import torch; print(torch.cuda.is_available()); print(torch.cuda.get_device_name(0) if torch.cuda.is_available() else '')\"",
                ct);

            var lines = torchCheck.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 0 && lines[0].Trim() == "True")
            {
                cuda.TorchCuda = true;
                if (lines.Length > 1)
                    cuda.TorchGpuName = lines[1].Trim();
            }
        }
        catch { }

        _logger.LogInformation("[VoiceSetup] CUDA: {Available}, GPU: {Gpu}, Torch: {Torch}",
            cuda.Available, cuda.GpuName, cuda.TorchCuda);

        return cuda;
    }

    public async Task<bool> EnsureModelsAsync(CancellationToken ct = default)
    {
        // Check if whisper model exists
        var modelsDir = Path.Combine(_voiceDir, "models");
        var whisperModel = Path.Combine(modelsDir, "small.en");

        if (!Directory.Exists(whisperModel))
        {
            _logger.LogInformation("[VoiceSetup] Téléchargement du modèle Whisper...");
            await RunPythonCommandAsync(
                $"{_pythonExe} -c \"from faster_whisper import WhisperModel; WhisperModel('small')\"",
                ct);
        }

        return true;
    }

    public async Task<bool> TestMicrophoneAsync(CancellationToken ct = default)
    {
        var result = await RunPythonCommandAsync(
            $"{_pythonExe} -c \"import sounddevice; print(sounddevice.query_devices())\"",
            ct);

        return !string.IsNullOrEmpty(result);
    }

    public VoiceSetupReport GetLastReport() => _lastReport;

    private async Task<SetupStep> CheckPythonAsync(CancellationToken ct)
    {
        var step = new SetupStep { Name = "Python", StartedAt = DateTime.UtcNow };

        try
        {
            var version = await RunPythonCommandAsync($"{_pythonExe} --version", ct);
            step.Success = version.Contains("Python 3");
            step.Details = version.Trim();
            step.Message = step.Success ? $"Python trouvé: {version.Trim()}" : "Python 3.10+ requis";
        }
        catch (Exception ex)
        {
            step.Success = false;
            step.ErrorMessage = ex.Message;
            step.Message = "Python non trouvé dans le PATH";
        }

        step.CompletedAt = DateTime.UtcNow;
        return step;
    }

    private async Task<SetupStep> EnsureCoreDependenciesAsync(CancellationToken ct)
    {
        var step = new SetupStep { Name = "Dépendances Core", StartedAt = DateTime.UtcNow };

        try
        {
            // Check if pip packages are installed
            var checkCmd = string.Join(";", RequiredPackages.Select(p => $"import {p.ImportName}"));
            var result = await RunPythonCommandAsync(
                $"{_pythonExe} -c \"{checkCmd}\"",
                ct);

            if (string.IsNullOrEmpty(result) || result.Contains("ModuleNotFoundError"))
            {
                _logger.LogInformation("[VoiceSetup] Installation des dépendances core...");
                foreach (var (package, _) in RequiredPackages)
                {
                    await RunPythonCommandAsync(
                        $"{_pythonExe} -m pip install {package} --quiet",
                        ct);
                }
            }

            step.Success = true;
            step.Message = $"{RequiredPackages.Length} packages installés";
        }
        catch (Exception ex)
        {
            step.Success = false;
            step.ErrorMessage = ex.Message;
        }

        step.CompletedAt = DateTime.UtcNow;
        return step;
    }

    private async Task<SetupStep> EnsureSttDependenciesAsync(CancellationToken ct)
    {
        var step = new SetupStep { Name = "Dépendances STT (Whisper)", StartedAt = DateTime.UtcNow };

        try
        {
            var checkCmd = string.Join(";", SttPackages.Select(p => $"import {p.ImportName}"));
            var result = await RunPythonCommandAsync(
                $"{_pythonExe} -c \"{checkCmd}\"",
                ct);

            if (string.IsNullOrEmpty(result) || result.Contains("ModuleNotFoundError"))
            {
                _logger.LogInformation("[VoiceSetup] Installation des dépendances STT...");
                foreach (var (package, _) in SttPackages)
                {
                    await RunPythonCommandAsync(
                        $"{_pythonExe} -m pip install {package} --quiet",
                        ct);
                }
            }

            step.Success = true;
            step.Message = $"{SttPackages.Length} packages STT installés";
        }
        catch (Exception ex)
        {
            step.Success = false;
            step.ErrorMessage = ex.Message;
        }

        step.CompletedAt = DateTime.UtcNow;
        return step;
    }

    private async Task<SetupStep> CheckAndSetupCudaAsync(CancellationToken ct)
    {
        var step = new SetupStep { Name = "CUDA/GPU", StartedAt = DateTime.UtcNow };

        try
        {
            var cuda = await CheckCudaAsync(ct);
            step.CudaInfo = cuda;
            step.Success = true;
            step.Message = cuda.Available
                ? $"GPU détecté: {cuda.GpuName} ({cuda.MemoryMb}MB)"
                : "GPU non disponible, mode CPU activé";
        }
        catch (Exception)
        {
            step.Success = true; // Not fatal
            step.Message = "GPU non détecté, mode CPU";
        }

        step.CompletedAt = DateTime.UtcNow;
        return step;
    }

    private async Task<SetupStep> CheckVoiceServersAsync(CancellationToken ct)
    {
        var step = new SetupStep { Name = "Serveurs Vocaux", StartedAt = DateTime.UtcNow };

        var servers = new[] { (Name: "STT", Port: 17001), (Name: "WakeWord", Port: 17002), (Name: "EdgeTTS", Port: 17004) };
        var results = new List<string>();

        foreach (var server in servers)
        {
            try
            {
                using var client = new System.Net.Http.HttpClient();
                client.Timeout = TimeSpan.FromSeconds(3);
                var response = await client.GetAsync($"http://127.0.0.1:{server.Port}/health", ct);
                results.Add($"{server.Name}: OK");
            }
            catch
            {
                results.Add($"{server.Name}: Arrêté");
            }
        }

        step.Success = true;
        step.Message = string.Join(", ", results);
        step.CompletedAt = DateTime.UtcNow;
        return step;
    }

    private async Task<string> RunPythonCommandAsync(string command, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _voiceDir
            };

            using var process = Process.Start(psi)!;
            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            return string.IsNullOrEmpty(stderr) ? stdout : $"{stdout}\n{stderr}";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[VoiceSetup] Command failed: {Command}", command);
            return "";
        }
    }

    private async Task<string> RunCommandAsync(string filename, string arguments, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = filename,
                Arguments = arguments,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi)!;
            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return output;
        }
        catch
        {
            return "";
        }
    }

    private static long ParseMemory(string csvLine)
    {
        try
        {
            var parts = csvLine.Split(',');
            foreach (var part in parts)
            {
                if (part.Contains("MiB") && long.TryParse(part.Replace("MiB", "").Trim(), out var mb))
                    return mb;
            }
        }
        catch { }
        return 0;
    }

    private static string FindVoiceDirectory()
    {
        // Check relative to exe
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(exeDir, "voice"),
            Path.Combine(exeDir, "wwwroot", "voice"),
            Path.Combine(exeDir, "..", "..", "..", "..", "src", "Web", "JarvisAI.Web", "voice"),
            @"C:\Users\belmi\Desktop\jarvis-ai\src\Web\JarvisAI.Web\voice"
        };

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "edge_tts_server.py")))
                return Path.GetFullPath(candidate);
        }

        return Path.Combine(exeDir, "voice");
    }

    private static string FindPythonExecutable()
    {
        var candidates = new[]
        {
            // Check venv first
            Path.Combine(FindVoiceDirectory(), ".venv", "Scripts", "python.exe"),
            // System Python
            "python.exe",
            "python3.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python312", "python.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python311", "python.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python310", "python.exe"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return "python.exe";
    }
}

public sealed class VoiceSetupReport
{
    public string Status { get; set; } = "pending";
    public bool PythonFound { get; set; }
    public bool DependenciesInstalled { get; set; }
    public string PythonPath { get; set; } = "";
    public string VoiceDirectory { get; set; } = "";
    public CudaInfo CudaInfo { get; set; } = new();
    public List<SetupStep> Steps { get; set; } = new();
    public string? ErrorMessage { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public TimeSpan? Duration { get; set; }
}

public sealed class SetupStep
{
    public string Name { get; set; } = "";
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string Details { get; set; } = "";
    public string? ErrorMessage { get; set; }
    public CudaInfo? CudaInfo { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public sealed class CudaInfo
{
    public bool Available { get; set; }
    public bool TorchCuda { get; set; }
    public string GpuName { get; set; } = "";
    public string TorchGpuName { get; set; } = "";
    public long MemoryMb { get; set; }
    public string DriverVersion { get; set; } = "";
}
