using System.Globalization;
using System.Text;
using System.Text.Json;
using ProjectH.Shared.Simulation;

namespace ProjectH.QA;

// One recorded simulation step (D29): what the Unity client put into its InputCommand. Seq and ViewTick are not
// recorded: the replaying connection numbers its own inputs and sends its own latest snapshot tick.
public readonly record struct RecordedInput(float MoveX, float MoveY, float Yaw, InputButtons Buttons, float AimYaw, float AimPitch);

// The Unity client's input recording (QA-5 D29, the `-qaRecord` JSON Lines file; format in
// Client/Assets/Scripts/Qa/QaInputRecordFormat.cs): a header line, then one line per simulation step. Read and
// validated as untrusted input (request §9 of the core rules): bounded file size, line length and line count, finite
// numbers in range, t strictly increasing. Everything is loaded into one array of at most MaxInputs entries
// (~1.5 MB), which the replaying actor reads by index.
public sealed class InputRecording
{
    public const int Version = 1;
    public const int MaxInputs = 54_000;                 // D29: 30 minutes at 30 Hz (QaInputRecordFormat.MaxInputLines)
    public const int MaxLineChars = 1024;                 // a real line is ~110 characters
    public const long MaxFileBytes = 16L * 1024 * 1024;   // 54,001 lines of at most ~300 bytes fit easily
    public const int MinSimHz = 1;
    public const int MaxSimHz = 240;
    private const double TimeEpsilon = 1e-6;

    public int SimHz { get; private init; }
    public string? DevPlayerId { get; private init; }
    public RecordedInput[] Inputs { get; private init; } = Array.Empty<RecordedInput>();
    // Button bits that are not InputButtons values (a newer client): masked away, counted here.
    public int UnknownButtonLines { get; private init; }

    public double Seconds => SimHz > 0 ? (double)Inputs.Length / SimHz : 0;

    public static InputRecording Load(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new RecordingException($"Recording not found: {path}");
        if (info.Length > MaxFileBytes) throw new RecordingException($"Recording too large ({info.Length} bytes, max {MaxFileBytes}).");
        if (info.Length == 0) throw new RecordingException("The recording is empty: the client writes nothing until the local player spawns (D29). Record again and spawn before quitting.");
        using var reader = new StreamReader(path, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return Read(reader);
    }

    public static InputRecording Read(TextReader reader)
    {
        string? header = ReadLine(reader, 1);
        if (header == null || header.Trim().Length == 0) throw new RecordingException("The recording is empty: the client writes nothing until the local player spawns (D29).");
        (int simHz, string? devPlayerId) = ReadHeader(header);

        var inputs = new List<RecordedInput>(1024);
        int unknownButtons = 0;
        double lastT = double.NegativeInfinity;
        int lineNumber = 1;
        const ushort known = (ushort)(InputButtons.Jump | InputButtons.Sprint | InputButtons.Fire | InputButtons.Reload | InputButtons.Slot1
            | InputButtons.Slot2 | InputButtons.Slot3 | InputButtons.Interact | InputButtons.Drop | InputButtons.UseMedkit
            | InputButtons.UseShieldCell | InputButtons.Crouch | InputButtons.ToolHarvest | InputButtons.ToolBuild);
        while (true)
        {
            lineNumber++;
            string? line = ReadLine(reader, lineNumber);
            if (line == null) break;
            if (line.Trim().Length == 0) continue;   // a trailing newline
            if (inputs.Count >= MaxInputs) throw new RecordingException($"More than {MaxInputs} inputs (30 minutes at 30 Hz, D29).");
            JsonElement o = ParseObject(line, lineNumber);
            double t = Number(o, "t", lineNumber, 0, 1e7);
            if (t <= lastT + TimeEpsilon) throw new RecordingException($"Line {lineNumber}: t {t.ToString(CultureInfo.InvariantCulture)} is not after the previous line's {lastT.ToString(CultureInfo.InvariantCulture)} (t must increase).");
            lastT = t;
            double buttons = Number(o, "buttons", lineNumber, 0, ushort.MaxValue);
            if (buttons != Math.Floor(buttons)) throw new RecordingException($"Line {lineNumber}: 'buttons' must be an integer.");
            ushort bits = (ushort)buttons;
            if ((bits & ~known) != 0) unknownButtons++;
            inputs.Add(new RecordedInput(
                (float)Number(o, "moveX", lineNumber, -1, 1),
                (float)Number(o, "moveY", lineNumber, -1, 1),
                (float)Number(o, "yaw", lineNumber, -1e6, 1e6),
                (InputButtons)(bits & known),
                (float)Number(o, "aimYaw", lineNumber, -1e6, 1e6),
                (float)Number(o, "aimPitch", lineNumber, -90, 90)));
        }
        if (inputs.Count == 0) throw new RecordingException("The recording has a header but no inputs.");
        return new InputRecording { SimHz = simHz, DevPlayerId = devPlayerId, Inputs = inputs.ToArray(), UnknownButtonLines = unknownButtons };
    }

    private static (int SimHz, string? DevPlayerId) ReadHeader(string line)
    {
        JsonElement o = ParseObject(line, 1);
        if (JsonPath.Child(o, "type") is not { ValueKind: JsonValueKind.String } type || type.GetString() != "header")
            throw new RecordingException("Line 1 must be the header {\"type\":\"header\",\"version\":1,\"simHz\":30,...} (is this a -qaRecord file?).");
        double version = Number(o, "version", 1, 0, int.MaxValue);
        if (version != Version) throw new RecordingException($"Recording version {version.ToString(CultureInfo.InvariantCulture)} is not supported (only {Version}).");
        double hz = Number(o, "simHz", 1, MinSimHz, MaxSimHz);
        if (hz != Math.Floor(hz)) throw new RecordingException("'simHz' must be an integer.");
        string? id = JsonPath.Child(o, "devPlayerId") is { ValueKind: JsonValueKind.String } d ? JsonPath.Truncate(d.GetString()!, 64) : null;
        return ((int)hz, id);
    }

    // Bounded line read: a line longer than MaxLineChars is refused without reading it whole.
    private static string? ReadLine(TextReader reader, int lineNumber)
    {
        var sb = new StringBuilder(128);
        while (true)
        {
            int c = reader.Read();
            if (c < 0) return sb.Length == 0 ? null : sb.ToString();
            if (c == '\n') return sb.ToString().TrimEnd('\r');
            if (sb.Length >= MaxLineChars) throw new RecordingException($"Line {lineNumber} is longer than {MaxLineChars} characters.");
            sb.Append((char)c);
        }
    }

    private static JsonElement ParseObject(string line, int lineNumber)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 4 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new RecordingException($"Line {lineNumber} is not a JSON object.");
            return doc.RootElement.Clone();
        }
        catch (JsonException e)
        {
            throw new RecordingException($"Line {lineNumber}: malformed JSON ({e.Message}).");
        }
    }

    private static double Number(JsonElement o, string name, int lineNumber, double min, double max)
    {
        if (JsonPath.Child(o, name) is not { ValueKind: JsonValueKind.Number } v || !v.TryGetDouble(out double d))
            throw new RecordingException($"Line {lineNumber}: '{name}' must be a number.");
        if (!double.IsFinite(d) || d < min || d > max)
            throw new RecordingException($"Line {lineNumber}: '{name}' {d.ToString(CultureInfo.InvariantCulture)} is out of range ({min.ToString(CultureInfo.InvariantCulture)}..{max.ToString(CultureInfo.InvariantCulture)}).");
        return d;
    }
}

public sealed class RecordingException : Exception
{
    public RecordingException(string message) : base(message) { }
}
