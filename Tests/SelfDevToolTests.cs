using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JarvisAI.Tests;

/// <summary>
/// Vérifie l'outil autodev dans le vrai conteneur DI d'AddInfrastructure
/// (sans serveur HTTP) : la preuve que Jarvis peut se construire et se tester
/// lui-même. Ces tests peuvent tourner pendant que l'application est lancée.
/// </summary>
public class SelfDevToolTests
{
    private readonly IServiceProvider _sp;

    public SelfDevToolTests()
    {
        // Conteneur minimal dédié à autodev (pas AddInfrastructure complet :
        // certains outils exigent des services propres à l'hôte web/desktop).
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructureDevOnly();
        _sp = services.BuildServiceProvider();
    }

    private static async Task<ToolResult> CallAsync(IServiceProvider sp, string action, params (string Key, string Value)[] args)
    {
        var registry = sp.GetRequiredService<IToolRegistry>();
        var tool = registry.GetByName("autodev");
        Assert.NotNull(tool);
        var dict = new Dictionary<string, string> { ["action"] = action };
        foreach (var (k, v) in args) dict[k] = v;
        return await tool.ExecuteAsync(new AgentContext("[Test] autodev", "test"), dict);
    }

    [Fact]
    public async Task Autodev_Enregistre_Et_Status_Fonctionne()
    {
        var result = await CallAsync(_sp, "status");
        Assert.True(result.Success);
        Assert.Contains("Dépôt", result.Output);
    }

    [Fact]
    public async Task Autodev_Test_AvecFiltre_Rapide_Vert()
    {
        // Filtre minuscule : les 9 tests du parseur tournent en ~10 ms
        var result = await CallAsync(_sp, "test", ("filtre", "FullyQualifiedName~DotnetOutputParserTests"));
        Assert.True(result.Success);
        Assert.Contains("verts", result.Output);
    }

    [Fact]
    public async Task Autodev_Boucle_Status_Config()
    {
        var r1 = await CallAsync(_sp, "boucle");
        Assert.True(r1.Success);

        var r2 = await CallAsync(_sp, "boucle", ("valeur", "off"), ("autofix", "on"));
        Assert.True(r2.Success);
        Assert.Contains("OFF", r2.Output);

        // Remet une config saine pour ne pas polluer selfdev.json du poste
        var r3 = await CallAsync(_sp, "boucle", ("valeur", "720"), ("autofix", "on"), ("autopub", "off"));
        Assert.True(r3.Success);
    }

    [Fact]
    public async Task Autodev_Logs_Web_Lisible()
    {
        var result = await CallAsync(_sp, "logs", ("source", "web"), ("lignes", "10"));
        // Le fichier peut être absent sur une machine vierge : on accepte les deux,
        // mais l'outil ne doit jamais lever d'exception.
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Autodev_ActionInconnue_Erreur_Propre()
    {
        var result = await CallAsync(_sp, "action_bidon");
        Assert.False(result.Success);
    }
}