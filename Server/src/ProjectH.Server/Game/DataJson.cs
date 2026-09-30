using System;
using System.Text.Json;

namespace ProjectH.Server.Game;

// Shared by the data file loaders (weapons.json, items.json, loot.json). Startup only.
internal static class DataJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // Seconds -> whole ticks at simHz, at least 1: 1.25 s at 30 Hz = 37.5 -> 38.
    public static bool TryTicks(double seconds, int simHz, out ushort ticks)
    {
        ticks = 0;
        if (!double.IsFinite(seconds) || seconds <= 0) return false;
        double value = Math.Round(seconds * simHz, MidpointRounding.AwayFromZero);
        if (value > ushort.MaxValue) return false;
        ticks = (ushort)Math.Max(1, value);
        return true;
    }

    // Deserialize without throwing past the loader: a JsonException becomes an error string.
    public static bool TryDeserialize<T>(string json, out T? value, out string? error) where T : class
    {
        try
        {
            value = JsonSerializer.Deserialize<T>(json, Options);
            error = value == null ? "the file is empty or null." : null;
            return value != null;
        }
        catch (JsonException ex)
        {
            value = null;
            error = "invalid JSON: " + ex.Message;
            return false;
        }
    }
}
