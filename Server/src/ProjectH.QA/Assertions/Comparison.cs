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

    // 기능: 이름이 비교 연산자인지 본다.
    // 입력: name - 검사할 이름.
    // 출력: Operators에 있으면 true, 아니면 false.
    public static bool IsOperator(string name) => Array.IndexOf(Operators, name) >= 0;

    // 기능: JSON 값(숫자 또는 숫자 문자열)을 double로 읽는다.
    // 입력: value - 읽을 JSON 값, number - 읽은 숫자(out, 실패 시 0).
    // 출력: 유한한 숫자로 읽으면 true와 값, 아니면 false.
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
    // 기능: 연산자 하나로 실제값을 기대값과 비교한다(enum 힌트가 있으면 숫자 실제값을 이름으로 바꾼 뒤).
    // 입력: op - 연산자 이름, expected - 기대값(치환 뒤), tolerance - approximately의 허용 오차(null이면 DefaultTolerance), actual - 실제값(null이면 없음), enumHint - 경로의 enum 타입(없으면 null).
    // 출력: (통과 여부, 사람이 읽을 기대값 설명). 모르는 연산자, 잘못된 between·tolerance·approximately 값이면 QaStepException.
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

    // 기능: 값이 있고 null·undefined가 아닌지 본다.
    // 입력: value - 검사할 값(null 가능).
    // 출력: 실제 값이 있으면 true, 아니면 false.
    public static bool Exists(JsonElement? value) => value != null && value.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    // 기능: 두 값을 타입에 맞게 비교한다(한쪽이 숫자면 숫자로, bool이면 bool로, 둘 다 문자열이면 대소문자 무시, 그 외는 JSON 직렬화 비교).
    // 입력: actual - 실제값(null이면 없음), expected - 기대값.
    // 출력: 같으면 true. 실제값이 없거나 null이면 기대값이 null일 때만 true.
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

    // 기능: 값을 사람이 읽을 문자열로 만든다.
    // 입력: value - 설명할 값(null 가능).
    // 출력: 설명 문자열.
    public static string Text(JsonElement? value) => JsonPath.Describe(value);

    // 기능: enum 힌트가 있고 실제값이 0-255 숫자, 기대값이 숫자가 아닌 문자열이면 실제값을 enum 이름으로 바꾼다.
    // 입력: actual - 실제값, expected - 기대값, enumHint - enum 타입(없으면 null).
    // 출력: 이름으로 바뀐 실제값, 해당 없으면 원래 실제값.
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

    // 기능: 기대값과 실제값을 숫자로 읽는다.
    // 입력: actual - 실제값, expected - 기대값, a - 실제 숫자(out), e - 기대 숫자(out).
    // 출력: 실제값이 숫자면 true, 아니면 false. 기대값이 숫자가 아니면 QaStepException.
    private static bool Numbers(JsonElement? actual, JsonElement expected, out double a, out double e)
    {
        a = 0;
        if (!TryNumber(expected, out e)) throw new QaStepException($"Expected a number, got {Text(expected)}.");
        return actual != null && TryNumber(actual.Value, out a);
    }

    // 기능: JSON bool 또는 "true"/"false" 문자열을 bool로 읽는다.
    // 입력: value - 읽을 값, b - 읽은 bool(out, 실패 시 false).
    // 출력: 읽으면 true와 값, 아니면 false.
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

    // 기능: JSON 값을 직렬화한 정규 문자열로 만든다(배열·객체 비교용).
    // 입력: e - 직렬화할 값.
    // 출력: 직렬화된 JSON 문자열.
    private static string Canonical(JsonElement e) => JsonSerializer.Serialize(e);
}
