using JarvisAI.Application.Agents;
using JarvisAI.Application.Security;
using JarvisAI.Application.Tools;
using JarvisAI.Infrastructure.Events;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace JarvisAI.Tests;

public sealed class FileSystemSandboxTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _outsideDir;
    private readonly List<string> _junctions = new();

    public FileSystemSandboxTests()
    {
        var baseDir = Path.GetTempPath();
        _testRoot = Path.Combine(baseDir, $"jarvis_sandbox_{Guid.NewGuid():N}");
        _outsideDir = Path.Combine(baseDir, $"jarvis_outside_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
        Directory.CreateDirectory(_outsideDir);
    }

    public void Dispose()
    {
        foreach (var junction in _junctions)
        {
            try { Directory.Delete(junction, recursive: false); } catch { }
        }
        try { Directory.Delete(_testRoot, recursive: true); } catch { }
        try { Directory.Delete(_outsideDir, recursive: true); } catch { }
    }

    private SecurityManager CreateSecurity()
    {
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        return new SecurityManager(
            new Lazy<IToolRegistry>(() => new ToolRegistry(NullLogger<ToolRegistry>.Instance)),
            new MockConfirmationService(),
            eventBus,
            NullLogger<SecurityManager>.Instance,
            new SecurityOptions
            {
                AllowedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _testRoot }
            });
    }

    private FileSystemTool CreateTool() => new(NullLogger<FileSystemTool>.Instance, CreateSecurity());

    private static Task<ToolResult> RunAsync(FileSystemTool tool, params (string K, string V)[] args)
    {
        var dict = new Dictionary<string, string>();
        foreach (var (k, v) in args) dict[k] = v;
        return tool.ExecuteAsync(new AgentContext("test"), dict);
    }

    // ================================================================
    // Every operation is sandboxed (read and write).
    // ================================================================

    [Theory]
    [InlineData("read_file")]
    [InlineData("get_file_info")]
    [InlineData("delete_file")]
    [InlineData("write_file")]
    [InlineData("create_file")]
    [InlineData("edit_file")]
    [InlineData("create_directory")]
    public async Task All_actions_block_paths_outside_sandbox(string action)
    {
        var tool = CreateTool();
        var outsideFile = Path.Combine(_outsideDir, "secret.txt");
        File.WriteAllText(outsideFile, "secret");

        var result = await RunAsync(tool, ("action", action), ("path", outsideFile), ("content", "x"));

        Assert.False(result.Success);
        Assert.Contains("Access denied", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("list_directory")]
    [InlineData("search_files")]
    public async Task Directory_actions_block_outside_paths(string action)
    {
        var tool = CreateTool();

        var result = await RunAsync(tool, ("action", action), ("path", _outsideDir), ("pattern", "*.*"));

        Assert.False(result.Success);
        Assert.Contains("Access denied", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Move_and_copy_validate_both_source_and_destination()
    {
        var tool = CreateTool();
        var insideFile = Path.Combine(_testRoot, "source.txt");
        File.WriteAllText(insideFile, "data");

        // Source inside, destination outside -> blocked.
        var result = await RunAsync(tool, ("action", "move_file"), ("path", insideFile), ("destination", Path.Combine(_outsideDir, "x.txt")));
        Assert.False(result.Success);
        Assert.Contains("Access denied", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        // Source outside, destination inside -> blocked.
        var result2 = await RunAsync(tool, ("action", "copy_file"), ("path", Path.Combine(_outsideDir, "secret.txt")), ("destination", Path.Combine(_testRoot, "y.txt")));
        Assert.False(result2.Success);
        Assert.Contains("Access denied", result2.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================
    // Path traversal via .. and mixed separators.
    // ================================================================

    [Theory]
    [InlineData(@"..\..\Windows\win.ini")]
    [InlineData(@"../Windows/win.ini")]
    [InlineData(@"..\Windows\System32\evil.txt")]
    public async Task Traversal_that_escapes_sandbox_is_blocked(string relative)
    {
        var tool = CreateTool();
        var path = Path.Combine(_testRoot, relative);

        var result = await RunAsync(tool, ("action", "read_file"), ("path", path));

        Assert.False(result.Success);
        Assert.Contains("Access denied", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Traversal_staying_inside_sandbox_is_normalized_and_allowed()
    {
        var tool = CreateTool();
        var nestedDir = Directory.CreateDirectory(Path.Combine(_testRoot, "sub", "deep"));
        var file = Path.Combine(nestedDir.FullName, "ok.txt");
        File.WriteAllText(file, "hello");

        // "sub/../sub/deep/ok.txt" normalizes to inside the sandbox.
        var path = Path.Combine(_testRoot, "sub", "..", "sub", "deep", "ok.txt");
        var result = await RunAsync(tool, ("action", "read_file"), ("path", path));

        Assert.True(result.Success);
        Assert.Contains("hello", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================
    // Symlink / junction pointing outside the sandbox.
    // ================================================================

    [Fact]
    public async Task Junction_pointing_outside_sandbox_is_blocked_for_reads()
    {
        var tool = CreateTool();
        var linkPath = Path.Combine(_testRoot, "link");
        if (!TryCreateJunction(linkPath, _outsideDir))
            return; // junction support unavailable on this machine - skip

        File.WriteAllText(Path.Combine(_outsideDir, "secret.txt"), "topsecret");

        // Reading through the junction must be blocked even though the textual
        // path is inside the sandbox.
        var result = await RunAsync(tool, ("action", "read_file"), ("path", Path.Combine(linkPath, "secret.txt")));

        Assert.False(result.Success);
        Assert.Contains("Access denied", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Junction_pointing_outside_sandbox_is_blocked_for_writes()
    {
        var tool = CreateTool();
        var linkPath = Path.Combine(_testRoot, "link");
        if (!TryCreateJunction(linkPath, _outsideDir))
            return; // junction support unavailable on this machine - skip

        var result = await RunAsync(tool, ("action", "write_file"), ("path", Path.Combine(linkPath, "evil.txt")), ("content", "boom"));

        Assert.False(result.Success);
        Assert.Contains("Access denied", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_outsideDir, "evil.txt")));
    }

    [Fact]
    public void SecurityManager_rejects_null_and_whitespace_paths()
    {
        var security = CreateSecurity();
        Assert.False(security.IsPathAllowed(null!));
        Assert.False(security.IsPathAllowed("  "));
    }

    [Fact]
    public void SecurityManager_normalizes_case_insensitive_windows_paths()
    {
        var security = CreateSecurity();
        var inside = Path.Combine(_testRoot, "Sub", "File.txt");
        Assert.True(security.IsPathAllowed(inside));
        Assert.True(security.IsPathAllowed(_testRoot.ToUpperInvariant()));
    }

    // ================================================================
    // edit_file : remplacement ciblé dans un fichier existant.
    // ================================================================

    [Fact]
    public async Task Edit_file_replaces_first_occurrence()
    {
        var tool = CreateTool();
        var file = Path.Combine(_testRoot, "edit.txt");
        File.WriteAllText(file, "Bonjour le monde, bonjour à nouveau.");

        var result = await RunAsync(tool,
            ("action", "edit_file"),
            ("path", file),
            ("find", "Bonjour"),
            ("replace", "salut"));

        Assert.True(result.Success, result.ErrorMessage);
        var content = File.ReadAllText(file);
        Assert.Contains("salut le monde", content);
        Assert.Contains("bonjour à nouveau", content);
    }

    [Fact]
    public async Task Edit_file_with_empty_replace_deletes_the_text()
    {
        var tool = CreateTool();
        var file = Path.Combine(_testRoot, "edit.txt");
        File.WriteAllText(file, "abc def abc");

        var result = await RunAsync(tool,
            ("action", "edit_file"),
            ("path", file),
            ("find", "def"),
            ("replace", ""));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("abc  abc", File.ReadAllText(file));
    }

    [Fact]
    public async Task Edit_file_missing_find_parameter_fails()
    {
        var tool = CreateTool();
        var file = Path.Combine(_testRoot, "edit.txt");
        File.WriteAllText(file, "contenu");

        var result = await RunAsync(tool, ("action", "edit_file"), ("path", file));

        Assert.False(result.Success);
        Assert.Contains("'find' is required", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Edit_file_text_not_found_fails_without_changes()
    {
        var tool = CreateTool();
        var file = Path.Combine(_testRoot, "edit.txt");
        File.WriteAllText(file, "contenu original");

        var result = await RunAsync(tool,
            ("action", "edit_file"),
            ("path", file),
            ("find", "introuvable"),
            ("replace", "x"));

        Assert.False(result.Success);
        Assert.Contains("not found", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("contenu original", File.ReadAllText(file));
    }

    [Fact]
    public async Task Edit_file_missing_file_fails()
    {
        var tool = CreateTool();

        var result = await RunAsync(tool,
            ("action", "edit_file"),
            ("path", Path.Combine(_testRoot, "absent.txt")),
            ("find", "x"),
            ("replace", "y"));

        Assert.False(result.Success);
        Assert.Contains("not found", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    private bool TryCreateJunction(string linkPath, string target)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{linkPath}\" \"{target}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(psi)!;
            process.WaitForExit(3000);
            if (process.ExitCode != 0 || !Directory.Exists(linkPath))
                return false;
            _junctions.Add(linkPath);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
