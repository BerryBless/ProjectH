using System.Numerics;
using System.Text.Json;
using ProjectH.Shared.Simulation;

namespace ProjectH.QA;

// A position a step names: x and z always; y absent = the terrain height there (the server's setPosition rule);
// yaw optional (a marker's facing).
public readonly record struct QaPosition(float X, float? Y, float Z, float? Yaw)
{
    // 기능: 위치를 Vector3로 바꾼다. y가 없으면 그 지점의 지형 높이를 쓴다.
    // 입력: 없음.
    // 출력: 지면에 놓인 좌표.
    public Vector3 ToGround() => new(X, Y ?? GameMap.Terrain.Height(X, Z), Z);

    // 기능: 위치를 소수 2자리 표기로 만든다.
    // 입력: 없음.
    // 출력: "(x, y, z)" 문자열. y가 없으면 "~"로 표시.
    public override string ToString() => Y == null ? $"({X:0.##}, ~, {Z:0.##})" : $"({X:0.##}, {Y:0.##}, {Z:0.##})";
}

// Named positions (request §43-44): QA markers from QA/Markers.json, then the map's POI names (MapPois). Loaded once
// per tool run; read-only afterwards.
public sealed class MarkerStore
{
    public const int MaxMarkers = 1000;

    private readonly Dictionary<string, QaPosition> _markers = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, QaPosition> Markers => _markers;

    // 기능: 마커가 없는 저장소를 만든다(POI 이름은 여전히 풀린다).
    // 입력: 없음.
    // 출력: 빈 MarkerStore.
    public static MarkerStore Empty() => new();

    // 기능: QA/Markers.json을 읽어 마커 저장소를 만든다.
    // 입력: path - 마커 파일 경로.
    // 출력: 파일이 없으면 빈 저장소, 있으면 파싱된 저장소. 형식이 틀리면 FormatException, JSON이 깨졌으면 JsonException.
    public static MarkerStore LoadFile(string path)
    {
        if (!File.Exists(path)) return new MarkerStore();
        return Parse(File.ReadAllText(path));
    }

    // 기능: 마커 JSON 텍스트를 파싱해 이름(대소문자 무시)별 위치를 담는다.
    // 입력: json - { "markers": [ { name, x, y?, z, yaw? } ] } 텍스트.
    // 출력: 마커 저장소. markers가 배열이 아니거나 이름·x·z가 없거나 MaxMarkers를 넘으면 FormatException, JSON이 깨졌으면 JsonException.
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

    // 기능: 이름을 QA 마커, 그다음 맵 POI 이름에서 찾는다(대소문자 무시).
    // 입력: name - 위치 이름, position - 찾은 위치를 받을 변수.
    // 출력: 찾았으면 true와 위치(POI는 y·yaw 없음), 없으면 false.
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

    // 기능: 단계의 position 값을 위치로 푼다: 문자열이면 마커/POI 이름, 객체면 { x, y?, z, yaw? }.
    // 입력: value - 단계의 position JSON 값, position - 푼 위치를 받을 변수, error - 실패 이유를 받을 문자열.
    // 출력: 성공하면 true와 위치, 실패하면 false와 error.
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

    // 기능: { x, y?, z, yaw? } 객체에서 위치를 읽는다.
    // 입력: value - JSON 객체, position - 읽은 위치를 받을 변수, error - 실패 이유를 받을 문자열.
    // 출력: x·z가 숫자면 true와 위치, 아니면 false와 error.
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

    // 기능: 객체의 숫자 필드를 float로 읽는다.
    // 입력: value - JSON 객체, name - 필드 이름(대소문자 무시).
    // 출력: 유한한 숫자면 그 값, 없거나 숫자가 아니거나 무한·NaN이면 null.
    private static float? Number(JsonElement value, string name)
    {
        JsonElement? e = JsonPath.Child(value, name);
        if (e == null || e.Value.ValueKind != JsonValueKind.Number) return null;
        double d = e.Value.GetDouble();
        return double.IsFinite(d) ? (float)d : null;
    }
}
