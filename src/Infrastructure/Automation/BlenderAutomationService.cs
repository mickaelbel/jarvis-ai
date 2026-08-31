using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace JarvisAI.Infrastructure.Automation;

public interface IBlenderAutomationService
{
    Task<BlenderResult> OpenBlenderAsync(CancellationToken ct = default);
    Task<BlenderResult> CreateNewFileAsync(BlenderFile_type type = BlenderFile_type.General, CancellationToken ct = default);
    Task<BlenderResult> DeleteDefaultCubeAsync(CancellationToken ct = default);
    Task<BlenderResult> SaveFileAsync(string outputPath, CancellationToken ct = default);
    Task<BlenderResult> ExecuteFullWorkflowAsync(string outputPath, CancellationToken ct = default);
    Task<BlenderResult> ExecuteBlenderPythonAsync(string pythonCode, CancellationToken ct = default);
    bool IsBlenderInstalled();
    string? GetBlenderPath();
}

public enum BlenderFile_type
{
    General,
    VideoEditing,
    VFX,
    Sculpting
}

public sealed class BlenderAutomationService : IBlenderAutomationService
{
    private readonly ILogger<BlenderAutomationService> _logger;
    private readonly string _blenderPath;

    public BlenderAutomationService(ILogger<BlenderAutomationService> logger)
    {
        _logger = logger;
        _blenderPath = FindBlender();
    }

    public bool IsBlenderInstalled() => !string.IsNullOrEmpty(_blenderPath) && File.Exists(_blenderPath);

    public string? GetBlenderPath() => _blenderPath;

    public async Task<BlenderResult> OpenBlenderAsync(CancellationToken ct = default)
    {
        var result = new BlenderResult { Operation = "Open Blender" };

        if (!IsBlenderInstalled())
        {
            result.ErrorMessage = "Blender not found. Install from https://www.blender.org/download/";
            return result;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _blenderPath,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            };

            Process.Start(psi);
            result.Success = true;
            _logger.LogInformation("[Blender] Opened: {Path}", _blenderPath);

            // Wait for Blender to start
            await Task.Delay(3000, ct);
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            _logger.LogError(ex, "[Blender] Failed to open");
        }

