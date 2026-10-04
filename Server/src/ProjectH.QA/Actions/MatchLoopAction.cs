using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ProjectH.Shared.Protocol;

namespace ProjectH.QA;

// Soak (request §59-64): `matchLoop` repeats a match many times without copying steps. Per match: wait until the match is
// playing, one measure phase of matchSeconds (match_01... while the stress report has room; the samples are kept for the
// floor), forceMatchState finish (flow control, Arrange), wait until it is over and the next one is playing (the server's
// reset and countdown), one more /qa/metrics reading, then one MatchRow. The group workloads keep running throughout
// (re-arm, churn...); the loop does not touch them. Bounded: at most 500 matches, MatchLoopReport.MaxRows rows (the rest
// counted), and only the floors list (one double per match with a GC) grows with the matches.
public static partial class StressActions
{
    public const int MaxLoopMatches = 500;
    // A match ends by itself when the zone closes: zones.json's phases (wait + shrink) add up to 285 s after the zone
    // starts (after the drop route with AirDrop). 240 s leaves room, so the loop's finish normally ends each match; a
    // match that still ends first (last participant standing) is recorded as ended by itself.
    public const int MaxLoopMatchSeconds = 240;
    // matchLoop keeps its first phases in the report, then rows only: later measure steps (cooldown...) keep a slot.
    public const int MatchLoopKeptPhases = 150;
    internal static int KeptPhases = MatchLoopKeptPhases;   // tests lower it (one test class uses matchLoop, run in order)
    // Per match beyond matchSeconds: finish, ResultSeconds (10 by default), reset, countdown (3 in soak_match_reset) and
    // the waits. Used for the validator's scenario-timeout warning (an estimate, not measured).
    public const int MatchOverheadSeconds = 15;
    public const int DefaultTrendMatches = 5;
    public const double DefaultTrendPercent = 10;

    private static void RegisterMatchLoop(ActionRegistry r)
    {
        r.Add(new DelegateAction(new ActionSpec
        {
            Name = "matchLoop",
            Required = new[] { "matches", "matchSeconds" },
            Optional = new[] { "sampleEveryMatches", "sampleSeconds", "startTimeoutMs", "finishTimeoutMs", "trendMatches", "trendPercent" },
            DefaultTimeout = LoopTimeoutMs,
            Check = CheckMatchLoop,
        }, MatchLoopAsync));
    }

    private static IEnumerable<string> CheckMatchLoop(StepDefinition s)
    {
        if (s.Params.TryGetValue("matches", out JsonElement m) && !Variables.HasReference(m) && (!Comparison.TryNumber(m, out double d) || d < 1 || d > MaxLoopMatches || d != Math.Floor(d)))
            yield return $"'matches' must be a whole number 1-{MaxLoopMatches}.";
        foreach (string error in CheckRanges(s, ("matchSeconds", 1, MaxLoopMatchSeconds, false), ("trendMatches", 2, 100, true), ("trendPercent", 0, 1000, false),
                     ("sampleSeconds", 1, 120, false), ("startTimeoutMs", 1000, 600_000, true), ("finishTimeoutMs", 1000, 600_000, true), ("sampleEveryMatches", 1, MaxLoopMatches, true)))
            yield return error;
    }

    private static IEnumerable<string> CheckRanges(StepDefinition s, params (string Name, double Min, double Max, bool Whole)[] ranges)
    {
        foreach (var r in ranges)
        {
            if (s.Params.TryGetValue(r.Name, out JsonElement v) && !Variables.HasReference(v)
                && (!Comparison.TryNumber(v, out double d) || d < r.Min || d > r.Max || (r.Whole && d != Math.Floor(d))))
                yield return $"'{r.Name}' must be {(r.Whole ? "a whole number " : string.Empty)}{r.Min.ToString(CultureInfo.InvariantCulture)}-{r.Max.ToString(CultureInfo.InvariantCulture)}.";
        }
    }

    // The step's own budget: per match, play + two waits for a start (this one and the next after the reset) + the end
    // wait + 10 s for the finish and readings; a ${variable} counts at its bound (the scenario timeout still bounds the run).
    // The validator's estimate of the whole loop: matches x (matchSeconds + MatchOverheadSeconds), from literals or the
    // scenario's variable defaults (null when either is unknown). --set values are not seen here.
    public static double? LoopSeconds(StepDefinition s, IReadOnlyDictionary<string, JsonElement> variables)
    {
        double? Value(string name)
        {
            if (!s.Params.TryGetValue(name, out JsonElement v)) return null;
            if (Variables.HasReference(v))
            {
                string text = v.ValueKind == JsonValueKind.String ? v.GetString()! : string.Empty;
                if (!text.StartsWith("${", StringComparison.Ordinal) || !text.EndsWith('}')) return null;
                if (!variables.TryGetValue(text[2..^1], out v)) return null;
            }
            return Comparison.TryNumber(v, out double d) ? d : null;
        }
        return Value("matches") is double m && Value("matchSeconds") is double sec ? m * (sec + MatchOverheadSeconds) : null;
    }

