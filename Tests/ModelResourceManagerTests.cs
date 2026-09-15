using JarvisAI.Infrastructure.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public sealed class ModelResourceManagerTests
{
    private static ModelResourceManager CreateManager() =>
        new(new VramCatalogService(), NullLogger<ModelResourceManager>.Instance);

    [Fact]
    public void PeutCharger_RentreQuandRamDispoSuffisante()
    {
        var manager = CreateManager();
        var budget = new ResourceBudget(
            RamTotalGo: 32, RamUtiliseeGo: 10,
            VramTotalGo: 8, VramUtiliseeGo: 1);

        Assert.True(manager.PeutCharger(new ModelCharge("llama", RamGo: 8, VramGo: 4, DateTime.UtcNow), budget));
        Assert.True(manager.PeutCharger(new ModelCharge("llama", RamGo: 2, VramGo: 0, DateTime.UtcNow), budget));
    }

    [Fact]
    public void PeutCharger_RefuseQuandVramDepassee()
    {
        var manager = CreateManager();
        var budget = new ResourceBudget(
            RamTotalGo: 32, RamUtiliseeGo: 10,
            VramTotalGo: 8, VramUtiliseeGo: 7);

        Assert.False(manager.PeutCharger(new ModelCharge("llama 70B", RamGo: 4, VramGo: 42, DateTime.UtcNow), budget));
    }

    [Fact]
    public void PeutCharger_SansGpuSeuleRamContraint()
    {
        var manager = CreateManager();
        var budget = new ResourceBudget(
            RamTotalGo: 16, RamUtiliseeGo: 12,
            VramTotalGo: 0, VramUtiliseeGo: 0);

        Assert.True(manager.PeutCharger(new ModelCharge("petit", RamGo: 1, VramGo: 0, DateTime.UtcNow), budget));
        Assert.False(manager.PeutCharger(new ModelCharge("gros", RamGo: 40, VramGo: 0, DateTime.UtcNow), budget));
    }

    [Fact]
    public void ChoisirDefchargement_NeRendRienSiCibleRentre()
    {
        var manager = CreateManager();
        var budget = new ResourceBudget(
            RamTotalGo: 32, RamUtiliseeGo: 10,
            VramTotalGo: 8, VramUtiliseeGo: 1);

        var charges = new[] { new ModelCharge("qwen", RamGo: 1, VramGo: 1, DateTime.UtcNow) };
        Assert.Empty(manager.ChoisirDefchargement(
            new ModelCharge("llama", RamGo: 2, VramGo: 2, DateTime.UtcNow), budget, charges));
    }

    [Fact]
    public void ChoisirDefchargement_DechargeLeMoinsUtiliseDabord()
    {
        var manager = CreateManager();
        var budget = new ResourceBudget(
            RamTotalGo: 32, RamUtiliseeGo: 30,
            VramTotalGo: 8, VramUtiliseeGo: 6);

        var ancien = DateTime.UtcNow.AddHours(-3);
        var recent = DateTime.UtcNow;
        var charges = new[]
        {
            new ModelCharge("gros-recent", RamGo: 12, VramGo: 5, recent),
            new ModelCharge("petit-ancien", RamGo: 6, VramGo: 2, ancien)
        };

        var aDecharger = manager.ChoisirDefchargement(
            new ModelCharge("nouveau", RamGo: 8, VramGo: 4, DateTime.UtcNow), budget, charges);

        Assert.Contains("petit-ancien", aDecharger);
        Assert.Equal("petit-ancien", aDecharger[0]);
    }

    [Fact]
    public void ChoisirDefchargement_RefuseImpossibleSiTropGros()
    {
        var manager = CreateManager();
        var budget = new ResourceBudget(
            RamTotalGo: 8, RamUtiliseeGo: 7,
            VramTotalGo: 4, VramUtiliseeGo: 3);

        var charges = new[] { new ModelCharge("m", RamGo: 3, VramGo: 2, DateTime.UtcNow) };

        var aDecharger = manager.ChoisirDefchargement(
            new ModelCharge("70B", RamGo: 99, VramGo: 99, DateTime.UtcNow), budget, charges);

        // Même tout décharger ne suffit pas : le gestionnaire renvoie quand même
        // la liste complète (LRU) pour que l'appelant décide de ne pas charger.
        Assert.NotEmpty(aDecharger);
    }
}