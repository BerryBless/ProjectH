using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProjectH.QA;

// QA-5 D34: `convert-recording <file.jsonl> [--out QA/Scenarios/Recorded/<name>.json] [--actor playerA] [--force]`.
// Validates a Unity -qaRecord file, copies it next to the new scenario as <name>.inputs.jsonl, and writes a draft
// scenario that connects a headless actor and replays the inputs (playInputs). The draft's asserts are placeholders
// described in its description: what a recording should prove is the author's call. Exit 0 written, 2 refused.
public static partial class RecordingConverter
{
    public const string InputsSuffix = ".inputs.jsonl";
    private const int TimeoutMarginMs = 15_000;

    [GeneratedRegex(@"[^A-Za-z0-9_\-]")]
    private static partial Regex Unsafe();

    public static int Run(string root, string recordingPath, string? outPath, string actor, bool force, TextWriter output)
    {
        string source = Path.GetFullPath(recordingPath);
        InputRecording recording;
        try
        {
            recording = InputRecording.Load(source);
        }
        catch (Exception e) when (e is RecordingException or IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"INVALID recording {recordingPath}: {e.Message}");
            return 2;
        }
        if (ActorManager.CheckAlias(actor) is string aliasError)
        {
            output.WriteLine($"--actor: {aliasError}");
            return 2;
        }

        string scenarios = Path.GetFullPath(Path.Combine(root, "QA", "Scenarios"));
        // --out is relative to the repository root, like the other QA paths (an absolute path is taken as it is).
        string target = outPath != null
            ? Path.GetFullPath(Path.Combine(root, outPath))
            : Path.Combine(scenarios, "Recorded", DefaultName(source) + ".json");
        if (!target.StartsWith(scenarios + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !target.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine($"--out must be a .json file under QA/Scenarios (got {outPath}).");
            return 2;
        }
        string name = Path.GetFileNameWithoutExtension(target);
        string inputsFile = name + InputsSuffix;
        string inputsPath = Path.Combine(Path.GetDirectoryName(target)!, inputsFile);
        if (!force && (File.Exists(target) || File.Exists(inputsPath)))
        {
            output.WriteLine($"{Path.GetRelativePath(root, File.Exists(target) ? target : inputsPath)} already exists (use --force to replace it).");
            return 2;
        }
        if (string.Equals(Path.GetFullPath(inputsPath), source, StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine("The recording is already the scenario's inputs file; choose another --out.");
            return 2;
        }

        // The draft first, then the inputs (the larger write). A failure removes what this call created, so no scenario
        // is left pointing at a missing or half-copied recording; files that existed before (--force) are left as they are.
        bool targetExisted = File.Exists(target);
        bool inputsExisted = File.Exists(inputsPath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, Draft(name, recordingPath, recording, inputsFile, actor), new UTF8Encoding(false));
            File.Copy(source, inputsPath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (!inputsExisted) TryDelete(inputsPath);
            if (!targetExisted) TryDelete(target);
            output.WriteLine($"Could not write the scenario: {e.Message}");
            return 2;
        }
        output.WriteLine($"Recording: {recording.Inputs.Length} inputs, {recording.Seconds:0.##} s at {recording.SimHz} Hz" +
                         (recording.DevPlayerId != null ? $", recorded as {recording.DevPlayerId}" : string.Empty) +
                         (recording.UnknownButtonLines > 0 ? $", {recording.UnknownButtonLines} lines with unknown button bits (ignored)" : string.Empty));
        output.WriteLine($"Wrote {Path.GetRelativePath(root, target)} and {Path.GetRelativePath(root, inputsPath)}.");
        output.WriteLine("The asserts are placeholders: edit them to say what this recording must prove, then run it.");
        return 0;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort: the error above is what gets reported.
        }
    }

    // "Recordings/run1.inputs.jsonl" -> "run1"; anything but [A-Za-z0-9_-] becomes '_'.
    public static string DefaultName(string recordingPath)
    {
        string name = Path.GetFileName(recordingPath);
        if (name.EndsWith(InputsSuffix, StringComparison.OrdinalIgnoreCase)) name = name[..^InputsSuffix.Length];
        else name = Path.GetFileNameWithoutExtension(name);
        name = Unsafe().Replace(name, "_");
        return name.Length == 0 ? "recording" : name.Length > 64 ? name[..64] : name;
    }

