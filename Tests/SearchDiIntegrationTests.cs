using JarvisAI.Application.Search;
using JarvisAI.Application.Tools;
using JarvisAI.Infrastructure;
using JarvisAI.Infrastructure.Search;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public sealed class SearchDiIntegrationTests
{
    [Fact]
    public void AddWebSearch_resolves_service_providers_and_tool()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWebSearch();

        using var provider = services.BuildServiceProvider();

        var search = provider.GetRequiredService<IWebSearchService>();
        Assert.NotNull(search);

        var providers = provider.GetServices<IWebSearchProvider>().ToList();
        Assert.Equal(13, providers.Count);
        Assert.Contains(providers, p => p.Name == "duckduckgo");
        Assert.Contains(providers, p => p.Name == "bing");
        Assert.Contains(providers, p => p.Name == "wikipedia");
        Assert.Contains(providers, p => p.Name == "github");
        Assert.Contains(providers, p => p.Name == "reddit");
        Assert.Contains(providers, p => p.Name == "news");
        Assert.Contains(providers, p => p.Name == "arxiv");
        Assert.Contains(providers, p => p.Name == "semantic_scholar");
        Assert.Contains(providers, p => p.Name == "hal");
        Assert.Contains(providers, p => p.Name == "pubmed");
        Assert.Contains(providers, p => p.Name == "crossref");
        Assert.Contains(providers, p => p.Name == "stackoverflow");
        Assert.Contains(providers, p => p.Name == "nominatim");

        Assert.NotNull(provider.GetRequiredService<ISearchCache>());
        Assert.NotNull(provider.GetRequiredService<ILinkVerifier>());
    }

    [Fact]
    public void WebSearchTool_resolves_via_di_with_optional_open_action()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWebSearch();

        using var provider = services.BuildServiceProvider();

        var tool = provider.GetServices<ITool>().SingleOrDefault(t => t.Name == "web_search");
        Assert.NotNull(tool);
        var searchTool = Assert.IsType<WebSearchTool>(tool);
        Assert.Equal("web", searchTool.Category);
        Assert.NotEmpty(searchTool.Parameters);
    }

    [Fact]
    public void AddInfrastructure_registers_web_search_tool_and_registry()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure();

        // Le tool web_search est inscrit via une FABRIQUE dédiée (constructeur
        // stable à 3 paramètres) et non via une inscription par type : c'est ce
        // qui supprime l'ambiguïté de constructeur en DI. Si une inscription par
        // type était réintroduite, ce test échoue.
        var byType = services
            .Where(d => d.ServiceType == typeof(ITool) && d.ImplementationType == typeof(WebSearchTool))
            .ToList();
        Assert.Empty(byType);

        var factories = services
            .Where(d => d.ServiceType == typeof(ITool) && d.ImplementationFactory is not null)
            .ToList();
        // web_search + méta-outils d'auto-amélioration (create_tool, add_lesson)
        // sont inscrits via des fabriques dédiées — aucun ITool « cœur » par type.
        Assert.True(factories.Count >= 1);
        Assert.Contains(factories, f => f.ImplementationFactory is not null);

        var registryDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IToolRegistry));
        Assert.NotNull(registryDescriptor);
    }

    [Fact]
    public void Registry_populated_from_services_contains_web_search()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWebSearch();

        using var provider = services.BuildServiceProvider();
        var registry = new ToolRegistry(NullLoggerFactory.Instance.CreateLogger<ToolRegistry>());
        foreach (var registered in provider.GetServices<ITool>())
            registry.Register(registered);

        var tool = registry.GetByName("web_search");
        Assert.NotNull(tool);
        Assert.IsType<WebSearchTool>(tool);
        Assert.Contains(registry.GetByCategory("web"), t => t.Name == "web_search");
    }
}
