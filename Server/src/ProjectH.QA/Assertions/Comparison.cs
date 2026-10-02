using System.Globalization;
using System.Text.Json;

namespace ProjectH.QA;

// Request §30, §36: the assertion operators. Type-aware: numbers compare as numbers (a numeric string counts, since
// the server may send either), booleans as booleans, strings case-insensitively (enum names like "Playing" vs
// "playing"); a number compared with a name is first turned into the enum name when the path has a known enum.
public static class Comparison
{
    public static readonly string[] Operators =
    {
        "equals", "notEquals", "greaterThan", "lessThan", "between", "exists", "notExists", "approximately", "contains",
    };

    public const double DefaultTolerance = 0.01;
    private const double Epsilon = 1e-9;

    public static bool IsOperator(string name) => Array.IndexOf(Operators, name) >= 0;

    public static bool TryNumber(JsonElement value, out double number)
    {
        number = 0;
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetDouble(out number);
        if (value.ValueKind == JsonValueKind.String)
            return double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
        return false;
    }

    // Evaluates one operator. expected = the operator's value (already substituted); tolerance only for approximately.
    // Returns pass/fail and a readable "Expected" text.
    public static (bool Passed, string Expected) Evaluate(string op, JsonElement expected, JsonElement? tolerance, JsonElement? actual, Type? enumHint)
    {
        actual = Normalize(actual, expected, enumHint);
        switch (op)
        {
            case "exists":
            {
                bool want = expected.ValueKind != JsonValueKind.False;
                bool has = Exists(actual);
                return (has == want, want ? "exists" : "does not exist");
            }
            case "notExists":
            {
                bool want = expected.ValueKind != JsonValueKind.False;
                bool has = Exists(actual);
                return (has != want, want ? "does not exist" : "exists");
            }
            case "equals":
                return (AreEqual(actual, expected), Text(expected));
            case "notEquals":
                return (!AreEqual(actual, expected), "not " + Text(expected));
            case "greaterThan":
                return (Numbers(actual, expected, out double a, out double e) && a > e, "> " + Text(expected));
            case "lessThan":
                return (Numbers(actual, expected, out a, out e) && a < e, "< " + Text(expected));
            case "between":
            {
                if (expected.ValueKind != JsonValueKind.Array || expected.GetArrayLength() != 2
                    || !TryNumber(expected[0], out double low) || !TryNumber(expected[1], out double high))
                    throw new QaStepException("'between' needs [low, high] numbers.");
                bool ok = actual != null && TryNumber(actual.Value, out double v) && v >= low - Epsilon && v <= high + Epsilon;
                return (ok, $"between {low.ToString(CultureInfo.InvariantCulture)} and {high.ToString(CultureInfo.InvariantCulture)}");
            }
            case "approximately":
            {
                double tol = DefaultTolerance;
                if (tolerance != null)
                {
                    if (!TryNumber(tolerance.Value, out tol) || tol < 0) throw new QaStepException("'tolerance' must be a non-negative number.");
                }
                if (!TryNumber(expected, out double target)) throw new QaStepException("'approximately' needs a number.");
                bool ok = actual != null && TryNumber(actual.Value, out double v) && Math.Abs(v - target) <= tol + Epsilon;
                return (ok, $"{target.ToString(CultureInfo.InvariantCulture)} ± {tol.ToString(CultureInfo.InvariantCulture)}");
            }
            case "contains":
            {
                bool ok = false;
                if (actual is { ValueKind: JsonValueKind.Array } list)
                {
                    foreach (JsonElement item in list.EnumerateArray())
                    {
                        if (AreEqual(item, expected)) ok = true;
                    }
                }
                else if (actual is { ValueKind: JsonValueKind.String } s)
                {
                    ok = s.GetString()!.Contains(Text(expected), StringComparison.OrdinalIgnoreCase);
                }
                return (ok, "contains " + Text(expected));
            }
            default:
                throw new QaStepException($"Unknown operator '{op}'.");
        }
    }

    public static bool Exists(JsonElement? value) => value != null && value.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    public static bool AreEqual(JsonElement? actual, JsonElement expected)
    {
        if (actual == null || actual.Value.ValueKind == JsonValueKind.Null) return expected.ValueKind == JsonValueKind.Null;
        JsonElement a = actual.Value;
        if (expected.ValueKind == JsonValueKind.Number || a.ValueKind == JsonValueKind.Number)
        {
            return TryNumber(a, out double x) && TryNumber(expected, out double y) && Math.Abs(x - y) <= Epsilon * Math.Max(1.0, Math.Abs(y));
        }
        if (expected.ValueKind is JsonValueKind.True or JsonValueKind.False || a.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return TryBool(a, out bool x) && TryBool(expected, out bool y) && x == y;
        }
        if (expected.ValueKind == JsonValueKind.String && a.ValueKind == JsonValueKind.String)
        {
            return string.Equals(a.GetString(), expected.GetString(), StringComparison.OrdinalIgnoreCase);
        }
        return a.ValueKind == expected.ValueKind && Canonical(a) == Canonical(expected);
    }

    public static string Text(JsonElement? value) => JsonPath.Describe(value);

    // A numeric enum value compared with a name becomes the name (match.state 2 → "Playing").
    private static JsonElement? Normalize(JsonElement? actual, JsonElement expected, Type? enumHint)
    {
        if (actual == null || enumHint == null) return actual;
        if (actual.Value.ValueKind == JsonValueKind.Number && expected.ValueKind == JsonValueKind.String
            && !TryNumber(expected, out _) && actual.Value.TryGetInt32(out int n) && n >= 0 && n <= byte.MaxValue)
        {
            string? name = Enum.GetName(enumHint, Enum.ToObject(enumHint, n));
            if (name != null) return JsonSerializer.SerializeToElement(name);
        }
        return actual;
    }

    private static bool Numbers(JsonElement? actual, JsonElement expected, out double a, out double e)
    {
        a = 0;
        if (!TryNumber(expected, out e)) throw new QaStepException($"Expected a number, got {Text(expected)}.");
        return actual != null && TryNumber(actual.Value, out a);
    }

    private static bool TryBool(JsonElement value, out bool b)
    {
        b = false;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            b = value.GetBoolean();
            return true;
        }
        return value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out b);
    }

    private static string Canonical(JsonElement e) => JsonSerializer.Serialize(e);
}
