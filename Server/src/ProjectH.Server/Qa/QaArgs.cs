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

    public QaArgs(JsonElement args) => _args = args;

    public string? Error { get; private set; }

    public bool Has(string name) => TryGet(name, out _);

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

    private void Fail(string message) => Error ??= message;

    // A finite number in [min, max], or null when missing.
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

    public double RequiredNumber(string name, double min, double max)
    {
        double? value = Number(name, min, max);
        if (value == null && !Has(name)) Fail($"'{name}' is required.");
        return value ?? 0;
    }

    // A whole number in [min, max], or null when missing.
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

    public long RequiredInteger(string name, long min, long max)
    {
        long? value = Integer(name, min, max);
        if (value == null && !Has(name)) Fail($"'{name}' is required.");
        return value ?? 0;
    }

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

    public string RequiredText(string name, int maxLength = 200)
    {
        string? text = Text(name, maxLength);
        if (text == null && !Has(name)) Fail($"'{name}' is required.");
        return text ?? "";
    }

    public bool? Bool(string name)
    {
        if (!TryGet(name, out JsonElement value)) return null;
        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out bool parsed)) return parsed;
        Fail($"'{name}' must be true or false.");
        return null;
    }

    // One of the names (case-insensitive): its index, or -1 with Error set (or -1 without an error when missing).
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

    public int RequiredChoice(string name, params string[] names)
    {
        int index = Choice(name, names);
        if (index < 0 && !Has(name)) Fail($"'{name}' is required ({string.Join(", ", names)}).");
        return index;
    }

    public void Invalid(string message) => Fail(message);
}