    internal static int LoopTimeoutMs(StepDefinition s)
    {
        double matches = Seconds(s, "matches", MaxLoopMatches);
        double perMatch = Seconds(s, "matchSeconds", MaxLoopMatchSeconds) + 2 * Seconds(s, "startTimeoutMs", 30_000) / 1000 + Seconds(s, "finishTimeoutMs", 10_000) / 1000 + 10;
        return (int)Math.Min(int.MaxValue - 10_000, (matches * perMatch + 30) * 1000);
    }

    private static async Task<StepOutcome> MatchLoopAsync(StepContext ctx, CancellationToken token)
    {
        RunContext run = ctx.Run;
        int matches = ctx.Int("matches", 1, MaxLoopMatches) ?? throw new QaStepException("'matches' is required.");
        double matchSeconds = ctx.Double("matchSeconds") ?? 0;
        if (matchSeconds < 1 || matchSeconds > MaxLoopMatchSeconds) throw new QaStepException($"'matchSeconds' must be 1-{MaxLoopMatchSeconds} (a match ends by itself when the zone closes, 285 s).");
        int every = ctx.Int("sampleEveryMatches", 1, MaxLoopMatches) ?? 1;
        int startTimeout = ctx.Int("startTimeoutMs", 1000, 600_000) ?? 30_000;
        int finishTimeout = ctx.Int("finishTimeoutMs", 1000, 600_000) ?? 10_000;
        int trendMatches = ctx.Int("trendMatches", 2, 100) ?? DefaultTrendMatches;
        double trendPercent = ctx.Double("trendPercent") ?? DefaultTrendPercent;
        if (trendPercent < 0 || trendPercent > 1000) throw new QaStepException("'trendPercent' must be 0-1000.");
        var loop = new MatchLoopReport
        {
            Matches = matches, MatchSeconds = matchSeconds, SampleEveryMatches = every,
            TrendRule = $"Warning (not a failure) when the post-GC managed floor rose in each of the last {trendMatches} matches that had a GC and the last is more than {trendPercent}% above the first of them.",
        };
        run.Stress.MatchLoop = loop;
        string digits = matches >= 100 ? "D3" : "D2";
        var clock = Stopwatch.StartNew();
        var floors = new List<(int Match, double Floor)>();
        MeasureSample start;
        try
        {
            start = MeasureSample.From(await MetricsAsync(run.Server, null, token).ConfigureAwait(false), 0, 0);
        }
        catch (QaApiException e)
        {
            return StepOutcome.Fail($"matchLoop: {ServerGone(run, e)}");
        }
        AddRow(loop, Row("start", 0, true, clock, start, start, new[] { start }, null));

        try
        {
            for (int k = 1; k <= matches; k++)
            {
                JsonElement? playing = await WaitPlayingAsync(run, true, startTimeout, token).ConfigureAwait(false);
                if (playing == null)
                    return StepOutcome.Fail($"matchLoop: match {k} was not playing within {startTimeout} ms ({loop.MatchesDone} done).", "playing", "not playing", Value(loop));
                int round = RoundOf(playing.Value);
                string name = "match_" + k.ToString(digits, CultureInfo.InvariantCulture);
                bool keep = run.Stress.Phases.Count < KeptPhases;
                if (!keep) loop.PhasesNotKept++;
                PhaseRun phase = await MeasurePhaseAsync(run, name, ctx.Step.Id, matchSeconds, ctx.Double("sampleSeconds"), token, keep, warnTickBudget: false).ConfigureAwait(false);
                if (phase.Failure != null) return StepOutcome.Fail($"matchLoop {name}: {phase.Failure}", "server answering", phase.Failure, Value(loop));
                if (!phase.Result.Cancelled && phase.Result.TickMaxMs > MeasureResult.TickBudgetMs) loop.OverTickBudget++;
                // The same round still playing: finish it. Otherwise it ended by itself (zone, last participant), and the next
                // one may already have started: nothing to finish.
                FinishOutcome outcome;
                try
                {
                    outcome = await FinishRoundAsync(run, round, token).ConfigureAwait(false);
                }
                catch (QaApiException e)
                {
                    return StepOutcome.Fail($"matchLoop finishing match {k}: {ServerGone(run, e)}", "server answering", e.Message, Value(loop));
                }
                if (outcome.Error != null) return StepOutcome.Fail($"matchLoop: forceMatchState finish after match {k} refused: {outcome.Error}", "ok", outcome.Error, Value(loop));
                if (outcome.EndedNaturally) loop.EndedNaturally++;
                if (!await WaitRoundOverAsync(run, round, finishTimeout, token).ConfigureAwait(false))
                    return StepOutcome.Fail($"matchLoop: match {k} did not end within {finishTimeout} ms.", "over", "still playing", Value(loop));
                // The next match (reset, countdown) is up: the reading that closes this match's span.
                if (await WaitPlayingAsync(run, true, startTimeout, token).ConfigureAwait(false) == null)
                    return StepOutcome.Fail($"matchLoop: the match after match {k} was not playing within {startTimeout} ms.", "playing", "not playing", Value(loop));
                MeasureSample after;
                try
                {
                    after = MeasureSample.From(await MetricsAsync(run.Server, null, token).ConfigureAwait(false), clock.Elapsed.TotalSeconds, 0);
                }
                catch (QaApiException e)
                {
                    return StepOutcome.Fail($"matchLoop after match {k}: {ServerGone(run, e)}", "server answering", e.Message, Value(loop));
                }
                var span = new List<MeasureSample>(phase.Result.Samples.Count + 2) { phase.Start };
                span.AddRange(phase.Result.Samples);
                span.Add(after);
                double? floor = MatchTrend.PostGcFloor(span);
                bool milestone = MatchTrend.IsMilestone(k) || k == matches;
                if (milestone || k % every == 0 || outcome.EndedNaturally)
                {
                    MatchRow row = Row(name, k, milestone, clock, phase.Start, after, span, phase.Result);
                    row.EndedNaturally = outcome.EndedNaturally;
                    AddRow(loop, row);
                }
                loop.MatchesDone = k;
                if (floor is double f)
                {
                    floors.Add((k, f));
                    if (loop.TrendWarning == null && MatchTrend.Check(floors, trendMatches, trendPercent) is string warning)
                    {
                        loop.TrendWarning = warning;
                        run.Warnings.Add($"Stress: matchLoop {warning} (rule: {loop.TrendRule}).");
                    }
                }
            }
        }
        finally
        {
            // One warning for the whole loop instead of one per match: the run keeps 200 warnings, and the trend one must fit.
            if (loop.OverTickBudget > 0)
                run.Warnings.Add($"Stress: matchLoop: {loop.OverTickBudget} of {loop.MatchesDone} match phase(s) had a tickMax over the {MeasureResult.TickBudgetMs} ms tick budget (30 Hz; D42 warning, not a failure; see the phases and rows).");
        }
        MatchRow first = loop.Rows[0], lastRow = loop.Rows[^1];
        string text = $"{matches} matches of {matchSeconds} s: managed {MeasureMath.F(first.ManagedMB)} -> {MeasureMath.F(lastRow.ManagedMB)} MB, WS {MeasureMath.F(first.WorkingSetMB)} -> {MeasureMath.F(lastRow.WorkingSetMB)} MB, "
                      + $"{floors.Count} matches with a GC{(floors.Count > 0 ? $", post-GC floor {MeasureMath.F(floors[0].Floor)} -> {MeasureMath.F(floors[^1].Floor)} MB" : string.Empty)}"
                      + (loop.TrendWarning != null ? " — TREND WARNING" : string.Empty);
        return StepOutcome.Pass(text, Value(loop));
    }

