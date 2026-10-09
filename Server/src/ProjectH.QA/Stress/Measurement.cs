using System.Globalization;
using System.Text.Json;

namespace ProjectH.QA;

// D37: one /qa/metrics reading during a measure phase (window = the sample interval), plus the QA tool's own CPU.
public sealed class MeasureSample
{
    public double Seconds { get; set; }            // since the phase started
    public double TickP50Ms { get; set; }
    public double TickP95Ms { get; set; }
    public double TickP99Ms { get; set; }
    public double TickMaxMs { get; set; }
    public double CpuPercent { get; set; }         // server process, all logical processors = 100 %
    public double ToolCpuPercent { get; set; }     // this QA tool process, same scale
    public double WorkingSetMB { get; set; }
    public double ManagedMB { get; set; }
    public double AllocatedMBTotal { get; set; }
    public double GcPauseMsTotal { get; set; }
    public long Gen0 { get; set; }
    public long Gen1 { get; set; }
    public long Gen2 { get; set; }
    public double PktInPerSec { get; set; }
    public double PktOutPerSec { get; set; }
    public double BytesInPerSec { get; set; }
    public double BytesOutPerSec { get; set; }
    public double QaCommandMs { get; set; }
    public int ActiveSessions { get; set; }
    public int Players { get; set; }
    public int Alive { get; set; }
    public int BuildPieces { get; set; }
    public long Stalls { get; set; }
    public long TickFailures { get; set; }
    public long BadPackets { get; set; }
    public int DbQueue { get; set; }
    public long DbSaved { get; set; }
    public long DbFailed { get; set; }

    // 기능: /qa/metrics 응답 한 건을 MeasureSample로 읽는다(없거나 숫자가 아닌 키는 0).
    // 입력: m - /qa/metrics JSON, seconds - 단계 시작 후 경과 초, toolCpuPercent - QA 도구 자체 CPU %.
    // 출력: 채워진 MeasureSample.
    // Missing keys read as 0 (an older server without the D36 keys still gives the tick numbers).
    public static MeasureSample From(JsonElement m, double seconds, double toolCpuPercent)
    {
        double N(string path) => JsonPath.Get(m, path) is JsonElement v && Comparison.TryNumber(v, out double d) && double.IsFinite(d) ? d : 0;
        return new MeasureSample
        {
            Seconds = Math.Round(seconds, 2),
            TickP50Ms = N("tickP50Ms"), TickP95Ms = N("tickP95Ms"), TickP99Ms = N("tickP99Ms"), TickMaxMs = N("tickMaxMs"),
            CpuPercent = N("cpuPercent"), ToolCpuPercent = toolCpuPercent,
            WorkingSetMB = N("workingSetMB"), ManagedMB = N("managedMB"), AllocatedMBTotal = N("allocatedMBTotal"), GcPauseMsTotal = N("gcPauseMsTotal"),
            Gen0 = (long)N("gc.gen0"), Gen1 = (long)N("gc.gen1"), Gen2 = (long)N("gc.gen2"),
            PktInPerSec = N("pktInPerSec"), PktOutPerSec = N("pktOutPerSec"), BytesInPerSec = N("bytesInPerSec"), BytesOutPerSec = N("bytesOutPerSec"),
            QaCommandMs = N("qaCommandMs"),
            ActiveSessions = (int)N("activeSessions"), Players = (int)N("players"), Alive = (int)N("alive"), BuildPieces = (int)N("buildPieces"),
            Stalls = (long)N("health.stalls"), TickFailures = (long)N("health.tickFailures"), BadPackets = (long)N("health.badPackets"),
            DbQueue = (int)N("dbQueueLength"), DbSaved = (long)N("db.saved"), DbFailed = (long)N("db.failed"),
        };
    }
}