        return result;
    }

    public async Task<BlenderResult> CreateNewFileAsync(BlenderFile_type type = BlenderFile_type.General, CancellationToken ct = default)
    {
        var result = new BlenderResult { Operation = "Create New File" };

        var pythonCode = type switch
        {
            BlenderFile_type.General => "bpy.ops.wm.read_factory_settings(use_empty=False)",
            BlenderFile_type.VideoEditing => "bpy.ops.wm.read_factory_settings(use_empty=True)",
            BlenderFile_type.VFX => "bpy.ops.wm.read_factory_settings(use_empty=False)",
            BlenderFile_type.Sculpting => "bpy.ops.wm.read_factory_settings(use_empty=True)",
            _ => "bpy.ops.wm.read_factory_settings(use_empty=False)"
        };

        return await ExecuteBlenderPythonAsync(pythonCode, ct);
    }

    public async Task<BlenderResult> DeleteDefaultCubeAsync(CancellationToken ct = default)
    {
        var result = new BlenderResult { Operation = "Delete Default Cube" };

        var pythonCode = @"
import bpy

# Select all objects
bpy.ops.object.select_all(action='SELECT')

# Delete selected objects (including the default cube)
bpy.ops.object.delete(use_global=False)

print('Default objects deleted successfully')
";

        return await ExecuteBlenderPythonAsync(pythonCode, ct);
    }

    public async Task<BlenderResult> SaveFileAsync(string outputPath, CancellationToken ct = default)
    {
        var result = new BlenderResult { Operation = "Save File", OutputPath = outputPath };

        // Ensure output directory exists
        var dir = Path.GetDirectoryName(outputPath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var pythonCode = $@"
import bpy

# Save the file
bpy.ops.wm.save_as_mainfile(filepath=r'{outputPath}')
print(f'File saved to: {outputPath}')
";

        return await ExecuteBlenderPythonAsync(pythonCode, ct);
    }

    public async Task<BlenderResult> ExecuteFullWorkflowAsync(string outputPath, CancellationToken ct = default)
    {
        var result = new BlenderResult { Operation = "Full Workflow" };

        _logger.LogInformation("[Blender] Starting full workflow...");

        // Step 1: Open Blender if not running
        if (!IsBlenderRunning())
        {
            var openResult = await OpenBlenderAsync(ct);
            if (!openResult.Success)
            {
                result.ErrorMessage = $"Failed to open Blender: {openResult.ErrorMessage}";
                return result;
            }
            await Task.Delay(2000, ct);
        }

        // Step 2: Create new file (General)
        var createResult = await CreateNewFileAsync(BlenderFile_type.General, ct);
        if (!createResult.Success)
        {
            result.ErrorMessage = $"Failed to create new file: {createResult.ErrorMessage}";
            return result;
        }
        await Task.Delay(1000, ct);

        // Step 3: Delete default cube
        var deleteResult = await DeleteDefaultCubeAsync(ct);
        if (!deleteResult.Success)
        {
            result.ErrorMessage = $"Failed to delete cube: {deleteResult.ErrorMessage}";
            return result;
        }
        await Task.Delay(1000, ct);

        // Step 4: Save file
        var saveResult = await SaveFileAsync(outputPath, ct);
        if (!saveResult.Success)
        {
            result.ErrorMessage = $"Failed to save file: {saveResult.ErrorMessage}";
            return result;
        }

        result.Success = true;
        result.OutputPath = outputPath;
        _logger.LogInformation("[Blender] Workflow completed: {Path}", outputPath);

        return result;
    }

    public async Task<BlenderResult> ExecuteBlenderPythonAsync(string pythonCode, CancellationToken ct = default)
    {
        var result = new BlenderResult { Operation = "Execute Python" };

        if (!IsBlenderInstalled())
        {
            result.ErrorMessage = "Blender not installed";
            return result;
        }

        try
        {
            // Write Python script to temp file
            var tempScript = Path.Combine(Path.GetTempPath(), $"blender_script_{Guid.NewGuid():N}.py");
            await File.WriteAllTextAsync(tempScript, pythonCode, ct);

            var psi = new ProcessStartInfo
            {
                FileName = _blenderPath,
                Arguments = $"--background --python \"{tempScript}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process is not null)
            {
                var output = await process.StandardOutput.ReadToEndAsync(ct);
                var error = await process.StandardError.ReadToEndAsync(ct);

                await process.WaitForExitAsync(ct);

                // Clean up temp script
                try { File.Delete(tempScript); } catch { }

                if (process.ExitCode == 0)
                {
                    result.Success = true;
                    result.Output = output;
                    _logger.LogInformation("[Blender] Python executed successfully");
                }
                else
                {
                    result.ErrorMessage = $"Blender exited with code {process.ExitCode}: {error}";
                    _logger.LogWarning("[Blender] Python execution failed: {Error}", error);
                }
            }
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            _logger.LogError(ex, "[Blender] Failed to execute Python");
        }

        return result;
    }

    private static string FindBlender()
    {
        // Check common installation paths
        var paths = new[]
        {
            @"C:\Program Files\Blender Foundation\Blender 5.1\blender.exe",
            @"C:\Program Files\Blender Foundation\Blender 5.0\blender.exe",
            @"C:\Program Files\Blender Foundation\Blender 4.2\blender.exe",
            @"C:\Program Files\Blender Foundation\Blender 4.1\blender.exe",
            @"C:\Program Files\Blender Foundation\Blender 4.0\blender.exe",
            @"C:\Program Files\Blender Foundation\Blender 3.6\blender.exe",
            @"C:\Program Files\Blender Foundation\Blender 3.5\blender.exe",
            @"C:\Program Files\Blender Foundation\Blender 3.4\blender.exe",
        };

        foreach (var path in paths)
        {
            if (File.Exists(path))
                return path;
        }

        // Try PATH
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "blender",
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };

            using var process = Process.Start(psi);
            if (process is not null)
            {
                process.WaitForExit(2000);
                if (process.ExitCode == 0)
                    return "blender";
            }
        }
        catch { }

        // Try registry
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Blender Foundation\Blender");
            if (key is not null)
            {
                var installPath = key.GetValue("InstallDir")?.ToString();
                if (!string.IsNullOrEmpty(installPath))
                {
                    var exePath = Path.Combine(installPath, "blender.exe");
                    if (File.Exists(exePath))
                        return exePath;
                }
            }
        }
        catch { }

        return "";
    }

    private static bool IsBlenderRunning()
    {
        return Process.GetProcessesByName("blender").Length > 0;
    }
}

public sealed class BlenderResult
{
    public bool Success { get; set; }
    public string Operation { get; set; } = "";
    public string? OutputPath { get; set; }
    public string? Output { get; set; }
    public string? ErrorMessage { get; set; }
}
