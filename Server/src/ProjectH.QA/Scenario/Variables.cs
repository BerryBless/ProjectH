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

    public static IEnumerable<string> ReferencedRoots(string text)
    {
        foreach (Match m in Reference().Matches(text)) yield return m.Groups[1].Value.Split('.')[0];
    }

    public static bool HasReference(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!.Contains("${", StringComparison.Ordinal),
        JsonValueKind.Object or JsonValueKind.Array => value.GetRawText().Contains("${", StringComparison.Ordinal),
        _ => false,
    };

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

    public static string SubstituteText(string text, IReadOnlyDictionary<string, JsonElement> variables) =>
        Reference().Replace(text, m =>
        {
            JsonElement v = Lookup(m.Groups[1].Value, variables);
            return v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText();
        });

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
    public QaStepException(string message) : base(message) { }
}

// Reading inside JSON values by dotted path. Object keys match case-insensitively (the server's camelCase or a
// scenario's own casing), array segments are indices.
public static class JsonPath
{
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

    public static JsonElement? Get(JsonElement? root, string dotted) =>
        dotted.Length == 0 ? root : Get(root, dotted.Split('.'));

    public static string Describe(JsonElement? value)
    {
        if (value == null) return "(missing)";
        return value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString()! : Truncate(value.Value.GetRawText(), 300);
    }

    public static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "...";

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

    public static string Text(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();

    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
}