// D37: one phase's result (warmup, steady, cooldown, match_01...). Tick percentiles come from one /qa/metrics query over
// the whole phase when it is at most the server's 120 s ring (TicksExact); a longer phase reports the worst sample
// window's p95 / p99 / max and the mean of the sample p50s, and says so (TicksNote).
public sealed class MeasureResult
{
    public string Name { get; set; } = string.Empty;
    public string? StepId { get; set; }
    public string StartedUtc { get; set; } = string.Empty;
    public double Seconds { get; set; }
    public double PlannedSeconds { get; set; }
    public double SampleSeconds { get; set; }
    public bool Cancelled { get; set; }
    public bool TicksExact { get; set; }
    public string? TicksNote { get; set; }
    public double TickP50Ms { get; set; }
    public double TickP95Ms { get; set; }
    public double TickP99Ms { get; set; }
    public double TickMaxMs { get; set; }
    public double CpuAvgPercent { get; set; }
    public double CpuMaxPercent { get; set; }
    public double ToolCpuAvgPercent { get; set; }
    public double ToolCpuCores { get; set; }       // the same, as cores in use (1.0 = one core busy)
    public double WorkingSetStartMB { get; set; }
    public double WorkingSetEndMB { get; set; }
    public double WorkingSetMaxMB { get; set; }
    public double ManagedStartMB { get; set; }
    public double ManagedEndMB { get; set; }
    public double ManagedMaxMB { get; set; }
    public long Gen0 { get; set; }
    public long Gen1 { get; set; }
    public long Gen2 { get; set; }
    public double AllocatedMB { get; set; }
    public double AllocatedMBPerSec { get; set; }
    public double GcPauseMs { get; set; }
    public double SendKBps { get; set; }           // server bytes out per second / 1024 (UDP payload)
    public double RecvKBps { get; set; }
    public double SendKBpsMax { get; set; }
    public double PktInPerSec { get; set; }
    public double PktOutPerSec { get; set; }
    public double QaCommandMsAvg { get; set; }
    public int PlayersMin { get; set; }
    public int PlayersMax { get; set; }
    public int SessionsMin { get; set; }
    public int SessionsMax { get; set; }
    public int AliveMin { get; set; }
    public int AliveMax { get; set; }
    public int BuildPiecesStart { get; set; }
    public int BuildPiecesEnd { get; set; }
    public int DbQueueMax { get; set; }
    public long DbSaved { get; set; }
    public long DbFailed { get; set; }
    public long Stalls { get; set; }
    public long TickFailures { get; set; }
    public long BadPackets { get; set; }
    public long ArrangeCommands { get; set; }      // group re-arm / arrange QA commands sent during the phase
    public LatencyStats? InputLatency { get; set; }
    public string InputLatencyNote { get; set; } = "Not Available";
    public int SamplesDropped { get; set; }
    public List<MeasureSample> Samples { get; set; } = new();

    // D42: the 30 Hz tick budget; a phase above it is a Warning (no hard limit unless the scenario asserts one).
    public const double TickBudgetMs = 33.0;
}

