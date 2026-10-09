using System.Net;
using System.Text;
using System.Text.Json;

namespace ProjectH.QA;

// The report's stress parts (§103, D37, D41): the Stress Summary under the header, then one row per measure phase, the
// samples of each phase (collapsed), stalls, a crash and the groups. Plain HTML (details/summary, no script).
public static class StressReportHtml
{
    // 기능: 숫자를 보고용 문자열로 만든다(MeasureMath.F 위임).
    // 입력: v - 숫자.
    // 출력: 포맷된 문자열.
    private static string F(double v) => MeasureMath.F(v);
    // 기능: 텍스트를 HTML escape한다.
    // 입력: text - 원문.
    // 출력: escape된 문자열.
    private static string E(string text) => WebUtility.HtmlEncode(text);

    // 기능: Stress Summary 절(크래시 안내, 판정 단계의 한 줄 표)을 HTML로 덧붙인다.
    // 입력: sb - 출력 버퍼, s - stress 보고.
    // 출력: 반환값 없음. sb에 HTML이 추가된다.
    public static void Summary(StringBuilder sb, StressReport s)
    {
        sb.Append("<h2>Stress Summary</h2>");
        if (s.Crash != null)
            sb.Append("<p class=\"fail\"><b>The server crashed</b> during step ").Append(E(s.Crash.StepId ?? "?")).Append(" (exit code ")
              .Append(E(s.Crash.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown")).Append("). See Crash below.</p>");
        StressSummary? m = s.Summary;
        if (m == null)
        {
            sb.Append("<p class=\"muted\">No measure phase ran.</p>");
            return;
        }
        sb.Append("<p class=\"muted\">From phase <b>").Append(E(m.Phase)).Append("</b> (the \"steady\" phase, else the longest one). Server CPU: all logical processors = 100 %. KB = 1024 bytes of UDP payload.")
          .Append(m.TicksExact ? string.Empty : " Tick percentiles are approximate (sample windows).").Append("</p>");
        sb.Append("<table><tr><th>Result</th><th>Players</th><th>Seconds</th><th>Tick p50</th><th>p95</th><th>p99</th><th>max (ms)</th><th>CPU %</th><th>Managed MB</th><th>Working set MB</th>")
          .Append("<th>GC 0/1/2</th><th>Alloc MB/s</th><th>Send KB/s</th><th>Recv KB/s</th><th>DB queue max</th><th>Build pieces</th><th>Stalls</th><th>R1 p95 (ms)</th><th>QA tool CPU %</th></tr>");
        sb.Append("<tr><td class=\"").Append(E(m.Result)).Append("\">").Append(E(m.Result)).Append("</td><td>").Append(m.Players).Append("</td><td>").Append(F(m.Seconds))
          .Append("</td><td>").Append(F(m.TickP50Ms)).Append("</td><td>").Append(F(m.TickP95Ms)).Append("</td><td>").Append(F(m.TickP99Ms)).Append("</td><td>").Append(F(m.TickMaxMs))
          .Append(m.TickMaxMs > MeasureResult.TickBudgetMs ? " <b>(over 33)</b>" : string.Empty)
          .Append("</td><td>").Append(F(m.CpuPercent)).Append("</td><td>").Append(F(m.ManagedMB)).Append("</td><td>").Append(F(m.WorkingSetMB))
          .Append("</td><td>").Append(m.Gen0).Append('/').Append(m.Gen1).Append('/').Append(m.Gen2).Append("</td><td>").Append(F(m.AllocatedMBPerSec))
          .Append("</td><td>").Append(F(m.SendKBps)).Append("</td><td>").Append(F(m.RecvKBps)).Append("</td><td>").Append(m.DbQueueMax).Append("</td><td>").Append(m.BuildPieces)
          .Append("</td><td>").Append(m.Stalls).Append("</td><td>").Append(m.InputLatencyP95Ms is double l ? F(l) : "Not Available").Append("</td><td>").Append(F(m.ToolCpuPercent))
          .Append("</td></tr></table>");
    }

    // 기능: matchLoop 추세 절(요약 문단, 경고, milestone 행 표, 전체 행의 접이식 표)을 덧붙인다.
    // 입력: sb - 출력 버퍼, loop - matchLoop 보고.
    // 출력: 반환값 없음. sb에 HTML이 추가된다.
    // matchLoop (soak): the trend over matches. The table shows "start", the milestones (after match 1/5/10/25/50/100...)
    // and the last match; every recorded row is in the collapsed table below it.
    public static void MatchLoop(StringBuilder sb, MatchLoopReport loop)
    {
        sb.Append("<h2>Match loop trend</h2><p class=\"muted\">").Append(loop.MatchesDone).Append(" of ").Append(loop.Matches).Append(" matches of ").Append(F(loop.MatchSeconds))
          .Append(" s; ").Append(loop.EndedNaturally).Append(" ended by themselves before the finish; ").Append(loop.PhasesNotKept).Append(" match phases not kept in the phase table (rows only); ")
          .Append(loop.OverTickBudget).Append(" over the 33 ms tick budget. Post-GC floor = the lowest managed MB in a match's span after a GC in that span (empty: no GC). ").Append(E(loop.TrendRule)).Append("</p>");
        if (loop.TrendWarning != null) sb.Append("<p class=\"fail\"><b>Trend warning:</b> ").Append(E(loop.TrendWarning)).Append("</p>");
        IEnumerable<MatchRow> compact = loop.Rows.Where(r => r.Milestone || ReferenceEquals(r, loop.Rows[^1]));
        Rows(sb, compact);
        if (loop.Rows.Count > 0)
        {
            sb.Append("<details><summary>All ").Append(loop.Rows.Count).Append(" rows").Append(loop.RowsDropped > 0 ? $" ({loop.RowsDropped} more not kept)" : string.Empty).Append("</summary>");
            Rows(sb, loop.Rows);
            sb.Append("</details>");
        }
    }

    // 기능: MatchRow들을 표 하나로 덧붙인다.
    // 입력: sb - 출력 버퍼, rows - 표에 넣을 행.
    // 출력: 반환값 없음. sb에 HTML이 추가된다.
    private static void Rows(StringBuilder sb, IEnumerable<MatchRow> rows)
    {
        sb.Append("<table><tr><th>Row</th><th>s</th><th>Post-GC floor MB</th><th>Managed end / min MB</th><th>WS MB</th><th>GC 0/1/2 in match</th><th>GC 0/1/2 total</th><th>Alloc MB</th><th>GC pause ms</th>")
          .Append("<th>Tick p95 / p99 / max</th><th>Build pieces</th><th>Sessions</th><th>Players</th><th>Alive at end</th></tr>");
        foreach (MatchRow r in rows)
        {
            sb.Append("<tr><td>").Append(E(r.Label)).Append(r.EndedNaturally ? " (ended by itself)" : string.Empty).Append("</td><td>").Append(F(r.Seconds)).Append("</td><td>").Append(r.PostGcFloorMB is double f ? F(f) : "-")
              .Append("</td><td>").Append(F(r.ManagedMB)).Append(" / ").Append(F(r.ManagedMinMB)).Append("</td><td>").Append(F(r.WorkingSetMB))
              .Append("</td><td>").Append(r.Gen0Delta).Append('/').Append(r.Gen1Delta).Append('/').Append(r.Gen2Delta)
              .Append("</td><td>").Append(r.Gen0).Append('/').Append(r.Gen1).Append('/').Append(r.Gen2)
              .Append("</td><td>").Append(F(r.AllocatedMB)).Append("</td><td>").Append(F(r.GcPauseMs))
              .Append("</td><td>").Append(F(r.TickP95Ms)).Append(" / ").Append(F(r.TickP99Ms)).Append(" / ").Append(F(r.TickMaxMs))
              .Append("</td><td>").Append(r.BuildPieces).Append("</td><td>").Append(r.Sessions).Append("</td><td>").Append(r.Players).Append("</td><td>").Append(r.AliveAtEnd).Append("</td></tr>");
        }
        sb.Append("</table>");
    }

    // 기능: 보고서 하단의 stress 상세(matchLoop, 측정 단계 표, 단계별 sample 접이식 표, stall, crash, groups)를 덧붙인다.
    // 입력: sb - 출력 버퍼, s - stress 보고.
    // 출력: 반환값 없음. sb에 HTML이 추가된다.
    public static void Details(StringBuilder sb, StressReport s)
    {
        if (s.MatchLoop != null) MatchLoop(sb, s.MatchLoop);
        sb.Append("<h2>Measure phases</h2>");
        if (s.PhasesDropped > 0) sb.Append("<p class=\"fail\">").Append(s.PhasesDropped).Append(" more phase(s) were measured after the report's limit of ").Append(StressReport.MaxPhases).Append(" phases and are not listed.</p>");
        if (s.Phases.Count == 0) sb.Append("<p class=\"muted\">None.</p>");
        else
        {
            sb.Append("<table><tr><th>Phase</th><th>Step</th><th>s</th><th>Players min-max</th><th>Alive min-max</th><th>Tick p50</th><th>p95</th><th>p99</th><th>max</th><th>CPU avg/max %</th>")
              .Append("<th>WS start/end/max MB</th><th>Managed start/end/max MB</th><th>GC 0/1/2</th><th>Alloc MB (MB/s)</th><th>GC pause ms</th><th>Send / Recv KB/s</th><th>pkt in/out /s</th>")
              .Append("<th>QA ms/tick</th><th>Build start-end</th><th>DB queue max, saved, failed</th><th>Stalls</th><th>Tick failures</th><th>Bad packets</th><th>Arrange cmds</th><th>R1 p50/p95/p99/max ms</th><th>R1 - RTT p50/p95/p99 ms</th><th>Tool CPU % (cores)</th></tr>");
            foreach (MeasureResult p in s.Phases)
            {
                string latency = p.InputLatency is LatencyStats l ? $"{F(l.P50Ms)} / {F(l.P95Ms)} / {F(l.P99Ms)} / {F(l.MaxMs)}" : "Not Available";
                string latencyNet = p.InputLatency is LatencyStats n ? $"{F(n.MinusRttP50Ms)} / {F(n.MinusRttP95Ms)} / {F(n.MinusRttP99Ms)}" : "-";
                sb.Append("<tr><td>").Append(E(p.Name)).Append(p.Cancelled ? " (cut short)" : string.Empty).Append("</td><td>").Append(E(p.StepId ?? string.Empty))
                  .Append("</td><td>").Append(F(p.Seconds)).Append("</td><td>").Append(p.PlayersMin).Append('-').Append(p.PlayersMax)
                  .Append("</td><td>").Append(p.AliveMin).Append('-').Append(p.AliveMax)
                  .Append("</td><td>").Append(F(p.TickP50Ms)).Append("</td><td>").Append(F(p.TickP95Ms)).Append("</td><td>").Append(F(p.TickP99Ms)).Append("</td><td>").Append(F(p.TickMaxMs))
                  .Append(p.TicksExact ? string.Empty : "*").Append("</td><td>").Append(F(p.CpuAvgPercent)).Append(" / ").Append(F(p.CpuMaxPercent))
                  .Append("</td><td>").Append(F(p.WorkingSetStartMB)).Append(" / ").Append(F(p.WorkingSetEndMB)).Append(" / ").Append(F(p.WorkingSetMaxMB))
                  .Append("</td><td>").Append(F(p.ManagedStartMB)).Append(" / ").Append(F(p.ManagedEndMB)).Append(" / ").Append(F(p.ManagedMaxMB))
                  .Append("</td><td>").Append(p.Gen0).Append('/').Append(p.Gen1).Append('/').Append(p.Gen2).Append("</td><td>").Append(F(p.AllocatedMB)).Append(" (").Append(F(p.AllocatedMBPerSec)).Append(')')
                  .Append("</td><td>").Append(F(p.GcPauseMs)).Append("</td><td>").Append(F(p.SendKBps)).Append(" / ").Append(F(p.RecvKBps))
                  .Append("</td><td>").Append(F(p.PktInPerSec)).Append(" / ").Append(F(p.PktOutPerSec)).Append("</td><td>").Append(F(p.QaCommandMsAvg))
                  .Append("</td><td>").Append(p.BuildPiecesStart).Append('-').Append(p.BuildPiecesEnd).Append("</td><td>").Append(p.DbQueueMax).Append(", ").Append(p.DbSaved).Append(", ").Append(p.DbFailed)
                  .Append("</td><td>").Append(p.Stalls).Append("</td><td>").Append(p.TickFailures).Append("</td><td>").Append(p.BadPackets).Append("</td><td>").Append(p.ArrangeCommands)
                  .Append("</td><td>").Append(E(latency)).Append("</td><td>").Append(E(latencyNet)).Append("</td><td>").Append(F(p.ToolCpuAvgPercent)).Append(" (").Append(F(p.ToolCpuCores)).Append(")</td></tr>");
            }
            sb.Append("</table>");
            if (s.Phases.Any(p => !p.TicksExact)) sb.Append("<p class=\"muted\">* approximate tick percentiles: ").Append(E(s.Phases.First(p => !p.TicksExact).TicksNote ?? string.Empty)).Append("</p>");
            sb.Append("<p class=\"muted\">R1 = input sent by a headless actor until a snapshot acknowledges it (AckInputSeq): includes the server's next tick, the 15 Hz snapshot interval and the actor pump's ~33 ms tick. Arrange cmds = QA commands groups sent during the phase (re-arm after a respawn); they count in QA ms/tick.</p>");
            foreach (MeasureResult p in s.Phases)
            {
                sb.Append("<details><summary>Samples of ").Append(E(p.Name)).Append(" (").Append(p.Samples.Count).Append(", every ").Append(F(p.SampleSeconds)).Append(" s")
                  .Append(p.SamplesDropped > 0 ? $", {p.SamplesDropped} not kept" : string.Empty).Append(")</summary><table><tr><th>t (s)</th><th>Players</th><th>Alive</th><th>Tick p50</th><th>p95</th><th>p99</th><th>max</th><th>CPU %</th><th>Tool CPU %</th><th>WS MB</th><th>Managed MB</th><th>GC 0/1/2</th><th>Send KB/s</th><th>Recv KB/s</th><th>QA ms</th><th>Build</th><th>Stalls</th><th>DB queue</th></tr>");
                foreach (MeasureSample x in p.Samples)
                {
                    sb.Append("<tr><td>").Append(F(x.Seconds)).Append("</td><td>").Append(x.Players).Append("</td><td>").Append(x.Alive).Append("</td><td>").Append(F(x.TickP50Ms))
                      .Append("</td><td>").Append(F(x.TickP95Ms)).Append("</td><td>").Append(F(x.TickP99Ms)).Append("</td><td>").Append(F(x.TickMaxMs)).Append("</td><td>").Append(F(x.CpuPercent))
                      .Append("</td><td>").Append(F(x.ToolCpuPercent)).Append("</td><td>").Append(F(x.WorkingSetMB)).Append("</td><td>").Append(F(x.ManagedMB))
                      .Append("</td><td>").Append(x.Gen0).Append('/').Append(x.Gen1).Append('/').Append(x.Gen2).Append("</td><td>").Append(F(x.BytesOutPerSec / 1024)).Append("</td><td>").Append(F(x.BytesInPerSec / 1024))
                      .Append("</td><td>").Append(F(x.QaCommandMs)).Append("</td><td>").Append(x.BuildPieces).Append("</td><td>").Append(x.Stalls).Append("</td><td>").Append(x.DbQueue).Append("</td></tr>");
                }
                sb.Append("</table></details>");
            }
        }

        sb.Append("<h2>Stalls</h2>");
        if (s.Stalls.Count == 0) sb.Append("<p class=\"muted\">The server counted no stall during a measure phase.</p>");
        foreach (StallRecord st in s.Stalls)
        {
            sb.Append("<div class=\"fail\"><p><b>").Append(E(st.Utc)).Append("</b> phase ").Append(E(st.Phase)).Append(", step ").Append(E(st.StepId ?? "?"))
              .Append(": stalls ").Append(st.StallsBefore).Append(" → ").Append(st.StallsAfter).Append(", players ").Append(st.Players).Append(", actors ").Append(st.Actors).Append("</p>");
            if (st.Sample != null) sb.Append("<pre>").Append(E(JsonSerializer.Serialize(st.Sample, QaJson.Compact))).Append("</pre>");
            if (st.EventsNote != null) sb.Append("<p class=\"muted\">").Append(E(st.EventsNote)).Append("</p>");
            if (st.RecentEvents.Count > 0)
            {
                sb.Append("<pre>");
                foreach (JsonElement e in st.RecentEvents) sb.Append(E(e.GetRawText())).Append('\n');
                sb.Append("</pre>");
            }
            sb.Append("</div>");
        }
        if (s.StallsDropped > 0) sb.Append("<p class=\"muted\">").Append(s.StallsDropped).Append(" more stall records were not kept.</p>");

        if (s.Crash != null)
        {
            CrashRecord c = s.Crash;
            sb.Append("<h2>Crash</h2><div class=\"fail\"><p>At ").Append(E(c.Utc)).Append(", step ").Append(E(c.StepId ?? "?")).Append(", exit code ")
              .Append(E(c.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown")).Append(", actors ").Append(c.Actors).Append(" (").Append(c.ActorsJoined)
              .Append(" joined), last phase ").Append(E(c.LastPhase ?? "-")).Append("</p>");
            if (c.LastSample != null) sb.Append("<p>Last sample:</p><pre>").Append(E(JsonSerializer.Serialize(c.LastSample, QaJson.Compact))).Append("</pre>");
            sb.Append("<p>Server log tail:</p><pre>");
            foreach (string line in c.LogTail) sb.Append(E(line)).Append('\n');
            sb.Append("</pre></div>");
        }

        if (s.Groups != null)
        {
            sb.Append("<h2>Groups</h2><pre>").Append(E(JsonSerializer.Serialize(s.Groups, QaJson.Options))).Append("</pre>");
        }
    }
}
