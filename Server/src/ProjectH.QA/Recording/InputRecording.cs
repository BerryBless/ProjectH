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

    // 기능: 녹화 파일의 존재·크기(0 또는 MaxFileBytes 초과)를 확인한 뒤 UTF-8로 열어 Read로 파싱한다.
    // 입력: path - -qaRecord JSON Lines 파일 경로.
    // 출력: 검증된 InputRecording. 파일이 없거나 비었거나 너무 크거나 형식이 틀리면 RecordingException.
    public static InputRecording Load(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new RecordingException($"Recording not found: {path}");
        if (info.Length > MaxFileBytes) throw new RecordingException($"Recording too large ({info.Length} bytes, max {MaxFileBytes}).");
        if (info.Length == 0) throw new RecordingException("The recording is empty: the client writes nothing until the local player spawns (D29). Record again and spawn before quitting.");
        using var reader = new StreamReader(path, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return Read(reader);
    }

    // 기능: 녹화 파일을 읽어 검증한다(모르는 버튼 비트는 지우고 센다. Phase 17: InteractHeld·ThrowGrenade는 아는 비트).
    // 입력: reader - 녹화 텍스트.
    // 출력: InputRecording. 형식이 틀리면 RecordingException(줄 번호 포함).
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
            | InputButtons.UseShieldCell | InputButtons.Crouch | InputButtons.ToolHarvest | InputButtons.ToolBuild
            | InputButtons.InteractHeld | InputButtons.ThrowGrenade);   // Phase 14 InteractHeld, Phase 17 ThrowGrenade
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

    // 기능: 1행 헤더({"type":"header","version":1,"simHz":N,"devPlayerId"?})를 검증해 시뮬레이션 Hz와 개발 플레이어 ID를 읽는다.
    // 입력: line - 헤더 줄 텍스트.
    // 출력: simHz와 devPlayerId(없으면 null, 64자로 자름). 헤더가 아니거나 버전·Hz가 맞지 않으면 RecordingException.
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

    // 기능: 한 줄을 MaxLineChars까지만 읽는다(초과하면 끝까지 읽지 않고 거부).
    // 입력: reader - 녹화 텍스트, lineNumber - 오류 메시지용 줄 번호.
    // 출력: CR을 뗀 한 줄. 파일 끝이면 null. 너무 길면 RecordingException.
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

    // 기능: 한 줄을 깊이 4 이하의 JSON 객체로 파싱한다.
    // 입력: line - 줄 텍스트, lineNumber - 오류 메시지용 줄 번호.
    // 출력: 복제된 JSON 객체. 객체가 아니거나 JSON이 깨졌으면 RecordingException.
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

    // 기능: 객체의 숫자 필드를 읽어 유한하고 범위 안인지 검사한다.
    // 입력: o - JSON 객체, name - 필드 이름, lineNumber - 오류 메시지용 줄 번호, min - 최소값, max - 최대값.
    // 출력: 필드 값. 숫자가 아니거나 범위 밖이면 RecordingException.
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
    // 기능: 녹화 파일 형식 오류 예외를 만든다.
    // 입력: message - 줄 번호가 포함된 오류 설명.
    // 출력: 그 메시지를 가진 예외.
    public RecordingException(string message) : base(message) { }
}
