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

    public static int SampleInterval(double seconds, double? requested)
    {
        double interval = Math.Max(MinSampleSeconds, requested ?? DefaultSampleSeconds);
        interval = Math.Max(interval, Math.Ceiling(seconds / MaxSamples));
        // The server's window is whole seconds, 1-120.
        return (int)Math.Clamp(Math.Ceiling(interval), 1, 120);
    }

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
    public StressSummary? Summary { get; set; }
    public List<StallRecord> Stalls { get; } = new();
    public int StallsDropped { get; set; }
    public CrashRecord? Crash { get; set; }
    public object? Groups { get; set; }
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

    public string Line()
    {
        string F(double v) => MeasureMath.F(v);
        string latency = InputLatencyP95Ms is double l ? $"R1 p95 {F(l)} ms" : "R1 n/a";
        return $"STRESS {Phase} {F(Seconds)} s, {Players} players: tick p50 {F(TickP50Ms)} p95 {F(TickP95Ms)} p99 {F(TickP99Ms)} max {F(TickMaxMs)} ms{(TicksExact ? "" : " (approx.)")}"
            + $" | CPU {F(CpuPercent)}% | managed {F(ManagedMB)} MB, WS {F(WorkingSetMB)} MB | GC {Gen0}/{Gen1}/{Gen2}, alloc {F(AllocatedMBPerSec)} MB/s"
            + $" | send {F(SendKBps)} KB/s, recv {F(RecvKBps)} KB/s | build {BuildPieces} | DB queue {DbQueueMax} | stalls {Stalls} | {latency} | tool CPU {F(ToolCpuPercent)}%";
    }
}
