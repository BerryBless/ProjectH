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

    // Stress D42 (request §75, §79): the judged phase's numbers. Warn = compared like a metric without a threshold
    // (Warning when worse by more than baselineWarnPercent, never a failure); the others are shown for information.
    public static readonly (string Name, bool Warn, Func<StressSummary, double?> Value)[] StressMetrics =
    {
        ("tickP50Ms", false, s => s.TickP50Ms),
        ("tickP95Ms", true, s => s.TickP95Ms),
        ("tickP99Ms", true, s => s.TickP99Ms),
        ("tickMaxMs", false, s => s.TickMaxMs),
        ("cpuPercent", true, s => s.CpuPercent),
        ("managedMB", true, s => s.ManagedMB),
        ("workingSetMB", true, s => s.WorkingSetMB),
        ("sendKBps", true, s => s.SendKBps),
        ("recvKBps", false, s => s.RecvKBps),
        ("allocatedMBPerSec", false, s => s.AllocatedMBPerSec),
        ("inputLatencyP95Ms", false, s => s.InputLatencyP95Ms),
    };

    [GeneratedRegex(@"[^A-Za-z0-9_\-]")]
    private static partial Regex Unsafe();

    // 기능: 히스토리 줄에 기록할 시나리오 파일 표기를 만든다.
    // 입력: repoRoot - 저장소 루트, scenarioPath - 시나리오 파일 경로.
    // 출력: QA/Scenarios 아래면 '/' 구분의 상대 경로, 밖이면 절대 경로.
    // The scenario file as recorded in every history line: relative to QA/Scenarios with '/' ("Stress/load_bots_10.json"),
    // or the full path for a file outside QA/Scenarios.
    public static string ScenarioFile(string repoRoot, string scenarioPath)
    {
        string full = Path.GetFullPath(scenarioPath);
        string scenarios = Path.GetFullPath(Path.Combine(repoRoot, "QA", "Scenarios")) + Path.DirectorySeparatorChar;
        return full.StartsWith(scenarios, StringComparison.OrdinalIgnoreCase) ? full[scenarios.Length..].Replace('\\', '/') : full;
    }

    // 기능: 시나리오 파일의 히스토리 파일 키(읽을 수 있는 부분 + 경로 해시 8자)를 만든다.
    // 입력: repoRoot - 저장소 루트, scenarioPath - 시나리오 파일 경로.
    // 출력: 파일 이름으로 안전한 키 문자열(최대 110자 + "_" + 해시).
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

    // 기능: 파라미터 세트를 비교용 키로 만든다(같은 compact JSON이면 같은 세트).
    // 입력: parameters - 실행에 쓴 파라미터 세트.
    // 출력: 512자 이하면 compact JSON, 길면 "sha256:<해시>", 파라미터가 없으면 null.
    // Same parameter set = same canonical (compact) JSON; long sets compare by hash.
    public static string? ParametersKey(JsonElement? parameters)
    {
        if (parameters == null) return null;
        string text = JsonSerializer.Serialize(parameters.Value, QaJson.Compact);
        return text.Length <= 512 ? text : "sha256:" + Hash(text);
    }

    // 기능: 텍스트의 SHA-256 해시를 구한다.
    // 입력: text - 해시할 문자열.
    // 출력: 소문자 16진수 64자.
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    // 기능: 끝난 실행의 보고서에서 히스토리 한 줄(실행 정보·서버 지표·스트레스 지표·baselineValues 변수)을 만든다.
    // 입력: report - 실행 보고서, scenario - 시나리오 정의, scenarioFile - ScenarioFile로 만든 파일 표기, variables - 실행이 끝났을 때의 변수.
    // 출력: 기록할 HistoryEntry. 스칼라 값만 담고 문자열은 MaxStringValueChars로 자른다.
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
        if (report.Stress?.Summary is StressSummary stress)
        {
            entry.StressPhase = stress.Phase;
            foreach ((string name, _, Func<StressSummary, double?> value) in StressMetrics)
            {
                if (value(stress) is double d && double.IsFinite(d)) entry.Stress[name] = Math.Round(d, 4);
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

    // 기능: 파일 락 아래에서 히스토리를 읽어 entry를 덧붙이고 마지막 MaxEntries줄만 남겨 임시 파일로 바꿔치기한다.
    // 입력: historyDir - 히스토리 폴더, key - 시나리오 키, entry - 기록할 줄.
    // 출력: 같은 파일·같은 파라미터 세트의 가장 최근 PASSED 항목(없으면 null)과 경고 문자열(락 실패·IO 오류·파일 재시작 시, 아니면 null). 예외는 던지지 않는다.
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

    // 기능: 히스토리 파일을 락 없이 읽어 파싱되는 줄만 모은다.
    // 입력: historyDir - 히스토리 폴더, key - 시나리오 키.
    // 출력: 항목 목록. 파일이 없거나 MaxFileBytes보다 크면 빈 목록.
    // Reads the history file without the lock (tests and tools): a reader may see the old or the new file, never half
    // of one, because writers replace it whole.
    public static IReadOnlyList<HistoryEntry> Read(string historyDir, string key)
    {
        string file = Path.Combine(historyDir, key + ".jsonl");
        if (!File.Exists(file) || new FileInfo(file).Length > MaxFileBytes) return Array.Empty<HistoryEntry>();
        return File.ReadAllLines(file, Encoding.UTF8).Select(Parse).Where(e => e != null).Select(e => e!).ToArray();
    }

    // 기능: 히스토리 한 줄을 역직렬화한다.
    // 입력: line - JSON 한 줄.
    // 출력: HistoryEntry. 깨진 줄이면 null.
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

    // 기능: .lock 파일을 FileShare.None으로 열어 OS 파일 락을 잡는다. 다른 프로세스가 쥐고 있으면 LockWait 동안 25ms 간격으로 재시도한다.
    // 입력: path - 락 파일 경로.
    // 출력: 잡았으면 열린 FileStream(Dispose가 해제), 제한 시간 안에 못 잡으면 null.
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

    // 기능: 이번 실행과 이전 PASSED 실행의 소요 시간·서버 지표·스트레스 지표·baselineValues를 행으로 비교한다.
    // 입력: current - 이번 실행 항목, previous - 비교할 이전 항목(null이면 첫 기준), scenario - 시나리오 정의(경고 비율·임계값 판단).
    // 출력: Baseline 보고서. 이전 항목이 없으면 Note만 채운다. 임계값 없는 지표가 WarnPercent보다 나빠지면 Warnings에 추가된다.
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
        foreach ((string name, bool warn, _) in StressMetrics)
        {
            double? before = previous.Stress.TryGetValue(name, out double b) ? b : null;
            double? now = current.Stress.TryGetValue(name, out double c) ? c : null;
            AddRow(report, $"stress.{name} ({current.StressPhase ?? previous.StressPhase})", before, now, warn ? $"warn > +{scenario.BaselineWarnPercent:0.#}%" : "info only", warn);
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

    // 기능: 비교 행 하나를 보고서에 넣고, 경고 대상이면 변화율이 WarnPercent를 넘는지 판정한다.
    // 입력: report - 채울 보고서, name - 지표 이름, before - 이전 값, now - 이번 값, threshold - 임계값 설명 텍스트, warn - 경고 판정 대상인지.
    // 출력: 반환값 없음. 둘 다 null이면 아무것도 넣지 않고, 경고면 Rows와 Warnings에 추가된다.
    private static void AddRow(BaselineReport report, string name, double? before, double? now, string threshold, bool warn)
    {
        if (before == null && now == null) return;
        double? change = before is double b && now is double n && b != 0 ? (n - b) / Math.Abs(b) * 100.0 : null;
        bool warning = warn && change is double c && c > report.WarnPercent;
        report.Rows.Add(new BaselineRow(name, before, now, change, threshold, warning));
        if (warning)
            report.Warnings.Add($"Baseline: {name} {Fmt(before)} -> {Fmt(now)} (+{change:0.#}%, more than {report.WarnPercent:0.#}% worse than run {report.PreviousRunId}). No threshold in the scenario, so this is a warning, not a failure (§136).");
    }

    // 기능: 지표 값을 표시용 문자열로 만든다.
    // 입력: v - 지표 값.
    // 출력: 100 이상이면 소수 1자리, 아니면 3자리까지. null이면 "-".
    public static string Fmt(double? v) => v is double d ? d.ToString(Math.Abs(d) >= 100 ? "0.#" : "0.###", CultureInfo.InvariantCulture) : "-";

    // 기능: 저장된 변수 값 중 유한한 숫자만 읽는다.
    // 입력: values - 항목의 변수 사전, name - 변수 이름.
    // 출력: 숫자 값. 없거나 숫자가 아니면 null.
    private static double? Number(Dictionary<string, JsonElement> values, string name) =>
        values.TryGetValue(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) && double.IsFinite(d) ? d : null;

    // 기능: 시나리오에서 연산자로 검사되는 assert / waitFor의 경로와 그 단계의 saveAs("var.<이름>")를 모은다.
    // 입력: scenario - 시나리오 정의.
    // 출력: 자체 임계값이 있는 경로 집합(대소문자 무시).
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
    // Stress: the judged phase's name and numbers (BaselineHistory.StressMetrics); empty for other runs.
    public string? StressPhase { get; set; }
    public Dictionary<string, double> Stress { get; set; } = new();
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
