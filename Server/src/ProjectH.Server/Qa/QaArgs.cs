using System;
using System.Globalization;
using System.Text.Json;

namespace ProjectH.Server.Qa;

// Reads one command's "args" object. Numbers may be JSON numbers or numeric strings (a scenario's ${variable} may be
// substituted as a string); names compare case-insensitively. The first problem found is kept in Error and every later
// read returns a default, so a command checks Error once after reading all of its arguments.
internal sealed class QaArgs
{
    private readonly JsonElement _args;

    // 기능: 명령의 "args" 객체를 감싼다.
    // 입력: args - 명령 본문의 args(없으면 Undefined).
    // 출력: Error가 null인 QaArgs.
    public QaArgs(JsonElement args) => _args = args;

    public string? Error { get; private set; }

    // 기능: 이름의 인자가 있는지 본다(대소문자 무시, null 값은 없는 것).
    // 입력: name - 인자 이름.
    // 출력: 있으면 true.
    public bool Has(string name) => TryGet(name, out _);

    // 기능: 이름의 인자 값을 찾는다(대소문자 무시, args가 객체가 아니거나 값이 null이면 없는 것).
    // 입력: name - 인자 이름, value - 값을 받을 곳.
    // 출력: 있으면 true와 값, 없으면 false.
    private bool TryGet(string name, out JsonElement value)
    {
        value = default;
        if (_args.ValueKind != JsonValueKind.Object) return false;
        foreach (JsonProperty property in _args.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (property.Value.ValueKind == JsonValueKind.Null) return false;
            value = property.Value;
            return true;
        }
        return false;
    }

    // 기능: 첫 오류만 기억한다(이미 있으면 그대로).
    // 입력: message - 오류 메시지.
    // 출력: 반환값 없음. Error가 비어 있었으면 채워진다.
    private void Fail(string message) => Error ??= message;

    // 기능: [min, max]의 유한한 수를 읽는다(JSON 수 또는 숫자 문자열).
    // 입력: name - 인자 이름, min·max - 허용 범위.
    // 출력: 읽은 수, 없으면 null, 잘못됐으면 null과 Error.
    public double? Number(string name, double min, double max)
    {
        if (!TryGet(name, out JsonElement value)) return null;
        double number;
        if (value.ValueKind == JsonValueKind.Number) number = value.GetDouble();
        else if (value.ValueKind != JsonValueKind.String ||
                 !double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            Fail($"'{name}' must be a number.");
            return null;
        }
        if (!double.IsFinite(number))
        {
            Fail($"'{name}' must be a finite number.");
            return null;
        }
        if (number < min || number > max)
        {
            Fail($"'{name}' must be {min.ToString(CultureInfo.InvariantCulture)}-{max.ToString(CultureInfo.InvariantCulture)}.");
            return null;
        }
        return number;
    }

    // 기능: 필수 수를 읽는다(없으면 Error).
    // 입력: name - 인자 이름, min·max - 허용 범위.
    // 출력: 읽은 수, 없거나 잘못됐으면 0과 Error.
    public double RequiredNumber(string name, double min, double max)
    {
        double? value = Number(name, min, max);
        if (value == null && !Has(name)) Fail($"'{name}' is required.");
        return value ?? 0;
    }

    // 기능: [min, max]의 정수를 읽는다(소수점이 있으면 오류).
    // 입력: name - 인자 이름, min·max - 허용 범위.
    // 출력: 읽은 정수, 없으면 null, 잘못됐으면 null과 Error.
    public long? Integer(string name, long min, long max)
    {
        double? value = Number(name, min, max);
        if (value == null) return null;
        if (Math.Floor(value.Value) != value.Value)
        {
            Fail($"'{name}' must be a whole number.");
            return null;
        }
        return (long)value.Value;
    }

    // 기능: 필수 정수를 읽는다(없으면 Error).
    // 입력: name - 인자 이름, min·max - 허용 범위.
    // 출력: 읽은 정수, 없거나 잘못됐으면 0과 Error.
    public long RequiredInteger(string name, long min, long max)
    {
        long? value = Integer(name, min, max);
        if (value == null && !Has(name)) Fail($"'{name}' is required.");
        return value ?? 0;
    }

    // 기능: 문자열을 읽는다(JSON 수는 원문 그대로 문자열로).
    // 입력: name - 인자 이름, maxLength - 허용 길이.
    // 출력: 읽은 문자열, 없으면 null, 문자열·수가 아니거나 길면 null과 Error.
    public string? Text(string name, int maxLength = 200)
    {
        if (!TryGet(name, out JsonElement value)) return null;
        string? text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
        if (text == null)
        {
            Fail($"'{name}' must be a string.");
            return null;
        }
        if (text.Length > maxLength)
        {
            Fail($"'{name}' is longer than {maxLength} characters.");
            return null;
        }
        return text;
    }

    // 기능: 필수 문자열을 읽는다(없으면 Error).
    // 입력: name - 인자 이름, maxLength - 허용 길이.
    // 출력: 읽은 문자열, 없거나 잘못됐으면 ""과 Error.
    public string RequiredText(string name, int maxLength = 200)
    {
        string? text = Text(name, maxLength);
        if (text == null && !Has(name)) Fail($"'{name}' is required.");
        return text ?? "";
    }

    // 기능: bool을 읽는다(JSON true/false 또는 "true"/"false" 문자열).
    // 입력: name - 인자 이름.
    // 출력: 읽은 값, 없으면 null, 잘못됐으면 null과 Error.
    public bool? Bool(string name)
    {
        if (!TryGet(name, out JsonElement value)) return null;
        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out bool parsed)) return parsed;
        Fail($"'{name}' must be true or false.");
        return null;
    }

    // 기능: 이름 목록 중 하나를 읽는다(대소문자 무시, 앞뒤 공백 무시).
    // 입력: name - 인자 이름, names - 허용 이름들.
    // 출력: 고른 이름의 번호, 없으면 -1(Error 없음), 목록에 없거나 문자열이 아니면 -1과 Error.
    public int Choice(string name, params string[] names)
    {
        string? text = Text(name);
        if (text == null) return -1;
        for (int i = 0; i < names.Length; i++)
        {
            if (string.Equals(text.Trim(), names[i], StringComparison.OrdinalIgnoreCase)) return i;
        }
        Fail($"'{name}' must be one of: {string.Join(", ", names)}.");
        return -1;
    }

    // 기능: 필수 선택을 읽는다(없으면 Error).
    // 입력: name - 인자 이름, names - 허용 이름들.
    // 출력: 고른 이름의 번호, 없거나 잘못됐으면 -1과 Error.
    public int RequiredChoice(string name, params string[] names)
    {
        int index = Choice(name, names);
        if (index < 0 && !Has(name)) Fail($"'{name}' is required ({string.Join(", ", names)}).");
        return index;
    }

    // 기능: 명령이 직접 찾은 인자 오류를 기록한다(첫 오류만 남는다).
    // 입력: message - 오류 메시지.
    // 출력: 반환값 없음. Error가 비어 있었으면 채워진다.
    public void Invalid(string message) => Fail(message);
}