// The pure arithmetic of a phase (tests): the start reading (cumulative counters), the sample windows, and `whole`, one
// reading whose window is the whole phase (null when the phase is longer than the server's ring, or was cut short). The
// end of the cumulative counters is `whole`, else the last sample.
public static class MeasureMath
{
    // 기능: 단계의 시작 reading·sample 창·전체 창(whole)으로 MeasureResult의 집계 항목(tick 백분위·CPU·메모리·GC·네트워크·플레이어·DB·카운터 차이)을 채운다.
    // 입력: r - 채울 결과, start - 단계 시작 reading(누적 카운터 기준), samples - sample 창 reading들, whole - 단계 전체 창 reading(없으면 null), seconds - 단계 길이.
    // 출력: 반환값 없음. r의 집계 필드가 덮어써진다(whole이 없으면 TicksExact=false와 TicksNote).
    public static void Summarize(MeasureResult r, MeasureSample start, IReadOnlyList<MeasureSample> samples, MeasureSample? whole, double seconds)
    {
        r.Seconds = Math.Round(seconds, 2);
        MeasureSample end = whole ?? (samples.Count > 0 ? samples[^1] : start);
        IReadOnlyList<MeasureSample> all = samples.Count > 0 ? samples : new[] { end };
        if (whole != null)
        {
            r.TicksExact = true;
            r.TicksNote = null;
            r.TickP50Ms = whole.TickP50Ms;
            r.TickP95Ms = whole.TickP95Ms;
            r.TickP99Ms = whole.TickP99Ms;
            r.TickMaxMs = whole.TickMaxMs;
        }
        else
        {
            r.TicksExact = false;
            r.TicksNote = samples.Count == 0
                ? "No sample window: the phase ended before its first sample."
                : $"No whole-phase window (longer than the server's 120 s tick ring, or cut short): p50 is the mean of the {all.Count} sample windows' p50, p95/p99/max the worst sample window's (approximate; p95/p99 never better than the truth).";
            r.TickP50Ms = all.Average(s => s.TickP50Ms);
            r.TickP95Ms = all.Max(s => s.TickP95Ms);
            r.TickP99Ms = all.Max(s => s.TickP99Ms);
            r.TickMaxMs = all.Max(s => s.TickMaxMs);
        }
        r.CpuAvgPercent = whole?.CpuPercent ?? all.Average(s => s.CpuPercent);
        r.CpuMaxPercent = all.Max(s => s.CpuPercent);
        r.ToolCpuAvgPercent = whole?.ToolCpuPercent ?? all.Average(s => s.ToolCpuPercent);
        r.ToolCpuCores = r.ToolCpuAvgPercent / 100.0 * Environment.ProcessorCount;
        r.WorkingSetStartMB = start.WorkingSetMB;
        r.WorkingSetEndMB = end.WorkingSetMB;
        r.WorkingSetMaxMB = Math.Max(Math.Max(start.WorkingSetMB, end.WorkingSetMB), all.Max(s => s.WorkingSetMB));
        r.ManagedStartMB = start.ManagedMB;
        r.ManagedEndMB = end.ManagedMB;
        r.ManagedMaxMB = Math.Max(Math.Max(start.ManagedMB, end.ManagedMB), all.Max(s => s.ManagedMB));
        r.Gen0 = end.Gen0 - start.Gen0;
        r.Gen1 = end.Gen1 - start.Gen1;
        r.Gen2 = end.Gen2 - start.Gen2;
        r.AllocatedMB = Math.Max(0, end.AllocatedMBTotal - start.AllocatedMBTotal);
        r.AllocatedMBPerSec = seconds > 0 ? r.AllocatedMB / seconds : 0;
        r.GcPauseMs = Math.Max(0, end.GcPauseMsTotal - start.GcPauseMsTotal);
        r.SendKBps = (whole?.BytesOutPerSec ?? all.Average(s => s.BytesOutPerSec)) / 1024.0;
        r.RecvKBps = (whole?.BytesInPerSec ?? all.Average(s => s.BytesInPerSec)) / 1024.0;
        r.SendKBpsMax = all.Max(s => s.BytesOutPerSec) / 1024.0;
        r.PktInPerSec = whole?.PktInPerSec ?? all.Average(s => s.PktInPerSec);
        r.PktOutPerSec = whole?.PktOutPerSec ?? all.Average(s => s.PktOutPerSec);
        r.QaCommandMsAvg = whole?.QaCommandMs ?? all.Average(s => s.QaCommandMs);
        r.PlayersMin = all.Min(s => s.Players);
        r.PlayersMax = all.Max(s => s.Players);
        r.SessionsMin = all.Min(s => s.ActiveSessions);
        r.SessionsMax = all.Max(s => s.ActiveSessions);
        r.AliveMin = all.Min(s => s.Alive);
        r.AliveMax = all.Max(s => s.Alive);
        r.BuildPiecesStart = start.BuildPieces;
        r.BuildPiecesEnd = end.BuildPieces;
        r.DbQueueMax = Math.Max(start.DbQueue, all.Max(s => s.DbQueue));
        r.DbSaved = end.DbSaved - start.DbSaved;
        r.DbFailed = end.DbFailed - start.DbFailed;
        r.Stalls = end.Stalls - start.Stalls;
        r.TickFailures = end.TickFailures - start.TickFailures;
        r.BadPackets = end.BadPackets - start.BadPackets;
    }

    // Samples to take: one per sampleSeconds, at most MaxSamples (a longer phase gets a longer interval).
    public const int MaxSamples = 720;
    public const double MinSampleSeconds = 1;
    public const double DefaultSampleSeconds = 5;

    // 기능: 단계 길이와 요청값으로 sample 간격을 정한다(최소 MinSampleSeconds, 최대 MaxSamples개가 되도록 늘림).
    // 입력: seconds - 단계 길이, requested - 시나리오가 요청한 간격(null이면 DefaultSampleSeconds).
    // 출력: 정수 초 간격(서버 창 범위 1..120).
    public static int SampleInterval(double seconds, double? requested)
    {
        double interval = Math.Max(MinSampleSeconds, requested ?? DefaultSampleSeconds);
        interval = Math.Max(interval, Math.Ceiling(seconds / MaxSamples));
        // The server's window is whole seconds, 1-120.
        return (int)Math.Clamp(Math.Ceiling(interval), 1, 120);
    }

