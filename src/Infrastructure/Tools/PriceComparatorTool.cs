using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Automation;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

public sealed class PriceComparatorTool : ToolBase
{
    private readonly IPriceComparatorService _service;
    private readonly ILogger<PriceComparatorTool> _logger;

    public override string Name => "price_comparator";
    public override string Description => "Comparer les prix d'un produit sur plusieurs sites. Usage: price_comparator(product: \"iPhone 15\")";
    public override string Category => "web";
    public override SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;
    public override TimeSpan Timeout => TimeSpan.FromSeconds(90);

    public override IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("product", "Nom du produit à rechercher", typeof(string), required: true)
    };

    public PriceComparatorTool(IPriceComparatorService service, ILogger<PriceComparatorTool> logger)
        : base(logger)
    {
        _service = service;
        _logger = logger;
    }

    protected override async Task<ToolResult> ExecuteCoreAsync(
        AgentContext context,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken ct)
    {
        var product = RequireParam(parameters, "product");

        var result = await _service.CompareProductAsync(product, ct);
        if (!result.Success)
            return Fail("Échec de la comparaison de prix.");

        if (result.Offers.Count == 0)
            return Ok($"Aucune offre trouvée pour « {product} ».");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Résultats pour « {product} » ({result.Offers.Count} offres) :");
        sb.AppendLine();
        foreach (var offer in result.Offers)
        {
            sb.AppendLine($"  {offer.Price:F2} € — {offer.Site} — {offer.Url}");
        }
        if (result.BestOffer is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"Meilleur prix : {result.BestOffer.Price:F2} € sur {result.BestOffer.Site}");
        }

        return Ok(sb.ToString());
    }
}