    private static void AddRow(MatchLoopReport loop, MatchRow row)
    {
        if (loop.Rows.Count < MatchLoopReport.MaxRows) loop.Rows.Add(row);
        else loop.RowsDropped++;
    }

    private static MatchRow Row(string label, int match, bool milestone, Stopwatch clock, MeasureSample start, MeasureSample end, IReadOnlyList<MeasureSample> span, MeasureResult? phase) => new()
    {
        Label = label, Match = match, Milestone = milestone, Seconds = Math.Round(clock.Elapsed.TotalSeconds, 1),
        ManagedMB = end.ManagedMB, ManagedMinMB = span.Min(x => x.ManagedMB), PostGcFloorMB = MatchTrend.PostGcFloor(span), WorkingSetMB = end.WorkingSetMB,
        Gen0 = end.Gen0, Gen1 = end.Gen1, Gen2 = end.Gen2,
        Gen0Delta = end.Gen0 - start.Gen0, Gen1Delta = end.Gen1 - start.Gen1, Gen2Delta = end.Gen2 - start.Gen2,
        AllocatedMB = Math.Round(end.AllocatedMBTotal - start.AllocatedMBTotal, 3), GcPauseMs = Math.Round(end.GcPauseMsTotal - start.GcPauseMsTotal, 2),
        TickP95Ms = phase?.TickP95Ms ?? 0, TickP99Ms = phase?.TickP99Ms ?? 0, TickMaxMs = phase?.TickMaxMs ?? 0,
        BuildPieces = phase?.BuildPiecesEnd ?? end.BuildPieces, Sessions = end.ActiveSessions, Players = end.Players, AliveAtEnd = phase?.AliveMin ?? end.Alive,
    };

