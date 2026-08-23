using JarvisAI.Application.Agents;
using JarvisAI.Application.AutoImprovement;
using JarvisAI.Application.Tools;
using JarvisAI.Infrastructure.AutoImprovement;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public class SelfImprovementTests
{
    private sealed class SelfImpHost : IToolHost
    {
        public string NowUtc() => "2026-01-01T00:00:00.0000000Z";
        public string ReadTextFile(string path) => "nope";
        public string WriteTextFile(string path, string content) => "ok";
        public string HttpGet(string url) => "{}";
        public string Log(string message) => "";
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jarvis-selfimp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static (SelfImprovementManager manager, ToolRegistry registry) CreateManager(string dir, out AutoToolStore store)
    {
        store = new AutoToolStore(NullLogger<AutoToolStore>.Instance, dir);
        var compiler = new CSharpToolCompiler(NullLogger<CSharpToolCompiler>.Instance);
        var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        var executor = new FakeToolExecutor();
        var manager = new SelfImprovementManager(
            store,
            new Lazy<IToolRegistry>(() => registry),
            new Lazy<IToolExecutor>(() => executor),
            compiler,
            new ToolHostFactory(NullLogger<ToolHost>.Instance),
            NullLogger<SelfImprovementManager>.Instance);
        return (manager, registry);
    }

    [Fact]
    public void Compiler_compiles_and_executes_csharp_tool()
    {
        var compiler = new CSharpToolCompiler(NullLogger<CSharpToolCompiler>.Instance);
        var compile = compiler.Compile("greeter", """
            var who = args.ContainsKey("name") ? args["name"] : "world";
            return "Hello " + who + "! It is " + host.NowUtc() + ".";
            """);
        Assert.True(compile.Success, compile.Error);

        var output = compiler.Execute("greeter", new SelfImpHost(),
            new Dictionary<string, string> { ["name"] = "Ada" }, TimeSpan.FromSeconds(10));
        Assert.Equal("Hello Ada! It is 2026-01-01T00:00:00.0000000Z.", output);
    }

    [Theory]
    [InlineData("System.Diagnostics.Process.Start(\"x\"); return \"\";")]
    [InlineData("var t = typeof(int); return \"\";")]
    [InlineData("System.IO.File.ReadAllText(\"C:/Windows/win.ini\"); return \"\";")]
    [InlineData("return System.Environment.UserName;")]
    [InlineData("var m = typeof(Tool).GetMethod(\"Run\"); return \"\";")]
    public void Compiler_rejects_forbidden_constructs(string code)
    {
        var compiler = new CSharpToolCompiler(NullLogger<CSharpToolCompiler>.Instance);
        Assert.False(compiler.Compile("badtool", code).Success);
    }

    [Fact]
    public async Task Manager_creates_csharp_tool_and_it_executes()
    {
        var (manager, registry) = CreateManager(TempDir(), out _);

        var result = manager.CreateTool(new AutoToolSpec
        {
            Name = "greeter",
            Description = "Greets a person",
            RiskLevel = "low",
            Runtime = AutoToolRuntimes.CSharp,
            Code = """
                var who = args.ContainsKey("name") ? args["name"] : "world";
                return "Hello " + who + "!";
                """
        }, "user");
        Assert.True(result.Success, result.Error);
        Assert.NotNull(registry.GetByName("greeter"));

        var output = await registry.GetByName("greeter")!.ExecuteAsync(
            new AgentContext("greet"), new Dictionary<string, string> { ["name"] = "Ada" });
        Assert.True(output.Success, output.ErrorMessage);
        Assert.Equal("Hello Ada!", output.Output);
    }

    [Fact]
    public async Task Manager_creates_recipe_tool_and_it_executes_steps()
    {
        var (manager, registry) = CreateManager(TempDir(), out _);
        registry.Register(new TestTool("system_info", "sys info"));
        registry.Register(new TestTool("date_time", "current time"));
        var executor = new FakeToolExecutor();

        var result = manager.CreateTool(new AutoToolSpec
        {
            Name = "system_summary",
            Description = "Machine summary",
            RiskLevel = "low",
            Runtime = AutoToolRuntimes.Recipe,
            Steps =
            {
                new AutoToolRecipeStep { ToolName = "system_info" },
                new AutoToolRecipeStep { ToolName = "date_time" }
            }
        }, "ai");
        Assert.True(result.Success, result.Error);
        Assert.IsType<RecipeTool>(registry.GetByName("system_summary"));

        var output = await registry.GetByName("system_summary")!.ExecuteAsync(
            new AgentContext("summarize"), new Dictionary<string, string>());
        Assert.True(output.Success, output.ErrorMessage);
        Assert.Contains("system_info", output.Output);
        Assert.Contains("date_time", output.Output);
    }

    [Theory]
    [InlineData("terminal")]
    [InlineData("create_tool")]
    [InlineData("X")]
    [InlineData("bad name")]
    [InlineData("BAD_name")]
    public void Manager_rejects_invalid_or_reserved_names(string name)
    {
        var (manager, _) = CreateManager(TempDir(), out _);
        var result = manager.CreateTool(new AutoToolSpec
        {
            Name = name,
            Description = "d",
            RiskLevel = "low",
            Runtime = AutoToolRuntimes.Recipe,
            Steps = { new AutoToolRecipeStep { ToolName = "date_time" } }
        }, "ai");
        Assert.False(result.Success);
    }

    [Fact]
    public void Manager_allows_recipe_chaining_another_auto_tool()
    {
        var (manager, registry) = CreateManager(TempDir(), out _);

        Assert.True(manager.CreateTool(new AutoToolSpec
        {
            Name = "helper",
            Description = "helper",
            RiskLevel = "low",
            Runtime = AutoToolRuntimes.CSharp,
            Code = "return \"helper\";"
        }, "ai").Success);

        var result = manager.CreateTool(new AutoToolSpec
        {
            Name = "wrapper",
            Description = "wraps helper",
            RiskLevel = "low",
            Runtime = AutoToolRuntimes.Recipe,
            Steps = { new AutoToolRecipeStep { ToolName = "helper" } }
        }, "ai");
        Assert.True(result.Success, result.Error);
        Assert.IsType<RecipeTool>(registry.GetByName("wrapper"));
    }

    [Fact]
    public void Manager_rejects_recipe_direct_self_reference()
    {
        var (manager, _) = CreateManager(TempDir(), out _);
        var result = manager.CreateTool(new AutoToolSpec
        {
            Name = "loop",
            Description = "d",
            RiskLevel = "low",
            Runtime = AutoToolRuntimes.Recipe,
            Steps = { new AutoToolRecipeStep { ToolName = "loop" } }
        }, "ai");
        Assert.False(result.Success);
    }

    [Fact]
    public void Manager_rejects_recipe_cycle_through_other_auto_tools()
    {
        var (manager, _) = CreateManager(TempDir(), out _);

        Assert.True(manager.CreateTool(new AutoToolSpec
        {
            Name = "alpha",
            Description = "alpha",
            RiskLevel = "low",
            Runtime = AutoToolRuntimes.CSharp,
            Code = "return \"alpha\";"
        }, "ai").Success);

        Assert.True(manager.CreateTool(new AutoToolSpec
        {
            Name = "beta",
            Description = "wraps alpha",
            RiskLevel = "low",
            Runtime = AutoToolRuntimes.Recipe,
            Steps = { new AutoToolRecipeStep { ToolName = "alpha" } }
        }, "ai").Success);

        var upgradeAlpha = manager.CreateTool(new AutoToolSpec
        {
            Name = "alpha",
            Description = "now wraps beta",
            RiskLevel = "low",
            Runtime = AutoToolRuntimes.Recipe,
            Steps = { new AutoToolRecipeStep { ToolName = "beta" } }
        }, "ai");
        Assert.False(upgradeAlpha.Success);
        Assert.Contains("cycle", upgradeAlpha.Error);
    }

    [Fact]
    public void Manager_rejects_recipe_with_unknown_step()
    {
        var (manager, _) = CreateManager(TempDir(), out _);
        var result = manager.CreateTool(new AutoToolSpec
        {
            Name = "mystery",
            Description = "d",
            RiskLevel = "low",
            Runtime = AutoToolRuntimes.Recipe,
            Steps = { new AutoToolRecipeStep { ToolName = "no_such_tool" } }
        }, "ai");
        Assert.False(result.Success);
        Assert.Contains("unknown tool", result.Error);
    }

    [Fact]
    public void Persisted_tools_survive_reload_and_safe_mode_skips()
    {
        var dir = TempDir();
        var (manager, registry) = CreateManager(dir, out _);
        Assert.True(manager.CreateTool(new AutoToolSpec
        {
            Name = "persisted_tool",
            Description = "d",
            RiskLevel = "low",
            Runtime = AutoToolRuntimes.CSharp,
            Code = "return \"persisted\";"
        }, "user").Success);

        var (_, reloadedRegistry) = CreateManager(dir, out var reloadedStore);
        reloadedStore.SafeMode = true;
        var manager2 = new SelfImprovementManager(
            reloadedStore,
            new Lazy<IToolRegistry>(() => reloadedRegistry),
            new Lazy<IToolExecutor>(() => new FakeToolExecutor()),
            new CSharpToolCompiler(NullLogger<CSharpToolCompiler>.Instance),
            new ToolHostFactory(NullLogger<ToolHost>.Instance),
            NullLogger<SelfImprovementManager>.Instance);
        manager2.LoadPersisted();
        Assert.Null(reloadedRegistry.GetByName("persisted_tool"));
        Assert.Contains(reloadedStore.LoadTools(), t => t.Name == "persisted_tool");

        reloadedStore.SafeMode = false;
        manager2.LoadPersisted();
        Assert.NotNull(reloadedRegistry.GetByName("persisted_tool"));
    }

    [Fact]
    public void Lessons_are_appended_trimmed_and_listed()
    {
        var (manager, _) = CreateManager(TempDir(), out var store);
        manager.AddLesson("  leçon importante sur les outils  ");
        manager.AddLesson(new string('x', 600));

        var lessons = manager.GetLessons();
        Assert.Equal(2, lessons.Count);
        Assert.DoesNotContain(lessons, l => l.StartsWith(' '));
        Assert.All(lessons, l => Assert.True(l.Length <= 530));
        Assert.NotEmpty(store.LoadLessons());
    }

    [Fact]
    public void ToolHost_blocks_file_access_outside_workspace()
    {
        var host = new ToolHostFactory(NullLogger<ToolHost>.Instance).Create();
        Assert.Throws<UnauthorizedAccessException>(() => host.ReadTextFile(@"C:\Windows\win.ini"));
        Assert.Throws<UnauthorizedAccessException>(() => host.WriteTextFile(@"C:\Windows\pwn.txt", "x"));
    }
}
