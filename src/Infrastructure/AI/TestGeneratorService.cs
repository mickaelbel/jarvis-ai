using Microsoft.Extensions.Logging;
using System.Text;

namespace JarvisAI.Infrastructure.AI;

public interface ITestGeneratorService
{
    TestGenerationResult GenerateTests(string code, string language, TestFramework framework = TestFramework.XUnit);
    IReadOnlyList<string> GetAvailableFrameworks(string language);
}

public sealed class TestGeneratorService : ITestGeneratorService
{
    private readonly ILogger<TestGeneratorService> _logger;

    public TestGeneratorService(ILogger<TestGeneratorService> logger)
    {
        _logger = logger;
    }

    public TestGenerationResult GenerateTests(string code, string language, TestFramework framework = TestFramework.XUnit)
    {
        var lang = language.ToLowerInvariant();
        var testCode = lang switch
        {
            "csharp" or "cs" => GenerateCSharpTests(code, framework),
            "python" or "py" => GeneratePythonTests(code),
            "javascript" or "js" => GenerateJavaScriptTests(code),
            _ => $"// Tests non supportés pour {lang}"
        };

        var methodCount = CountTestMethods(testCode);

        var result = new TestGenerationResult
        {
            Language = language,
            Framework = framework.ToString(),
            GeneratedCode = testCode,
            TestMethodCount = methodCount,
            GeneratedAt = DateTime.UtcNow
        };

        _logger.LogInformation("[TestGen] Generated {Count} tests for {Lang}", methodCount, language);
        return result;
    }

    public IReadOnlyList<string> GetAvailableFrameworks(string language)
    {
        return language.ToLowerInvariant() switch
        {
            "csharp" or "cs" => new[] { "XUnit", "NUnit", "MSTest" },
            "python" or "py" => new[] { "Pytest", "Unittest" },
            "javascript" or "js" => new[] { "Jest", "Mocha", "Vitest" },
            _ => Array.Empty<string>()
        };
    }

