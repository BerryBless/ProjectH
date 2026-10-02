using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProjectH.QA;

// QA-5 D33 (request §135-136): one history line per finished run in <report root>/history/<key>.jsonl, the last
// MaxEntries per scenario file, and a comparison with the latest earlier PASSED run of the same scenario and the same
// parameter set. Local only (QA/Reports is gitignored).
//
// Two tool processes may finish runs of the same scenario at once. Writes take an OS file lock on <key>.lock
// (FileShare.None, bounded retry), read the file, append, keep the tail, write a temp file in the same folder and
// replace the history file with it. The lock is the only one here (no nesting), held only for that read-modify-write,
// never across an await; a writer that cannot get it within LockWait gives up with a warning (never a failure).
// Lifetime: the lock handle is disposed at the end of Record; the .lock file stays (an empty marker, reused).
public static partial class BaselineHistory
{
    public const int MaxEntries = 50;
    public const long MaxFileBytes = 4 * 1024 * 1024;     // 50 short lines are a few KB; larger means it is not ours
    public const int MaxLineChars = 64 * 1024;
    public const int MaxStringValueChars = 200;
    public const int MaxParametersChars = 4096;
    public static readonly TimeSpan LockWait = TimeSpan.FromSeconds(3);

    // The server metrics every entry records (from /qa/metrics at the end of the run) and the assertion paths that
    // give each one an explicit threshold.
    public static readonly (string Name, string[] Paths)[] ServerMetrics =
    {
        ("tickP50Ms", new[] { "server.tickP50Ms", "server.metrics.tickP50Ms" }),
        ("tickP95Ms", new[] { "server.tickP95Ms", "server.metrics.tickP95Ms" }),
        ("tickP99Ms", new[] { "server.tickP99Ms", "server.metrics.tickP99Ms" }),
        ("workingSetMB", new[] { "server.memoryMB", "server.workingSetMB", "server.metrics.workingSetMB" }),
    };

    [GeneratedRegex(@"[^A-Za-z0-9_\-]")]
    private static partial Regex Unsafe();

    // The scenario file as recorded in every history line: relative to QA/Scenarios with '/' ("Stress/load_bots_10.json"),
    // or the full path for a file outside QA/Scenarios.
    public static string ScenarioFile(string repoRoot, string scenarioPath)
    {
        string full = Path.GetFullPath(scenarioPath);
        string scenarios = Path.GetFullPath(Path.Combine(repoRoot, "QA", "Scenarios")) + Path.DirectorySeparatorChar;
        return full.StartsWith(scenarios, StringComparison.OrdinalIgnoreCase) ? full[scenarios.Length..].Replace('\\', '/') : full;
    }

    // A readable part (the relative path without ".json", every other character '_') plus a hash of the path itself, so
    // two files whose readable parts collapse to the same text ("Stress/a.json", "Stress_a.json") never share a history
    // ("Stress/load_bots_10.json" -> "Stress_load_bots_10_<8 hex>"). Outside QA/Scenarios: "external_<name>_<hash>".
    public static string Key(string repoRoot, string scenarioPath)
    {
        string full = Path.GetFullPath(scenarioPath);
        string file = ScenarioFile(repoRoot, scenarioPath);
        string hash = Hash(file.ToLowerInvariant())[..8];
        string readable;
        if (!string.Equals(file, full, StringComparison.Ordinal))
        {
            string relative = file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? file[..^5] : file;
            readable = Unsafe().Replace(relative, "_");
        }
        else
        {
            readable = "external_" + Unsafe().Replace(Path.GetFileNameWithoutExtension(full), "_");
        }
        if (readable.Length > 110) readable = readable[..110];
        return readable + "_" + hash;
    }

