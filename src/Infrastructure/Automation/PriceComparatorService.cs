using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Automation;

public interface IPriceComparatorService
{
    Task<PriceComparisonResult> CompareProductAsync(string productName, CancellationToken ct = default);
    Task<List<PriceOffer>> SearchMultipleSitesAsync(string query, IEnumerable<string> sites, CancellationToken ct = default);
}

public sealed class PriceComparatorService : IPriceComparatorService
{
    private readonly ILogger<PriceComparatorService> _logger;
    private readonly HttpClient _httpClient;

    public PriceComparatorService(ILogger<PriceComparatorService> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
    }

    public async Task<PriceComparisonResult> CompareProductAsync(string productName, CancellationToken ct = default)
    {
        var result = new PriceComparisonResult { ProductName = productName };
        var sites = new[] { "amazon.fr", "fnac.com", "cdiscount.com", "ldlc.com", "rueducommerce.fr" };

        foreach (var site in sites)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var offers = await SearchSiteAsync(productName, site, ct);
                result.Offers.AddRange(offers);
            }
            catch { }
        }

        result.Offers = result.Offers.OrderBy(o => o.Price).ToList();
        result.BestOffer = result.Offers.FirstOrDefault();
        result.Success = true;

        _logger.LogInformation("[PriceCompare] Found {Count} offers for {Product}", result.Offers.Count, productName);
        return result;
    }

    public async Task<List<PriceOffer>> SearchMultipleSitesAsync(string query, IEnumerable<string> sites, CancellationToken ct = default)
    {
        var offers = new List<PriceOffer>();
        foreach (var site in sites)
        {
            if (ct.IsCancellationRequested) break;
            offers.AddRange(await SearchSiteAsync(query, site, ct));
        }
        return offers;
    }

    private async Task<List<PriceOffer>> SearchSiteAsync(string query, string site, CancellationToken ct)
    {
        var offers = new List<PriceOffer>();
        try
        {
            var url = $"https://www.{site}/s?k={Uri.EscapeDataString(query)}";
            var html = await _httpClient.GetStringAsync(url, ct);

            // Extract price patterns
            var priceMatches = System.Text.RegularExpressions.Regex.Matches(html, @"(\d+[.,]\d{2})\s*€");
            foreach (System.Text.RegularExpressions.Match m in priceMatches)
            {
                if (decimal.TryParse(m.Groups[1].Value.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var price))
                {
                    offers.Add(new PriceOffer
                    {
                        Site = site,
                        ProductName = query,
                        Price = price,
                        Currency = "EUR",
                        Url = url
                    });
                }
            }
        }
        catch { }
        return offers.Take(5).ToList();
    }
}

public sealed class PriceComparisonResult
{
    public bool Success { get; set; }
    public string ProductName { get; set; } = "";
    public List<PriceOffer> Offers { get; set; } = new();
    public PriceOffer? BestOffer { get; set; }
}

public sealed class PriceOffer
{
    public string Site { get; set; } = "";
    public string ProductName { get; set; } = "";
    public decimal Price { get; set; }
    public string Currency { get; set; } = "EUR";
    public string Url { get; set; } = "";
    public DateTime FoundAt { get; set; } = DateTime.UtcNow;
}
