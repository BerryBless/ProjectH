using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProjectH.QA;

// `${name}` substitution (design doc: variables + saveAs values). A string that is exactly one `${...}` becomes that
// value with its JSON type (so `"lessThan": "${hpBefore}"` compares numbers); inside a longer string the value is
// written as text. `${name.field}` reads a field of an object value (a saved command result). Names are checked before
// the run (ScenarioValidator); a name still missing at run time (its saveAs step failed) is a step failure.
public static partial class Variables
{
    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_\-]*(?:\.[A-Za-z0-9_\-]+)*)\}")]
    private static partial Regex Reference();

    [GeneratedRegex(@"^\$\{([A-Za-z_][A-Za-z0-9_\-]*(?:\.[A-Za-z0-9_\-]+)*)\}$")]
    private static partial Regex WholeReference();

    // 기능: 값 안의 문자열(객체·배열은 재귀)에 적힌 `${name...}` 참조의 루트 이름을 모두 찾는다.
    // 입력: value - 검사할 JSON 값.
    // 출력: 참조된 변수 루트 이름의 지연 열거(중복 포함).
    // Every variable root name referenced anywhere inside the value (for validation).
    public static IEnumerable<string> ReferencedRoots(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                foreach (Match m in Reference().Matches(value.GetString()!)) yield return m.Groups[1].Value.Split('.')[0];
                break;
            case JsonValueKind.Object:
                foreach (JsonProperty p in value.EnumerateObject())
                    foreach (string r in ReferencedRoots(p.Value)) yield return r;
                break;
            case JsonValueKind.Array:
                foreach (JsonElement e in value.EnumerateArray())
                    foreach (string r in ReferencedRoots(e)) yield return r;
                break;
        }
    }

    // 기능: 문자열에 적힌 `${name...}` 참조의 루트 이름을 모두 찾는다.
    // 입력: text - 검사할 문자열.
    // 출력: 참조된 변수 루트 이름의 지연 열거(중복 포함).
    public static IEnumerable<string> ReferencedRoots(string text)
    {
        foreach (Match m in Reference().Matches(text)) yield return m.Groups[1].Value.Split('.')[0];
    }

    // 기능: 값에 `${` 참조가 있을 수 있는지 빠르게 확인한다.
    // 입력: value - 검사할 JSON 값.
    // 출력: 문자열·객체·배열 안에 "${"가 있으면 true, 그 외 종류는 false.
    public static bool HasReference(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!.Contains("${", StringComparison.Ordinal),
        JsonValueKind.Object or JsonValueKind.Array => value.GetRawText().Contains("${", StringComparison.Ordinal),
        _ => false,
    };

    // 기능: 값 안의 `${...}` 참조를 변수 값으로 바꾼다. 문자열 전체가 참조 하나면 그 값을 JSON 타입 그대로, 긴 문자열 안이면 텍스트로 넣고, 객체·배열은 다시 써서 만든다.
    // 입력: value - 치환할 JSON 값, variables - 변수 사전.
    // 출력: 치환된 값(참조가 없으면 원본 그대로). 모르는 변수·필드면 QaStepException.
    public static JsonElement Substitute(JsonElement value, IReadOnlyDictionary<string, JsonElement> variables)
    {
        if (!HasReference(value)) return value;
        if (value.ValueKind == JsonValueKind.String)
        {
            string text = value.GetString()!;
            Match whole = WholeReference().Match(text);
            if (whole.Success) return Lookup(whole.Groups[1].Value, variables);
            return JsonSerializer.SerializeToElement(SubstituteText(text, variables));
        }
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, value, variables);
        }
        using JsonDocument doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }

    // 기능: 문자열 안의 모든 `${...}` 참조를 변수 값의 텍스트(문자열은 그대로, 그 외는 원문 JSON)로 바꾼다.
    // 입력: text - 치환할 문자열, variables - 변수 사전.
    // 출력: 치환된 문자열. 모르는 변수·필드면 QaStepException.
    public static string SubstituteText(string text, IReadOnlyDictionary<string, JsonElement> variables) =>
        Reference().Replace(text, m =>
        {
            JsonElement v = Lookup(m.Groups[1].Value, variables);
            return v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText();
        });

    // 기능: JSON 값을 writer에 다시 쓰되 문자열은 치환해서 쓴다(객체·배열은 재귀).
    // 입력: writer - 출력 writer, value - 쓸 JSON 값, variables - 변수 사전.
    // 출력: 반환값 없음. writer에 치환된 값이 기록된다.
    private static void Write(Utf8JsonWriter writer, JsonElement value, IReadOnlyDictionary<string, JsonElement> variables)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty p in value.EnumerateObject())
                {
                    writer.WritePropertyName(p.Name);
                    Write(writer, p.Value, variables);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement e in value.EnumerateArray()) Write(writer, e, variables);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                Substitute(value, variables).WriteTo(writer);
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    // 기능: "name.field.field" 참조를 변수 사전에서 찾아 필드를 따라 내려간다.
    // 입력: reference - 점으로 이은 참조, variables - 변수 사전.
    // 출력: 참조된 JSON 값. 변수가 없거나 필드가 없으면 QaStepException.
    private static JsonElement Lookup(string reference, IReadOnlyDictionary<string, JsonElement> variables)
    {
        string[] parts = reference.Split('.');
        if (!variables.TryGetValue(parts[0], out JsonElement value))
            throw new QaStepException($"Unknown variable '${{{parts[0]}}}' (its saveAs step may have failed).");
        for (int i = 1; i < parts.Length; i++)
        {
            JsonElement? next = JsonPath.Child(value, parts[i]);
            if (next == null) throw new QaStepException($"Variable '${{{reference}}}' has no field '{parts[i]}'.");
            value = next.Value;
        }
        return value;
    }
}