    // Same parameter set = same canonical (compact) JSON; long sets compare by hash.
    public static string? ParametersKey(JsonElement? parameters)
    {
        if (parameters == null) return null;
        string text = JsonSerializer.Serialize(parameters.Value, QaJson.Compact);
        return text.Length <= 512 ? text : "sha256:" + Hash(text);
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static HistoryEntry EntryFor(RunReport report, ScenarioDefinition scenario, string scenarioFile, IReadOnlyDictionary<string, JsonElement> variables)
    {
        var entry = new HistoryEntry
        {
            RunId = report.RunId,
            Utc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Scenario = scenario.Name,
            ScenarioFile = scenarioFile,
            Seed = report.Seed,
            ParametersKey = ParametersKey(report.Parameters),
            GitCommit = report.GitCommit,
            GitDirty = report.GitDirty,
            Status = report.Status.ToString(),
            DurationMs = report.DurationMs,
        };
        if (report.Parameters is JsonElement p && JsonSerializer.Serialize(p, QaJson.Compact).Length <= MaxParametersChars) entry.Parameters = p;
        if (report.Metrics is JsonElement m)
        {
            foreach ((string name, _) in ServerMetrics)
            {
                if (JsonPath.Child(m, name) is JsonElement v && Comparison.TryNumber(v, out double d) && double.IsFinite(d)) entry.Metrics[name] = d;
            }
        }
        foreach (string name in scenario.BaselineValues)
        {
            // Scalars only (a saved object such as a fire result would make lines large); strings bounded.
            if (!variables.TryGetValue(name, out JsonElement v)) continue;
            entry.Values[name] = v.ValueKind switch
            {
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => v.Clone(),
                JsonValueKind.String => JsonPath.From(JsonPath.Truncate(v.GetString()!, MaxStringValueChars)),
                _ => JsonPath.From($"({v.ValueKind.ToString().ToLowerInvariant()}, not recorded)"),
            };
        }
        return entry;
    }

    // Appends `entry` to the scenario's history (keeping the last MaxEntries) and returns the latest earlier PASSED
    // entry with the same parameter set. Never throws: a failure is the warning.
    public static (HistoryEntry? Previous, string? Warning) Record(string historyDir, string key, HistoryEntry entry)
    {
        string file = Path.Combine(historyDir, key + ".jsonl");
        string? temp = null;
        try
        {
            Directory.CreateDirectory(historyDir);
            using FileStream? lockHandle = AcquireLock(Path.Combine(historyDir, key + ".lock"));
            if (lockHandle == null) return (null, $"Baseline history not written: {key}.lock is held by another QA process (waited {LockWait.TotalSeconds:0} s).");

            var lines = new List<string>();
            string? warning = null;
            if (File.Exists(file))
            {
                if (new FileInfo(file).Length > MaxFileBytes) warning = $"Baseline history {key}.jsonl was larger than {MaxFileBytes / 1024 / 1024} MB and was started again.";
                else lines.AddRange(File.ReadAllLines(file, Encoding.UTF8).Where(l => l.Length > 0 && l.Length <= MaxLineChars));
            }
            HistoryEntry? previous = null;
            for (int i = lines.Count - 1; i >= 0 && previous == null; i--)
            {
                HistoryEntry? e = Parse(lines[i]);
                if (e != null && e.Status == nameof(RunStatus.Passed) && e.RunId != entry.RunId && e.ParametersKey == entry.ParametersKey
                    && string.Equals(e.ScenarioFile, entry.ScenarioFile, StringComparison.OrdinalIgnoreCase)) previous = e;
            }
            lines.Add(JsonSerializer.Serialize(entry, QaJson.Compact));
            if (lines.Count > MaxEntries) lines.RemoveRange(0, lines.Count - MaxEntries);
            temp = Path.Combine(historyDir, $"{key}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
            File.WriteAllLines(temp, lines, new UTF8Encoding(false));
            File.Move(temp, file, overwrite: true);
            temp = null;
            return (previous, warning);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return (null, $"Baseline history not written ({key}.jsonl): {e.Message}");
        }
        finally
        {
            if (temp != null)
            {
                try { File.Delete(temp); } catch (Exception) { /* best effort */ }
            }
        }
    }

    // Reads the history file without the lock (tests and tools): a reader may see the old or the new file, never half
    // of one, because writers replace it whole.
    public static IReadOnlyList<HistoryEntry> Read(string historyDir, string key)
    {
        string file = Path.Combine(historyDir, key + ".jsonl");
        if (!File.Exists(file) || new FileInfo(file).Length > MaxFileBytes) return Array.Empty<HistoryEntry>();
        return File.ReadAllLines(file, Encoding.UTF8).Select(Parse).Where(e => e != null).Select(e => e!).ToArray();
    }

    private static HistoryEntry? Parse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<HistoryEntry>(line, QaJson.Compact);
        }
        catch (JsonException)
        {
            return null;   // a damaged line is skipped (and trimmed away with time)
        }
    }

