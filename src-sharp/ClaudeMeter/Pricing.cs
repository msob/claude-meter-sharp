using System.Globalization;

namespace ClaudeMeter;

public sealed record ModelPrice(string Name, double InputPerMtok, double OutputPerMtok)
{
    /// <summary>Cost of one million tokens at a given output share.</summary>
    public double BlendedPerMtok(double outputRatio = 0.25) =>
        InputPerMtok * (1 - outputRatio) + OutputPerMtok * outputRatio;
}

/// <summary>
/// Rough cost estimate. The API returns only utilization fractions against
/// opaque caps, so: utilization × approximate plan token cap × a blended price.
/// Always label the result "approx".
/// </summary>
public static class Pricing
{
    // Per-million-token public pricing, USD, May 2026.
    public static readonly Dictionary<string, ModelPrice> Prices = new()
    {
        ["opus-4.6"] = new("Claude Opus 4.6", 5.0, 25.0),
        ["sonnet-4.6"] = new("Claude Sonnet 4.6", 3.0, 15.0),
        ["haiku-4.5"] = new("Claude Haiku 4.5", 1.0, 5.0),
    };

    // Approximate rolling-window token caps by plan (community estimates, not
    // published by Anthropic): (5h, 7d, 7d Opus).
    public static readonly Dictionary<string, (long H5, long D7, long D7Opus)> PlanCaps = new()
    {
        ["pro"] = (225_000, 7_000_000, 0),
        ["max_5x"] = (1_125_000, 35_000_000, 5_000_000),
        ["max_20x"] = (4_500_000, 140_000_000, 20_000_000),
        ["api_only"] = (0, 0, 0),
        ["unknown"] = (1_125_000, 35_000_000, 5_000_000),  // assume Max 5x
    };

    public static double EstimateCostUsd(double utilization, string window, string plan = "unknown",
        string modelKey = "sonnet-4.6", double outputRatio = 0.25)
    {
        var caps = PlanCaps.GetValueOrDefault(plan, PlanCaps["unknown"]);
        double tokens;
        switch (window)
        {
            case "5h": tokens = utilization * caps.H5; break;
            case "7d": tokens = utilization * caps.D7; break;
            case "7d_opus": tokens = utilization * caps.D7Opus; modelKey = "opus-4.6"; break;
            default: return 0.0;
        }
        var price = Prices.GetValueOrDefault(modelKey, Prices["sonnet-4.6"]);
        return tokens / 1_000_000.0 * price.BlendedPerMtok(outputRatio);
    }

    public static string FormatUsd(double amount)
    {
        var fmt = amount >= 1000 ? "N0" : amount >= 10 ? "N1" : "N2";
        return "$" + amount.ToString(fmt, CultureInfo.InvariantCulture);
    }
}