// A step cannot run as written (bad parameter, unknown name at run time). The step fails with this message; it is not
// a tool error.
public sealed class QaStepException : Exception
{
    // 기능: 단계를 실패시키는 예외를 만든다(도구 오류 아님).
    // 입력: message - 실패 이유.
    // 출력: 그 메시지를 가진 예외.
    public QaStepException(string message) : base(message) { }
}

// Reading inside JSON values by dotted path. Object keys match case-insensitively (the server's camelCase or a
// scenario's own casing), array segments are indices.
public static class JsonPath
{
    // 기능: 객체의 속성(정확한 이름 우선, 없으면 대소문자 무시) 또는 배열의 인덱스 항목을 읽는다.
    // 입력: value - 부모 JSON 값, segment - 속성 이름 또는 배열 인덱스.
    // 출력: 자식 값. 없거나 부모가 객체·배열이 아니면 null.
    public static JsonElement? Child(JsonElement value, string segment)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty(segment, out JsonElement exact)) return exact;
            foreach (JsonProperty p in value.EnumerateObject())
            {
                if (string.Equals(p.Name, segment, StringComparison.OrdinalIgnoreCase)) return p.Value;
            }
            return null;
        }
        if (value.ValueKind == JsonValueKind.Array && int.TryParse(segment, out int index) && index >= 0 && index < value.GetArrayLength())
        {
            return value[index];
        }
        return null;
    }

    // 기능: 경로 조각을 차례로 따라 내려가 값을 읽는다.
    // 입력: root - 시작 값(null 허용), segments - 경로 조각들.
    // 출력: 끝까지 따라간 값. 중간에 없으면 null.
    public static JsonElement? Get(JsonElement? root, IEnumerable<string> segments)
    {
        JsonElement? current = root;
        foreach (string s in segments)
        {
            if (current == null) return null;
            current = Child(current.Value, s);
        }
        return current;
    }

    // 기능: 점 구분 경로("player.position.x")로 값을 읽는다.
    // 입력: root - 시작 값(null 허용), dotted - 점 구분 경로(빈 문자열이면 root 그대로).
    // 출력: 경로의 값. 없으면 null.
    public static JsonElement? Get(JsonElement? root, string dotted) =>
        dotted.Length == 0 ? root : Get(root, dotted.Split('.'));

    // 기능: 값을 메시지용 텍스트로 만든다.
    // 입력: value - JSON 값(null 허용).
    // 출력: null이면 "(missing)", 문자열이면 그대로, 그 외는 300자로 자른 원문 JSON.
    public static string Describe(JsonElement? value)
    {
        if (value == null) return "(missing)";
        return value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString()! : Truncate(value.Value.GetRawText(), 300);
    }

    // 기능: 문자열을 최대 길이로 자른다.
    // 입력: text - 원문, max - 최대 길이.
    // 출력: max 이하면 원문, 넘으면 앞 max자 + "...".
    public static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "...";

    // 기능: .NET 값을 QaJson.Options 규칙(camelCase, enum 문자열)으로 JsonElement로 바꾼다.
    // 입력: value - 직렬화할 값.
    // 출력: 직렬화된 JsonElement.
    public static JsonElement From<T>(T value) => JsonSerializer.SerializeToElement(value, QaJson.Options);
}

public static class QaJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Same rules on one line (UI API responses and SSE data).
    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };

    // 기능: JSON 값을 표시용 텍스트로 만든다.
    // 입력: value - JSON 값.
    // 출력: 문자열이면 따옴표 없는 값, 그 외는 원문 JSON.
    public static string Text(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();

    // 기능: 문자열을 UTF-8 바이트로 바꾼다.
    // 입력: s - 원문.
    // 출력: UTF-8 인코딩된 바이트 배열.
    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
}