    // saveAs: the loop without the rows' bulk (first, last, the milestones, the trend).
    private static JsonElement Value(MatchLoopReport loop) => JsonPath.From(new
    {
        matches = loop.Matches, matchesDone = loop.MatchesDone, rows = loop.Rows.Count, rowsDropped = loop.RowsDropped, trendWarning = loop.TrendWarning,
        first = loop.Rows.FirstOrDefault(), last = loop.Rows.LastOrDefault(), milestones = loop.Rows.Where(r => r.Milestone).Take(20).ToArray(),
    });

    // Playing = Playing or FinalPhase (match.playing). Polls /qa/match at the run's poll interval. Returns the match that
    // satisfied the wait (null on timeout).
    private static async Task<JsonElement?> WaitPlayingAsync(RunContext run, bool playing, int timeoutMs, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                JsonElement match = await run.Server.GetMatchAsync(token).ConfigureAwait(false);
                if (IsPlaying(match) == playing) return match;
            }
            catch (QaApiException e) when (e.StatusCode is 503 or 504)
            {
            }
            if (clock.ElapsedMilliseconds >= timeoutMs) return null;
            await Task.Delay(Math.Max(50, run.PollIntervalMs), token).ConfigureAwait(false);
        }
    }

    // The round is over: not playing, or a later round (it ended and the next one already started).
    private static async Task<bool> WaitRoundOverAsync(RunContext run, int round, int timeoutMs, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                JsonElement match = await run.Server.GetMatchAsync(token).ConfigureAwait(false);
                if (!IsPlaying(match) || RoundOf(match) != round) return true;
            }
            catch (QaApiException e) when (e.StatusCode is 503 or 504)
            {
            }
            if (clock.ElapsedMilliseconds >= timeoutMs) return false;
            await Task.Delay(Math.Max(50, run.PollIntervalMs), token).ConfigureAwait(false);
        }
    }

    // /qa/match "round" (-1 when the server does not say).
    internal static int RoundOf(JsonElement match) =>
        JsonPath.Get(match, "round") is JsonElement r && Comparison.TryNumber(r, out double d) ? (int)d : -1;

    internal static bool IsPlaying(JsonElement match)
    {
        JsonElement? state = JsonPath.Get(match, "state");
        if (state == null) return false;
        string text = state.Value.ValueKind == JsonValueKind.Number && state.Value.TryGetInt32(out int n)
            ? ((MatchFlowState)n).ToString() : QaJson.Text(state.Value);
        return text.Equals("Playing", StringComparison.OrdinalIgnoreCase) || text.Equals("FinalPhase", StringComparison.OrdinalIgnoreCase);
    }

    internal readonly record struct FinishOutcome(bool EndedNaturally, string? Error);

    // forceMatchState finish for `round` (flow control, Arrange). First checks that the same round is still playing; if not,
    // the match ended by itself (EndedNaturally, nothing sent). 503 / 504 are retried twice. A 504's command may still have
    // run, so a refusal is checked against /qa/match: the round no longer playing means it is over (by an earlier attempt
    // after a 504, or by itself), not an error.
    internal static async Task<FinishOutcome> FinishRoundAsync(RunContext run, int round, CancellationToken token)
    {
        if (!await RoundPlayingAsync(run, round, token).ConfigureAwait(false)) return new FinishOutcome(true, null);
        JsonElement args = JsonPath.From(new { state = "finish" });
        bool maybeSent = false;
        for (int attempt = 0; ; attempt++)
        {
            CommandResponse response = await run.Server.CommandAsync("forceMatchState", null, args, run.RunId, token).ConfigureAwait(false);
            if (response.Ok) return new FinishOutcome(false, null);
            if (response.StatusCode is 503 or 504 && attempt < MaxRetries)
            {
                maybeSent |= response.StatusCode == 504;
                await Task.Delay(run.PollIntervalMs, token).ConfigureAwait(false);
                continue;
            }
            if (!await RoundPlayingAsync(run, round, token).ConfigureAwait(false)) return new FinishOutcome(!maybeSent, null);
            return new FinishOutcome(false, $"{response.Error} (HTTP {response.StatusCode})");
        }
    }

    private static async Task<bool> RoundPlayingAsync(RunContext run, int round, CancellationToken token)
    {
        JsonElement match = await run.Server.GetMatchAsync(token).ConfigureAwait(false);
        return IsPlaying(match) && RoundOf(match) == round;
    }
}
