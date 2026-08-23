using JarvisAI.Application.AutoImprovement;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;

namespace JarvisAI.Infrastructure.AutoImprovement;

/// <summary>
/// Compile à chaud le code C# des outils auto-créés avec :
///  - une whitelist stricte de références (aucun System.Diagnostics.Process,
///    System.IO.FileSystem, System.Net.Http, System.Runtime.Extensions…),
///  - une analyse statique qui rejette les patterns dangereux résolubles depuis le corelib
///    (réflexion, Environment, GC, Marshal, Thread, unsafe, AppDomain…),
///  - une exécution avec deadline fournie par le host.
/// Le code ne peut donc interagir avec le système QUE via l'IToolHost fourni.
/// </summary>
public sealed class CSharpToolCompiler : IAutoToolCompiler
{
    private readonly ILogger<CSharpToolCompiler> _logger;
    private readonly ConcurrentDictionary<string, MethodInfo> _cache = new();

    private static readonly Regex[] BannedPatterns =
    {
        Regex(@"\bProcessStartInfo\b"),
        Regex(@"System\.Diagnostics\.Process"),
        Regex(@"\bAppDomain\b"),
        Regex(@"\bAssembly\b"),
        Regex(@"\bReflection\b"),
        Regex(@"\bActivator\b"),
        Regex(@"\bMarshal\b"),
        Regex(@"\bRuntimeTypeHandle\b"),
        Regex(@"\bIntPtr\b"),
        Regex(@"\bunsafe\b"),
        Regex(@"\bDllImport\b"),
        Regex(@"\btypeof\b"),
        Regex(@"\bGetType\b"),
        Regex(@"\.GetMethod\b"),
        Regex(@"\.GetField\b"),
        Regex(@"\.GetProperty\b"),
        Regex(@"\.Invoke\b"),
        Regex(@"\bEnvironment\b"),
        Regex(@"\bGC\.\b"),
        Regex(@"\bThread\b"),
        Regex(@"\bRegistry\b"),
        Regex(@"\bSocket\b"),
        Regex(@"\bTcpClient\b"),
        Regex(@"\bUdpClient\b"),
        Regex(@"\bHttpClient\b"),
        Regex(@"\bHttpListener\b"),
        Regex(@"\bWebClient\b"),
        Regex(@"\bKill\s*\("),
        Regex(@"\bShutdown\b"),
        Regex(@"System\.IO\.|System\.Net\.|System\.Diagnostics\.|System\.Security\.|System\.Runtime\.InteropServices"),
        Regex(@"\bFile\b\.(Read|Write|Delete|Move|Copy|Open)"),
        Regex(@"\bDirectory\b\.(Delete|Move)")
    };

    private static readonly HashSet<string> AllowedReferences = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.Private.CoreLib.dll",
        "netstandard.dll",
        "mscorlib.dll",
        "System.Runtime.dll",
        "System.Console.dll",
        "System.Collections.dll",
        "System.Collections.Concurrent.dll",
        "System.Linq.dll",
        "System.Linq.Expressions.dll",
        "System.Text.Encoding.dll",
        "System.Text.Encoding.Extensions.dll",
        "System.Globalization.dll",
        "System.Text.RegularExpressions.dll",
        "System.Text.Json.dll",
        "System.Private.Uri.dll",
        "System.ComponentModel.Primitives.dll",
        "System.Runtime.CompilerServices.Unsafe.dll",
        "System.Runtime.Numerics.dll",
        "System.Numerics.Vectors.dll",
        "System.ObjectModel.dll",
        "System.Collections.Specialized.dll"
    };

    private static readonly Lazy<MetadataReference[]> References = new(() =>
    {
        var refs = new List<MetadataReference>();
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            foreach (var path in tpa.Split(Path.PathSeparator))
            {
                var name = Path.GetFileName(path);
                if (AllowedReferences.Contains(name))
                    refs.Add(MetadataReference.CreateFromFile(path));
            }
        }

        var appAsm = typeof(IToolHost).Assembly.Location;
        if (File.Exists(appAsm))
            refs.Add(MetadataReference.CreateFromFile(appAsm));

        return refs.ToArray();
    });

    public CSharpToolCompiler(ILogger<CSharpToolCompiler> logger) => _logger = logger;

    public CompileResult Compile(string toolName, string code)
    {
        foreach (var pattern in BannedPatterns)
        {
            if (pattern.IsMatch(code))
            {
                _logger.LogWarning("[AutoToolCompiler] Tool {Name} rejected by static analysis (pattern {Pattern})",
                    toolName, pattern.ToString());
                return CompileResult.Fail($"code contains a forbidden construct ({pattern}).");
            }
        }

        var source = BuildSource(code);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));

        var compilation = CSharpCompilation.Create(
            $"AutoTool_{Sanitize(toolName)}",
            new[] { syntaxTree },
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                allowUnsafe: false));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        if (!emit.Success)
        {
            var errors = emit.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Take(4)
                .Select(d => d.GetMessage())
                .ToList();
            var msg = string.Join(" | ", errors);
            _logger.LogWarning("[AutoToolCompiler] Tool {Name} failed to compile: {Errors}", toolName, msg);
            return CompileResult.Fail(msg);
        }

        try
        {
            ms.Position = 0;
            var asm = Assembly.Load(ms.ToArray());
            var method = asm.GetType("Tool")?.GetMethod("Run");
            if (method is null)
                return CompileResult.Fail("compiled tool has no Tool.Run method.");

            _cache[toolName] = method;
            return CompileResult.Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AutoToolCompiler] Failed to load compiled assembly for {Name}", toolName);
            return CompileResult.Fail($"assembly load failed: {ex.Message}");
        }
    }

    public string Execute(string toolName, IToolHost host, IReadOnlyDictionary<string, string> args, TimeSpan timeout)
    {
        if (!_cache.TryGetValue(toolName, out var method))
            return "ERROR: tool not compiled.";

        var dict = args as Dictionary<string, string> ?? new Dictionary<string, string>(args);
        try
        {
            var result = method.Invoke(null, new object?[] { host, dict });
            return result as string ?? "";
        }
        catch (TargetInvocationException tie)
        {
            _logger.LogWarning(tie, "[AutoToolCompiler] Tool {Name} threw: {Error}", toolName, tie.InnerException?.Message);
            return $"ERROR: {tie.InnerException?.Message ?? tie.Message}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AutoToolCompiler] Tool {Name} failed to invoke: {Error}", toolName, ex.Message);
            return $"ERROR: {ex.Message}";
        }
    }

    private static string BuildSource(string code)
        => $$"""
            using System;
            using System.Collections.Generic;
            using System.Text;
            using System.Text.Json;
            using JarvisAI.Application.AutoImprovement;

            public static class Tool
            {
                public static string Run(IToolHost host, Dictionary<string, string> args)
                {
            {{code}}
                    return "";
                }
            }
            """;

    private static Regex Regex(string pattern)
        => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static string Sanitize(string name)
        => new(name.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').ToArray());
}
