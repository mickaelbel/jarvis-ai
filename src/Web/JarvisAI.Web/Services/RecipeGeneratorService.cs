using Microsoft.Extensions.Logging;
using System.Text;

namespace JarvisAI.Web.Services;

public interface IRecipeGeneratorService
{
    IReadOnlyList<CodeRecipe> GetRecipes(string? category = null);
    CodeRecipe? GetRecipe(string recipeId);
    string GenerateRecipe(string template, string language, Dictionary<string, string>? parameters = null);
    IReadOnlyList<string> GetCategories();
}

public sealed class RecipeGeneratorService : IRecipeGeneratorService
{
    private readonly ILogger<RecipeGeneratorService> _logger;
    private readonly List<CodeRecipe> _recipes = new();

    public RecipeGeneratorService(ILogger<RecipeGeneratorService> logger)
    {
        _logger = logger;
        InitializeRecipes();
    }

    public IReadOnlyList<CodeRecipe> GetRecipes(string? category = null)
    {
        if (category is null) return _recipes;
        return _recipes.Where(r => r.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public CodeRecipe? GetRecipe(string recipeId)
        => _recipes.FirstOrDefault(r => r.Id == recipeId);

    public string GenerateRecipe(string template, string language, Dictionary<string, string>? parameters = null)
    {
        var recipe = _recipes.FirstOrDefault(r => r.Name.Equals(template, StringComparison.OrdinalIgnoreCase));
        if (recipe is null) return $"Recette non trouvée: {template}";

        var code = recipe.CodeTemplate;
        if (parameters is not null)
        {
            foreach (var kv in parameters)
            {
                code = code.Replace($"{{{{{kv.Key}}}}}", kv.Value);
            }
        }

        return code;
    }

    public IReadOnlyList<string> GetCategories()
        => _recipes.Select(r => r.Category).Distinct().OrderBy(c => c).ToList();

    private void InitializeRecipes()
    {
        _recipes.AddRange(new[]
        {
            new CodeRecipe
            {
                Id = "crud-api",
                Name = "CRUD API",
                Description = "API REST CRUD complète avec ASP.NET Core",
                Category = "API",
                Language = "C#",
                CodeTemplate = @"using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route(""api/[controller]"")]
public class {{EntityName}}Controller : ControllerBase
{
    private readonly IService<{{EntityName}}> _service;

    public {{EntityName}}Controller(IService<{{EntityName}}> service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<{{EntityName}}>>> GetAll()
    {
        return Ok(await _service.GetAllAsync());
    }

    [HttpGet(""{id}"")]
    public async Task<ActionResult<{{EntityName}}>> GetById(int id)
    {
        var item = await _service.GetByIdAsync(id);
        return item is null ? NotFound() : Ok(item);
    }

    [HttpPost]
    public async Task<ActionResult<{{EntityName}}>> Create([FromBody] {{EntityName}} item)
    {
        var created = await _service.CreateAsync(item);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut(""{id}"")]
    public async Task<IActionResult> Update(int id, [FromBody] {{EntityName}} item)
    {
        await _service.UpdateAsync(id, item);
        return NoContent();
    }

    [HttpDelete(""{id}"")]
    public async Task<IActionResult> Delete(int id)
    {
        await _service.DeleteAsync(id);
        return NoContent();
    }
}",
                Tags = new() { "API", "REST", "CRUD", "Controller" }
            },
            new CodeRecipe
            {
                Id = "di-service",
                Name = "Service avec DI",
                Description = "Service avec injection de dépendances",
                Category = "Architecture",
                Language = "C#",
                CodeTemplate = @"public interface I{{ServiceName}}Service
{
    Task<{{ReturnType}}> ProcessAsync(string input);
}

public class {{ServiceName}}Service : I{{ServiceName}}Service
{
    private readonly ILogger<{{ServiceName}}Service> _logger;

    public {{ServiceName}}Service(ILogger<{{ServiceName}}Service> logger)
    {
        _logger = logger;
    }

    public async Task<{{ReturnType}}> ProcessAsync(string input)
    {
        _logger.LogInformation(""Processing: {Input}"", input);
        // TODO: Implémenter la logique
        return await Task.FromResult(default({{ReturnType}}));
    }
}

// Registration
services.AddSingleton<I{{ServiceName}}Service, {{ServiceName}}Service>();",
                Tags = new() { "DI", "Service", "Pattern" }
            },
            new CodeRecipe
            {
                Id = "middleware",
                Name = "Middleware",
                Description = "Middleware ASP.NET Core personnalisé",
                Category = "API",
                Language = "C#",
                CodeTemplate = @"public class {{MiddlewareName}}Middleware
{
    private readonly RequestDelegate _next;

    public {{MiddlewareName}}Middleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Before
        _logger.LogInformation(""Before: {Path}"", context.Request.Path);

        await _next(context);

        // After
        _logger.LogInformation(""After: {StatusCode}"", context.Response.StatusCode);
    }
}

public static class {{MiddlewareName}}Extensions
{
    public static IApplicationBuilder Use{{MiddlewareName}}(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<{{MiddlewareName}}Middleware>();
    }
}",
                Tags = new() { "Middleware", "HTTP", "Pipeline" }
            },
            new CodeRecipe
            {
                Id = "unit-test",
                Name = "Unit Test",
                Description = "Test unitaire avec xUnit et Moq",
                Category = "Testing",
                Language = "C#",
                CodeTemplate = @"using Xunit;
using Moq;

public class {{ClassName}}Tests
{
    private readonly Mock<IService> _mockService;
    private readonly {{ClassName}} _sut;

    public {{ClassName}}Tests()
    {
        _mockService = new Mock<IService>();
        _sut = new {{ClassName}}(_mockService.Object);
    }

    [Fact]
    public void {{MethodName}}_ShouldReturnExpected_WhenValidInput()
    {
        // Arrange
        var input = ""test"";
        _mockService.Setup(x => x.Process(It.IsAny<string>()))
                   .Returns(""expected"");

        // Act
        var result = _sut.{{MethodName}}(input);

        // Assert
        Assert.Equal(""expected"", result);
        _mockService.Verify(x => x.Process(It.IsAny<string>()), Times.Once);
    }
}",
                Tags = new() { "Test", "xUnit", "Moq", "Unit" }
            },
            new CodeRecipe
            {
                Id = "background-service",
                Name = "Background Service",
                Description = "Service d'arrière-plan avec IHostedService",
                Category = "Architecture",
                Language = "C#",
                CodeTemplate = @"public class {{ServiceName}}BackgroundService : BackgroundService
{
    private readonly ILogger<{{ServiceName}}BackgroundService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public {{ServiceName}}BackgroundService(
        ILogger<{{ServiceName}}BackgroundService> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                // Do work here

                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ""Error in background service"");
            }
        }
    }
}

// Registration
services.AddHostedService<{{ServiceName}}BackgroundService>();",
                Tags = new() { "Background", "HostedService", "Worker" }
            }
        });
    }
}

public sealed class CodeRecipe
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public string Language { get; set; } = "";
    public string CodeTemplate { get; set; } = "";
    public List<string> Tags { get; set; } = new();
}