    // 기능: 숫자를 보고용 문자열로 만든다(절대값 100 이상은 소수 1자리, 그 외 3자리, InvariantCulture).
    // 입력: v - 숫자.
    // 출력: 포맷된 문자열.
    public static string F(double v) => v.ToString(Math.Abs(v) >= 100 ? "0.#" : "0.###", CultureInfo.InvariantCulture);
}

// D41: a stall the server counted during a measure phase, with what was going on.
public sealed class StallRecord
{
    public string Utc { get; set; } = string.Empty;
    public string Phase { get; set; } = string.Empty;
    public string? StepId { get; set; }
    public long StallsBefore { get; set; }
    public long StallsAfter { get; set; }
    public int Players { get; set; }
    public int Actors { get; set; }
    public MeasureSample? Sample { get; set; }
    public List<JsonElement> RecentEvents { get; set; } = new();
    public string? EventsNote { get; set; }
}

// D41: the launched server died during the run (not a scenario's stopServer / killServer).
public sealed class CrashRecord
{
    public string Utc { get; set; } = string.Empty;
    public int? ExitCode { get; set; }
    public string? StepId { get; set; }
    public int Actors { get; set; }
    public int ActorsJoined { get; set; }
    public MeasureSample? LastSample { get; set; }
    public string? LastPhase { get; set; }
    public string[] LogTail { get; set; } = Array.Empty<string>();
}

// The report's stress part: every measure phase, the summary (the "steady" phase, else the last), stalls, a crash.
public sealed class StressReport
{
    public const int MaxPhases = 200;
    public const int MaxStalls = 50;

    public bool StressMode { get; set; }
    public List<MeasureResult> Phases { get; } = new();
    // Phases measured after Phases was full (MaxPhases): not in the report, counted (shown in report.html).
    public int PhasesDropped { get; set; }
    public StressSummary? Summary { get; set; }
    public List<StallRecord> Stalls { get; } = new();
    public int StallsDropped { get; set; }
    public CrashRecord? Crash { get; set; }
    public object? Groups { get; set; }
    // matchLoop (soak, request §59-64): one row per match end; null when the run had no match loop.
    public MatchLoopReport? MatchLoop { get; set; }
}

// One matchLoop row: the server at the end of a match (after its reset), or "start" before the first one. Managed and
// working set from /qa/metrics (GC.GetTotalMemory(false) and the process working set). PostGcFloorMB: the lowest managed
// reading in this match's span (its measure samples and the reading after the reset) taken after a GC happened in that
// span (the cumulative gen0+1+2 count went up); null when no GC happened in the span.
public sealed class MatchRow
{
    public string Label { get; set; } = string.Empty;
    public int Match { get; set; }
    public bool Milestone { get; set; }
    // The match ended by itself (zone, last participant) before the loop's finish: no forceMatchState was needed.
    public bool EndedNaturally { get; set; }
    public double Seconds { get; set; }            // since the loop started
    public double ManagedMB { get; set; }
    public double ManagedMinMB { get; set; }
    public double? PostGcFloorMB { get; set; }
    public double WorkingSetMB { get; set; }
    public long Gen0 { get; set; }                 // cumulative, at the row
    public long Gen1 { get; set; }
    public long Gen2 { get; set; }
    public long Gen0Delta { get; set; }            // in this match's span
    public long Gen1Delta { get; set; }
    public long Gen2Delta { get; set; }
    public double AllocatedMB { get; set; }        // in this match's span
    public double GcPauseMs { get; set; }
    public double TickP95Ms { get; set; }          // the match's measure phase
    public double TickP99Ms { get; set; }
    public double TickMaxMs { get; set; }
    public int BuildPieces { get; set; }           // at the end of play (the reset clears them)
    public int Sessions { get; set; }
    public int Players { get; set; }
    public int AliveAtEnd { get; set; }
}