    private string GenerateCSharpTests(string code, TestFramework framework)
    {
        var sb = new StringBuilder();
        var className = ExtractClassName(code);
        var methods = ExtractMethods(code);

        var frameworkAttribute = framework switch
        {
            TestFramework.XUnit => "[Fact]",
            TestFramework.NUnit => "[Test]",
            TestFramework.MSTest => "[TestMethod]",
            _ => "[Fact]"
        };

        sb.AppendLine($"using Xunit;");
        sb.AppendLine($"using Moq;");
        sb.AppendLine();
        sb.AppendLine($"namespace {className}Tests;");
        sb.AppendLine();
        sb.AppendLine($"public class {className}Tests");
        sb.AppendLine("{");

        foreach (var method in methods)
        {
            sb.AppendLine($"    {frameworkAttribute}");
            sb.AppendLine($"    public void {method.Name}_ShouldReturnExpectedResult()");
            sb.AppendLine("    {");
            sb.AppendLine("        // Arrange");
            sb.AppendLine($"        var sut = new {className}();");
            sb.AppendLine();
            sb.AppendLine("        // Act");
            sb.AppendLine($"        var result = sut.{method.Name}({string.Join(", ", method.Parameters.Select(p => $"default({p.Type})"))});");
            sb.AppendLine();
            sb.AppendLine("        // Assert");
            sb.AppendLine("        Assert.NotNull(result);");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private string GeneratePythonTests(string code)
    {
        var sb = new StringBuilder();
        var functions = ExtractPythonFunctions(code);

        sb.AppendLine("import pytest");
        sb.AppendLine();

        foreach (var func in functions)
        {
            sb.AppendLine($"class Test{ToPascalCase(func.Name)}:");
            sb.AppendLine();
            sb.AppendLine($"    def test_{func.Name}_returns_expected(self):");
            sb.AppendLine("        # Arrange");
            sb.AppendLine($"        # TODO: Setup test data");
            sb.AppendLine();
            sb.AppendLine("        # Act");
            sb.AppendLine($"        # result = {func.Name}()");
            sb.AppendLine();
            sb.AppendLine("        # Assert");
            sb.AppendLine("        # assert result is not None");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private string GenerateJavaScriptTests(string code)
    {
        var sb = new StringBuilder();
        var functions = ExtractJSFunctions(code);

        sb.AppendLine("const { describe, it, expect } = require('@jest/globals');");
        sb.AppendLine();

        foreach (var func in functions)
        {
            sb.AppendLine($"describe('{func.Name}', () => {{");
            sb.AppendLine($"    it('should return expected result', () => {{");
            sb.AppendLine("        // Arrange");
            sb.AppendLine("        // TODO: Setup test data");
            sb.AppendLine();
            sb.AppendLine("        // Act");
            sb.AppendLine($"        // const result = {func.Name}();");
            sb.AppendLine();
            sb.AppendLine("        // Assert");
            sb.AppendLine("        // expect(result).toBeDefined();");
            sb.AppendLine("    });");
            sb.AppendLine("});");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private string ExtractClassName(string code)
    {
        var match = System.Text.RegularExpressions.Regex.Match(code, @"class\s+(\w+)");
        return match.Success ? match.Groups[1].Value : "MyClass";
    }

    private List<MethodInfo> ExtractMethods(string code)
    {
        var methods = new List<MethodInfo>();
        var matches = System.Text.RegularExpressions.Regex.Matches(code,
            @"(?:public|private|protected|internal)\s+(?:static\s+)?(?:async\s+)?(\w+)\s+(\w+)\s*\(([^)]*)\)");

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var returnType = match.Groups[1].Value;
            var name = match.Groups[2].Value;
            var paramsStr = match.Groups[3].Value;

            if (name == "ctor" || returnType == "class" || returnType == "interface") continue;

            var parameters = paramsStr.Split(',')
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p =>
                {
                    var parts = p.Trim().Split(' ');
                    return new ParameterInfo { Type = parts.Length > 0 ? parts[0] : "object", Name = parts.Length > 1 ? parts[1] : "param" };
                })
                .ToList();

            methods.Add(new MethodInfo { Name = name, ReturnType = returnType, Parameters = parameters });
        }

        return methods;
    }

    private List<PythonFunction> ExtractPythonFunctions(string code)
    {
        var functions = new List<PythonFunction>();
        var matches = System.Text.RegularExpressions.Regex.Matches(code,
            @"def\s+(\w+)\s*\(([^)]*)\):");

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            functions.Add(new PythonFunction
            {
                Name = match.Groups[1].Value,
                Parameters = match.Groups[2].Value.Split(',').Select(p => p.Trim()).ToList()
            });
        }

        return functions;
    }

    private List<JSFunction> ExtractJSFunctions(string code)
    {
        var functions = new List<JSFunction>();
        var matches = System.Text.RegularExpressions.Regex.Matches(code,
            @"(?:function\s+(\w+)|(?:const|let|var)\s+(\w+)\s*=\s*(?:\([^)]*\)|[^=])\s*=>)");

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var name = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            if (!string.IsNullOrEmpty(name))
                functions.Add(new JSFunction { Name = name });
        }

        return functions;
    }

    private int CountTestMethods(string testCode)
    {
        return System.Text.RegularExpressions.Regex.Matches(testCode,
            @"\[Fact\]|\[Test\]|\[TestMethod\]|def test_|it\('").Count;
    }

    private string ToPascalCase(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        return char.ToUpper(input[0]) + input[1..];
    }
}

public sealed class TestGenerationResult
{
    public string Language { get; set; } = "";
    public string Framework { get; set; } = "";
    public string GeneratedCode { get; set; } = "";
    public int TestMethodCount { get; set; }
    public DateTime GeneratedAt { get; set; }
}

public enum TestFramework { XUnit, NUnit, MSTest, Pytest, Jest }

internal class MethodInfo
{
    public string Name { get; set; } = "";
    public string ReturnType { get; set; } = "";
    public List<ParameterInfo> Parameters { get; set; } = new();
}

internal class ParameterInfo
{
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
}

internal class PythonFunction
{
    public string Name { get; set; } = "";
    public List<string> Parameters { get; set; } = new();
}

internal class JSFunction
{
    public string Name { get; set; } = "";
}
