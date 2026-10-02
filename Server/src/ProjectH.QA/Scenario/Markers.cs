using System.Numerics;
using System.Text.Json;
using ProjectH.Shared.Simulation;

namespace ProjectH.QA;

// A position a step names: x and z always; y absent = the terrain height there (the server's setPosition rule);
// yaw optional (a marker's facing).
public readonly record struct QaPosition(float X, float? Y, float Z, float? Yaw)
{
    public Vector3 ToGround() => new(X, Y ?? GameMap.Terrain.Height(X, Z), Z);

    public override string ToString() => Y == null ? $"({X:0.##}, ~, {Z:0.##})" : $"({X:0.##}, {Y:0.##}, {Z:0.##})";
}

// Named positions (request §43-44): QA markers from QA/Markers.json, then the map's POI names (MapPois). Loaded once
// per tool run; read-only afterwards.
public sealed class MarkerStore
{
    public const int MaxMarkers = 1000;

    private readonly Dictionary<string, QaPosition> _markers = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, QaPosition> Markers => _markers;

    public static MarkerStore Empty() => new();

    public static MarkerStore LoadFile(string path)
    {
        if (!File.Exists(path)) return new MarkerStore();
        return Parse(File.ReadAllText(path));
    }

    // { "markers": [ { "name": "QA_Combat_A", "x": -5, "y": 0, "z": 0, "yaw": 90 } ] }. Throws on a malformed file:
    // a broken marker file is a tool error, not something to run around.
    public static MarkerStore Parse(string json)
    {
        var store = new MarkerStore();
        using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (!doc.RootElement.TryGetProperty("markers", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
            throw new FormatException("Markers file: 'markers' must be an array.");
        foreach (JsonElement m in list.EnumerateArray())
        {
            if (store._markers.Count >= MaxMarkers) throw new FormatException($"Markers file: more than {MaxMarkers} markers.");
            string? name = m.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            if (string.IsNullOrEmpty(name)) throw new FormatException("Markers file: every marker needs a 'name'.");
            if (!TryReadObject(m, out QaPosition position, out string? error)) throw new FormatException($"Marker {name}: {error}");
            store._markers[name] = position;
        }
        return store;
    }

    public bool TryResolveName(string name, out QaPosition position)
    {
        if (_markers.TryGetValue(name, out position)) return true;
        foreach (MapPoi poi in MapPois.All)
        {
            if (!string.Equals(poi.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            position = new QaPosition(poi.X, null, poi.Z, null);
            return true;
        }
        position = default;
        return false;
    }

    // A step's position value: a marker/POI name or { x, y?, z, yaw? }.
    public bool TryResolve(JsonElement value, out QaPosition position, out string? error)
    {
        error = null;
        if (value.ValueKind == JsonValueKind.String)
        {
            string name = value.GetString()!;
            if (TryResolveName(name, out position)) return true;
            error = $"Unknown position name '{name}' (not a QA marker or a map POI).";
            return false;
        }
        if (value.ValueKind == JsonValueKind.Object) return TryReadObject(value, out position, out error);
        position = default;
        error = "A position is a marker name or an object { x, y?, z }.";
        return false;
    }

    private static bool TryReadObject(JsonElement value, out QaPosition position, out string? error)
    {
        position = default;
        error = null;
        float? x = Number(value, "x"), y = Number(value, "y"), z = Number(value, "z"), yaw = Number(value, "yaw");
        if (x == null || z == null)
        {
            error = "A position needs numeric x and z.";
            return false;
        }
        position = new QaPosition(x.Value, y, z.Value, yaw);
        return true;
    }

    private static float? Number(JsonElement value, string name)
    {
        JsonElement? e = JsonPath.Child(value, name);
        if (e == null || e.Value.ValueKind != JsonValueKind.Number) return null;
        double d = e.Value.GetDouble();
        return double.IsFinite(d) ? (float)d : null;
    }
}
