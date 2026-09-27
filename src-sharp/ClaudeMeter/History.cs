using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeMeter;

public sealed class Bucket
{
    [JsonPropertyName("t")] public string T { get; set; } = "";   // ISO 8601 UTC, "…Z"
    [JsonPropertyName("u5h")] public double? U5h { get; set; }
    [JsonPropertyName("u7d")] public double? U7d { get; set; }
    [JsonPropertyName("uopus")] public double? Uopus { get; set; }
}

/// <summary>
/// Usage history for the sparkline: <c>%APPDATA%\ClaudeMeter\history.json</c>,
/// a list of 10-minute buckets trimmed to the last 14 days.
/// </summary>
public static class History
{
    const int BucketMinutes = 10;
    const int HistoryDays = 14;

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeMeter", "history.json");

    /// <summary>Tolerant per item: one bad entry never costs the rest of the history.</summary>
    public static List<Bucket> Load(string? path = null)
    {
        var buckets = new List<Bucket>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path ?? DefaultPath));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return buckets;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("t", out var t)) continue;
                buckets.Add(new Bucket
                {
                    T = t.ValueKind == JsonValueKind.String ? t.GetString()! : t.GetRawText(),
                    U5h = Num(item, "u5h"),
                    U7d = Num(item, "u7d"),
                    Uopus = Num(item, "uopus"),
                });
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return buckets;
    }

    /// <summary>Add the current reading, collapsing into the open 10-minute bucket (highest reading wins).</summary>
    public static List<Bucket> Append(double? u5h, double? u7d, double? uopus, string? path = null, DateTimeOffset? now = null)
    {
        var utcNow = (now ?? DateTimeOffset.UtcNow).ToUniversalTime();
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var start = new DateTimeOffset(utcNow.Year, utcNow.Month, utcNow.Day, utcNow.Hour,
            utcNow.Minute / BucketMinutes * BucketMinutes, 0, TimeSpan.Zero);
        var key = Iso(start);

        var buckets = Load(path);
        if (buckets.Count > 0 && buckets[^1].T == key)
        {
            var last = buckets[^1];
            last.U5h = MaxOpt(last.U5h, u5h);
            last.U7d = MaxOpt(last.U7d, u7d);
            last.Uopus = MaxOpt(last.Uopus, uopus);
        }
        else
        {
            buckets.Add(new Bucket { T = key, U5h = u5h, U7d = u7d, Uopus = uopus });
        }

        var cutoff = Iso(utcNow.AddDays(-HistoryDays));
        buckets = buckets.Where(b => string.CompareOrdinal(b.T, cutoff) >= 0).ToList();
        File.WriteAllText(path, JsonSerializer.Serialize(buckets));
        return buckets;
    }

    /// <summary>The last <paramref name="points"/> non-null values of one field.</summary>
    public static List<double> SparklineSeries(IEnumerable<Bucket> buckets, Func<Bucket, double?> field, int points = 96)
    {
        var values = buckets.Select(field).OfType<double>().ToList();
        return values.Skip(Math.Max(0, values.Count - points)).ToList();
    }

    static string Iso(DateTimeOffset t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    static double? Num(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    static double? MaxOpt(double? a, double? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
}