    private static FileStream? AcquireLock(string path)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (clock.Elapsed < LockWait)
            {
                // Held by the other process for its few milliseconds of read-modify-write: try again shortly. A tool
                // thread at the end of a run, not the server: a short sleep is the simplest bounded wait.
                Thread.Sleep(25);
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    // The report's Baseline table: every metric and saved value that both runs have. A metric with an explicit
    // threshold (an assert / waitFor on it) is judged by that step; one without is a Warning when it is worse (higher)
    // by more than warnPercent. Duration is shown but never warned about (it follows waits and timeouts).
    public static BaselineReport Compare(HistoryEntry current, HistoryEntry? previous, ScenarioDefinition scenario)
    {
        var report = new BaselineReport { WarnPercent = scenario.BaselineWarnPercent };
        if (previous == null)
        {
            report.Note = current.ParametersKey == null
                ? "No earlier PASSED run of this scenario in the history: this run is the first baseline."
                : "No earlier PASSED run of this scenario with the same parameters: this run is the first baseline.";
            return report;
        }
        report.PreviousRunId = previous.RunId;
        report.PreviousUtc = previous.Utc;
        report.PreviousGitCommit = previous.GitCommit;
        HashSet<string> thresholds = ThresholdPaths(scenario);

        AddRow(report, "durationMs", previous.DurationMs, current.DurationMs, "info only", warn: false);
        foreach ((string name, string[] paths) in ServerMetrics)
        {
            double? before = previous.Metrics.TryGetValue(name, out double b) ? b : null;
            double? now = current.Metrics.TryGetValue(name, out double c) ? c : null;
            bool hasThreshold = paths.Any(thresholds.Contains);
            AddRow(report, "server." + name, before, now, hasThreshold ? "assert in scenario" : $"warn > +{scenario.BaselineWarnPercent:0.#}%", !hasThreshold);
        }
        foreach (string name in scenario.BaselineValues)
        {
            double? before = Number(previous.Values, name);
            double? now = Number(current.Values, name);
            bool hasThreshold = thresholds.Contains("var." + name);
            AddRow(report, name, before, now, hasThreshold ? "assert in scenario" : $"warn > +{scenario.BaselineWarnPercent:0.#}%", !hasThreshold);
        }
        return report;
    }

    private static void AddRow(BaselineReport report, string name, double? before, double? now, string threshold, bool warn)
    {
        if (before == null && now == null) return;
        double? change = before is double b && now is double n && b != 0 ? (n - b) / Math.Abs(b) * 100.0 : null;
        bool warning = warn && change is double c && c > report.WarnPercent;
        report.Rows.Add(new BaselineRow(name, before, now, change, threshold, warning));
        if (warning)
            report.Warnings.Add($"Baseline: {name} {Fmt(before)} -> {Fmt(now)} (+{change:0.#}%, more than {report.WarnPercent:0.#}% worse than run {report.PreviousRunId}). No threshold in the scenario, so this is a warning, not a failure (§136).");
    }

    public static string Fmt(double? v) => v is double d ? d.ToString(Math.Abs(d) >= 100 ? "0.#" : "0.###", CultureInfo.InvariantCulture) : "-";

    private static double? Number(Dictionary<string, JsonElement> values, string name) =>
        values.TryGetValue(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) && double.IsFinite(d) ? d : null;

    // Paths that an assert / waitFor checks with an operator, plus `var.<saveAs>` of an assert that saved its actual
    // value (the value already has its own threshold then).
    private static HashSet<string> ThresholdPaths(ScenarioDefinition scenario)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (StepDefinition step in scenario.Steps)
        {
            if (step.Action is not ("assert" or "waitFor")) continue;
            if (!Comparison.Operators.Any(step.Has)) continue;
            string? path = (step.Params.TryGetValue("path", out JsonElement p) || step.Params.TryGetValue("condition", out p)) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            if (path != null) paths.Add(path);
            if (step.SaveAs != null) paths.Add("var." + step.SaveAs);
        }
        return paths;
    }
}

public sealed class HistoryEntry
{
    public string RunId { get; set; } = string.Empty;
    public string Utc { get; set; } = string.Empty;
    public string Scenario { get; set; } = string.Empty;
    // The scenario file (BaselineHistory.ScenarioFile): only runs of the same file are compared.
    public string ScenarioFile { get; set; } = string.Empty;
    public int Seed { get; set; }
    public JsonElement? Parameters { get; set; }
    public string? ParametersKey { get; set; }
    public string? GitCommit { get; set; }
    public bool? GitDirty { get; set; }
    public string Status { get; set; } = string.Empty;
    public long DurationMs { get; set; }
    public Dictionary<string, double> Metrics { get; set; } = new();
    public Dictionary<string, JsonElement> Values { get; set; } = new();
}

public sealed record BaselineRow(string Name, double? Previous, double? Current, double? ChangePercent, string Threshold, bool Warning);

// The report's "Baseline" section.
public sealed class BaselineReport
{
    public string? HistoryFile { get; set; }
    public bool Recorded { get; set; }
    public string? Note { get; set; }
    public string? PreviousRunId { get; set; }
    public string? PreviousUtc { get; set; }
    public string? PreviousGitCommit { get; set; }
    public double WarnPercent { get; set; }
    public List<BaselineRow> Rows { get; } = new();
    public List<string> Warnings { get; } = new();
}