public sealed class MatchLoopReport
{
    public const int MaxRows = 520;                // 500 matches + "start" + room; the rest are counted
    public int Matches { get; set; }
    public int MatchesDone { get; set; }
    public double MatchSeconds { get; set; }
    public int SampleEveryMatches { get; set; }
    public List<MatchRow> Rows { get; } = new();
    public int RowsDropped { get; set; }
    public string TrendRule { get; set; } = string.Empty;
    public int EndedNaturally { get; set; }
    public int PhasesNotKept { get; set; }       // match phases measured but not kept in the report (after MatchLoopKeptPhases)
    public int OverTickBudget { get; set; }      // matches whose phase tickMax was over 33 ms (one summary warning)
    public string? TrendWarning { get; set; }
}

// matchLoop's pure parts (tests): milestones, a match span's post-GC floor and the trend rule.
public static class MatchTrend
{
    // 기능: 보고서에 줄을 남길 milestone 매치 번호인지 판정한다(1, 5, 그리고 10·25·50 x 10^n).
    // 입력: match - 매치 번호.
    // 출력: milestone이면 true.
    // "after match 1/5/10/25/50/100/250/500/1000..." (1, 5, then 10, 25, 50 x powers of ten... 2.5 and 5 steps).
    public static bool IsMilestone(int match)
    {
        if (match is 1 or 5) return true;
        for (long p = 10; p <= 1_000_000; p *= 10)
        {
            if (match == p || match == p * 25 / 10 || match == p * 5) return true;
        }
        return false;
    }

    // 기능: 구간 reading들 중 첫 reading보다 GC 횟수가 늘어난 reading의 최소 managed MB를 구한다.
    // 입력: span - 시간순 reading(첫 항목이 구간 시작).
    // 출력: post-GC 최저 managed MB. reading이 2개 미만이거나 구간에 GC가 없었으면 null.
    // The lowest managed reading after the first GC in the span (readings in time order; the first is the span's start).
    public static double? PostGcFloor(IReadOnlyList<MeasureSample> span)
    {
        if (span.Count < 2) return null;
        long before = Gc(span[0]);
        double? floor = null;
        for (int i = 1; i < span.Count; i++)
        {
            if (Gc(span[i]) <= before) continue;
            if (floor == null || span[i].ManagedMB < floor) floor = span[i].ManagedMB;
        }
        return floor;
    }

    // 기능: sample의 누적 GC 횟수(gen0+gen1+gen2)를 더한다.
    // 입력: s - sample.
    // 출력: 합계.
    private static long Gc(MeasureSample s) => s.Gen0 + s.Gen1 + s.Gen2;

    // 기능: 마지막 count개의 post-GC floor가 매번 오르고 첫 값 대비 percent %를 넘게 올랐으면 경고 문장을 만든다.
    // 입력: floors - (매치 번호, floor MB) 시간순 목록, count - 볼 마지막 매치 수, percent - 경고 기준 상승률(%).
    // 출력: 경고 문자열. 조건에 안 맞으면(count<2, 자료 부족, 첫 값 0 이하 포함) null.
    // D42-style warning rule (not a failure): the last `count` post-GC floors (matches without a GC skipped) each higher
    // than the one before, and the last more than `percent` % above the first of them. Null = no warning.
    public static string? Check(IReadOnlyList<(int Match, double Floor)> floors, int count, double percent)
    {
        if (count < 2 || floors.Count < count) return null;
        var last = floors.Skip(floors.Count - count).ToArray();
        for (int i = 1; i < last.Length; i++)
            if (!(last[i].Floor > last[i - 1].Floor)) return null;
        double first = last[0].Floor;
        if (first <= 0) return null;
        double rise = (last[^1].Floor - first) / first * 100.0;
        if (rise <= percent) return null;
        return $"post-GC managed floor rose in each of the last {count} matches with a GC (match {last[0].Match} {MeasureMath.F(first)} MB -> match {last[^1].Match} {MeasureMath.F(last[^1].Floor)} MB, +{MeasureMath.F(rise)}% > {MeasureMath.F(percent)}%)";
    }
}