    public static int PlayTimeoutMs(InputRecording recording) =>
        (int)Math.Min(ScenarioValidator.MaxStepTimeoutMs, Math.Ceiling(recording.Inputs.Length * 1000.0 / recording.SimHz) + TimeoutMarginMs);

    // The repository's scenario layout (one step per line) so the draft diffs like the hand-written ones.
    private static string Draft(string name, string sourceText, InputRecording recording, string inputsFile, string actor)
    {
        int playMs = PlayTimeoutMs(recording);
        int scenarioSeconds = (int)Math.Ceiling(playMs / 1000.0) + 60;
        string seconds = recording.Seconds.ToString("0.##", CultureInfo.InvariantCulture);
        string description =
            $"Draft from convert-recording ({Path.GetFileName(sourceText)}: {recording.Inputs.Length} inputs, {seconds} s at {recording.SimHz} Hz" +
            (recording.DevPlayerId != null ? $", recorded as {recording.DevPlayerId}" : string.Empty) + "). " +
            "A headless client replays the recorded inputs one per tick (D34). The recording has inputs only: build requests, UI and the server's state at record time are not in it, so the replay can end differently (another spawn point, loot, other players). " +
            "Server:DevRespawn keeps one client able to move and shoot without a second player. " +
            "TODO: replace the placeholder asserts (connected, alive) with what this recording must prove, e.g. player.position.x approximately N, player.health, event.PlayerDamaged; arrange the start with setPosition / giveWeapon if the inputs need it.";
        static string J(object value) => JsonSerializer.Serialize(value, QaJson.Compact);
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"schemaVersion\": 1,\n");
        sb.Append("  \"name\": ").Append(J("Recorded " + name)).Append(",\n");
        sb.Append("  \"description\": ").Append(J(description)).Append(",\n");
        sb.Append("  \"tags\": [\"recorded\"],\n");
        sb.Append("  \"seed\": 1,\n");
        sb.Append("  \"timeoutSeconds\": ").Append(scenarioSeconds.ToString(CultureInfo.InvariantCulture)).Append(",\n");
        sb.Append("  \"server\": { \"mode\": \"launch\", \"options\": { \"Server:DevRespawn\": \"true\" } },\n");
        sb.Append("  \"actors\": [ { \"id\": ").Append(J(actor)).Append(" } ],\n");
        sb.Append("  \"steps\": [\n");
        sb.Append("    { \"id\": \"connect\", \"action\": \"connect\", \"actor\": ").Append(J(actor)).Append(" },\n");
        sb.Append("    { \"id\": \"on_server\", \"action\": \"waitFor\", \"condition\": \"player.alive\", \"actor\": ").Append(J(actor)).Append(", \"equals\": true, \"timeoutMilliseconds\": 5000 },\n");
        sb.Append("    { \"id\": \"play\", \"phase\": \"act\", \"action\": \"playInputs\", \"actor\": ").Append(J(actor)).Append(", \"file\": ").Append(J(inputsFile))
          .Append(", \"timeoutMilliseconds\": ").Append(playMs.ToString(CultureInfo.InvariantCulture)).Append(", \"saveAs\": \"played\" },\n");
        sb.Append("    { \"id\": \"placeholder_connected\", \"phase\": \"assert\", \"assert\": \"player.connected\", \"actor\": ").Append(J(actor))
          .Append(", \"equals\": true, \"description\": \"Placeholder: still connected after the replay. Replace with what the recording must prove.\" },\n");
        sb.Append("    { \"id\": \"placeholder_alive\", \"assert\": \"player.alive\", \"actor\": ").Append(J(actor))
          .Append(", \"equals\": true, \"description\": \"Placeholder: e.g. player.position.x approximately <x>, player.health lessThan 100, event.PlayerDamaged greaterThan 0.\" }\n");
        sb.Append("  ]\n");
        sb.Append("}\n");
        return sb.ToString();
    }
}