// §103, D40: the line the report header, the console and the batch table show.
public sealed class StressSummary
{
    public string Phase { get; set; } = string.Empty;
    public double Seconds { get; set; }
    public int Players { get; set; }
    public double TickP50Ms { get; set; }
    public double TickP95Ms { get; set; }
    public double TickP99Ms { get; set; }
    public double TickMaxMs { get; set; }
    public bool TicksExact { get; set; }
    public double CpuPercent { get; set; }
    public double ToolCpuPercent { get; set; }
    public double ManagedMB { get; set; }
    public double WorkingSetMB { get; set; }
    public long Gen0 { get; set; }
    public long Gen1 { get; set; }
    public long Gen2 { get; set; }
    public double AllocatedMBPerSec { get; set; }
    public double SendKBps { get; set; }
    public double RecvKBps { get; set; }
    public int DbQueueMax { get; set; }
    public int BuildPieces { get; set; }
    public long Stalls { get; set; }
    public double? InputLatencyP95Ms { get; set; }
    public string Result { get; set; } = string.Empty;

    // 기능: 요약에 쓸 단계를 고른다("steady"가 있으면 그 마지막, 없으면 PlannedSeconds가 가장 긴 단계 중 마지막).
    // 입력: phases - 측정 단계들.
    // 출력: 고른 단계. 단계가 없으면 null.
    // The judged phase: "steady"; without one, the longest phase (the last of equally long ones: soak, match_10).
    public static MeasureResult? Pick(IReadOnlyList<MeasureResult> phases)
    {
        MeasureResult? steady = phases.LastOrDefault(x => string.Equals(x.Name, "steady", StringComparison.OrdinalIgnoreCase));
        if (steady != null) return steady;
        MeasureResult? best = null;
        foreach (MeasureResult x in phases)
        {
            if (best == null || x.PlannedSeconds >= best.PlannedSeconds) best = x;
        }
        return best;
    }

    // 기능: 고른 단계의 수치로 StressSummary를 만든다(Stalls는 모든 단계의 합).
    // 입력: phases - 측정 단계들, result - run 결과 문자열.
    // 출력: StressSummary. 단계가 없으면 null.
    public static StressSummary? From(IReadOnlyList<MeasureResult> phases, string result)
    {
        MeasureResult? p = Pick(phases);
        if (p == null) return null;
        return new StressSummary
        {
            Phase = p.Name, Seconds = p.Seconds, Players = p.PlayersMax, TickP50Ms = p.TickP50Ms, TickP95Ms = p.TickP95Ms, TickP99Ms = p.TickP99Ms,
            TickMaxMs = p.TickMaxMs, TicksExact = p.TicksExact, CpuPercent = p.CpuAvgPercent, ToolCpuPercent = p.ToolCpuAvgPercent,
            ManagedMB = p.ManagedEndMB, WorkingSetMB = p.WorkingSetMaxMB, Gen0 = p.Gen0, Gen1 = p.Gen1, Gen2 = p.Gen2,
            AllocatedMBPerSec = p.AllocatedMBPerSec, SendKBps = p.SendKBps, RecvKBps = p.RecvKBps, DbQueueMax = p.DbQueueMax,
            BuildPieces = p.BuildPiecesEnd, Stalls = phases.Sum(x => x.Stalls), InputLatencyP95Ms = p.InputLatency?.P95Ms, Result = result,
        };
    }

    // 기능: 요약 한 줄("STRESS ...")을 만든다.
    // 입력: 없음.
    // 출력: 콘솔·보고서 헤더·배치 표용 문자열.
    public string Line()
    {
        string F(double v) => MeasureMath.F(v);
        string latency = InputLatencyP95Ms is double l ? $"R1 p95 {F(l)} ms" : "R1 n/a";
        return $"STRESS {Phase} {F(Seconds)} s, {Players} players: tick p50 {F(TickP50Ms)} p95 {F(TickP95Ms)} p99 {F(TickP99Ms)} max {F(TickMaxMs)} ms{(TicksExact ? "" : " (approx.)")}"
            + $" | CPU {F(CpuPercent)}% | managed {F(ManagedMB)} MB, WS {F(WorkingSetMB)} MB | GC {Gen0}/{Gen1}/{Gen2}, alloc {F(AllocatedMBPerSec)} MB/s"
            + $" | send {F(SendKBps)} KB/s, recv {F(RecvKBps)} KB/s | build {BuildPieces} | DB queue {DbQueueMax} | stalls {Stalls} | {latency} | tool CPU {F(ToolCpuPercent)}%";
    }
}
