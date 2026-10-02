using System.Net;
using System.Text;
using System.Text.Json;

namespace ProjectH.QA;

// D17: report.json (everything, machine-readable) and report.html (one self-contained file: inline CSS, no scripts or
// external resources) in QA/Reports/<runId>/.
public static class ReportWriter
{
    public static void Write(RunReport report, string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(report, QaJson.Options), Encoding.UTF8);
        File.WriteAllText(Path.Combine(directory, "report.html"), Html(report), Encoding.UTF8);
    }

    public static string Html(RunReport r)
    {
        var sb = new StringBuilder(16 * 1024);
        string status = r.Status.ToString().ToUpperInvariant();
        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>")
          .Append(E($"{status} {r.Scenario} {r.RunId}")).Append("</title><style>")
          .Append("body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#222}h1{margin:0 0 8px}h2{margin-top:28px;border-bottom:1px solid #ccc}")
          .Append("table{border-collapse:collapse}td,th{border:1px solid #ccc;padding:3px 8px;text-align:left;vertical-align:top;font-size:13px}")
          .Append(".PASSED,.PASS{color:#fff;background:#2e7d32}.FAILED,.FAIL,.ERROR{color:#fff;background:#c62828}.CANCELLED,.SKIPPED{background:#ddd}")
          .Append(".badge{padding:2px 8px;border-radius:3px;font-weight:bold}pre{background:#f5f5f5;padding:8px;overflow:auto;max-height:480px;font-size:12px}")
          .Append(".fail{border:2px solid #c62828;padding:8px 12px;background:#fff5f5}.muted{color:#777}</style></head><body>");

        sb.Append("<h1><span class=\"badge ").Append(status).Append("\">").Append(status).Append("</span> ").Append(E(r.Scenario)).Append("</h1>");
        if (r.SkipReason != null) sb.Append("<p class=\"fail\">Skipped ").Append(E(r.SkipReason)).Append("</p>");
        if (r.Description.Length > 0) sb.Append("<p>").Append(E(r.Description)).Append("</p>");
        sb.Append("<table>");
        Row(sb, "Run ID", r.RunId);
        Row(sb, "Seed", r.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Row(sb, "Scenario file", r.ScenarioFile);
        Row(sb, "Tags", string.Join(", ", r.Tags));
        Row(sb, "Started", r.Started.ToString("yyyy-MM-dd HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture));
        Row(sb, "Duration", $"{r.DurationMs} ms");
        Row(sb, "Exit code", r.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Row(sb, "Git commit", (r.GitCommit ?? "(unknown)") + (r.GitDirty == true ? " (dirty working tree)" : r.GitDirty == false ? " (clean)" : string.Empty) + (r.GitError != null ? $" — {r.GitError}" : string.Empty));
        Row(sb, "Server", $"{r.ServerMode} {r.QaUrl} game port {r.GamePort}" + (r.ServerPid != null ? $" pid {r.ServerPid}" : string.Empty));
        Row(sb, "Server version", r.ServerVersion ?? "(unknown)");
        if (r.ParameterSet != null) Row(sb, "Parameters", $"set {r.ParameterSet}: {BatchSummary.Compact(r.Parameters)}");
        if (r.Batch != null) Row(sb, "Batch", r.Batch);
        sb.Append("</table>");
        if (r.ToolError != null) sb.Append("<div class=\"fail\"><b>Tool error:</b> <pre>").Append(E(r.ToolError)).Append("</pre></div>");

        if (r.Failure != null)
        {
            FailureInfo f = r.Failure;
            sb.Append("<h2>Failure</h2><div class=\"fail\"><p><b>Step ").Append(f.StepIndex + 1).Append(" (").Append(E(f.StepId)).Append(") ")
              .Append(E(f.Action)).Append("</b></p><p>").Append(E(f.Message ?? string.Empty)).Append("</p>");
            if (f.Expected != null) sb.Append("<p>Expected: <b>").Append(E(f.Expected)).Append("</b></p>");
            if (f.Actual != null) sb.Append("<p>Actual: <b>").Append(E(f.Actual)).Append("</b></p>");
            sb.Append("</div>");
        }

        sb.Append("<h2>Steps</h2><table><tr><th>#</th><th>Id</th><th>Step</th><th>Phase</th><th>Result</th><th>ms</th><th>Detail</th></tr>");
        foreach (StepResult s in r.Steps)
        {
            string word = StepResult.StatusWord(s.Status);
            sb.Append("<tr><td>").Append(s.Index + 1).Append("</td><td>").Append(E(s.Id)).Append("</td><td>").Append(E(s.Title))
              .Append("</td><td>").Append(E(s.Phase ?? string.Empty)).Append("</td><td class=\"").Append(word).Append("\">").Append(word)
              .Append("</td><td>").Append(s.DurationMs).Append("</td><td>").Append(E(s.Message ?? string.Empty));
            if (s.Expected != null || s.Actual != null) sb.Append("<br>Expected: ").Append(E(s.Expected ?? "")).Append("<br>Actual: ").Append(E(s.Actual ?? ""));
            sb.Append("</td></tr>");
        }
        sb.Append("</table>");

        if (r.Warnings.Count > 0)
        {
            sb.Append("<h2>Warnings</h2><ul>");
            foreach (string w in r.Warnings) sb.Append("<li>").Append(E(w)).Append("</li>");
            sb.Append("</ul>");
        }

        if (r.Baseline != null) Baseline(sb, r.Baseline);

        if (r.StateDump != null)
        {
            sb.Append("<h2>State dump</h2><h3>Actors</h3><table><tr><th>Actor</th><th>Status</th><th>Entity</th><th>Alive</th><th>Position</th><th>HP</th><th>Shield</th><th>Weapon</th><th>Ammo</th><th>RTT</th><th>Reason / error</th></tr>");
            foreach (ActorState a in r.StateDump.Actors)
            {
                sb.Append("<tr><td>").Append(E(a.Alias)).Append("</td><td>").Append(a.Status).Append("</td><td>").Append(a.EntityId)
                  .Append("</td><td>").Append(a.Alive).Append("</td><td>").Append(E($"{a.Position.X:0.##}, {a.Position.Y:0.##}, {a.Position.Z:0.##}"))
                  .Append("</td><td>").Append(a.Health).Append("</td><td>").Append(a.Shield).Append("</td><td>").Append(E($"{a.WeaponName} (slot {a.CurrentSlot}, {a.Tool})"))
                  .Append("</td><td>").Append(a.Ammo).Append("</td><td>").Append(a.RttMs).Append("</td><td>").Append(E(a.DisconnectReason + (a.Error != null ? " " + a.Error : ""))).Append("</td></tr>");
            }
            sb.Append("</table>");
            if (r.StateDump.Error != null) sb.Append("<p class=\"muted\">Server state unavailable: ").Append(E(r.StateDump.Error)).Append("</p>");
            Json(sb, "Server players", r.StateDump.Players);
            Json(sb, "Match", r.StateDump.Match);
        }

        sb.Append("<h2>Last events (").Append(r.Events.Count).Append(")</h2>");
        if (r.EventsDroppedByServer > 0) sb.Append("<p class=\"muted\">The server's event ring dropped ").Append(r.EventsDroppedByServer).Append(" events.</p>");
        sb.Append("<pre>");
        foreach (JsonElement e in r.Events) sb.Append(E(e.GetRawText())).Append('\n');
        sb.Append("</pre>");

        Json(sb, "Metrics", r.Metrics);

        sb.Append("<h2>Cleanup</h2><table><tr><th>What</th><th>Result</th><th>Detail</th></tr>");
        foreach (CleanupResult c in r.Cleanup)
            sb.Append("<tr><td>").Append(E(c.Name)).Append("</td><td class=\"").Append(c.Ok ? "PASS" : "FAIL").Append("\">").Append(c.Ok ? "OK" : "FAILED").Append("</td><td>").Append(E(c.Message)).Append("</td></tr>");
        sb.Append("</table>");

        // D30: manual checks (request §90), one row each.
        sb.Append("<h2>Manual checks</h2>");
        if (r.ManualChecks.Count == 0)
        {
            sb.Append("<p class=\"muted\">None.</p>");
        }
        else
        {
            sb.Append("<table><tr><th>#</th><th>Step</th><th>Check</th><th>Result</th><th>By</th><th>Note</th></tr>");
            foreach (JsonElement m in r.ManualChecks)
            {
                string Field(string name) => JsonPath.Child(m, name) is { ValueKind: not JsonValueKind.Null } v ? QaJson.Text(v) : string.Empty;
                string result = Field("result");
                string css = result is "PASS" or "FAIL" ? result : "SKIPPED";
                int index = JsonPath.Child(m, "stepIndex") is { ValueKind: JsonValueKind.Number } n ? n.GetInt32() + 1 : 0;
                sb.Append("<tr><td>").Append(index).Append("</td><td>").Append(E(Field("stepId"))).Append("</td><td>").Append(E(Field("description")))
                  .Append("</td><td class=\"").Append(css).Append("\">").Append(E(result)).Append("</td><td>").Append(E(Field("by")))
                  .Append("</td><td>").Append(E(Field("note"))).Append("</td></tr>");
            }
            sb.Append("</table>");
        }

        // QA-4: Unity screenshots next to the report (relative links keep the folder movable); a thumbnail links the PNG.
        sb.Append("<h2>Screenshots</h2>");
        if (r.Screenshots.Count == 0)
        {
            sb.Append("<p class=\"muted\">None.</p>");
        }
        else
        {
            sb.Append("<div>");
            foreach (string shot in r.Screenshots)
            {
                string href = string.Join('/', shot.Split('/').Select(Uri.EscapeDataString));
                sb.Append("<figure style=\"display:inline-block;margin:4px\"><a href=\"").Append(E(href)).Append("\"><img src=\"").Append(E(href))
                  .Append("\" style=\"max-width:320px;border:1px solid #ccc\" alt=\"").Append(E(shot)).Append("\"></a><figcaption>")
                  .Append(E(shot)).Append("</figcaption></figure>");
            }
            sb.Append("</div>");
        }

        if (r.ServerArguments.Count > 0) sb.Append("<h2>Server command</h2><pre>dotnet ").Append(E(string.Join(' ', r.ServerArguments))).Append("</pre>");
        sb.Append("<h2>Server log (last ").Append(r.ServerLogTail.Length).Append(" lines)</h2><pre>");
        foreach (string line in r.ServerLogTail) sb.Append(E(line)).Append('\n');
        sb.Append("</pre></body></html>");
        return sb.ToString();
    }

    // D33 / request §136: Previous / Current / Change % against the latest earlier PASSED run with the same parameters.
    private static void Baseline(StringBuilder sb, BaselineReport b)
    {
        sb.Append("<h2>Baseline</h2>");
        if (b.Note != null) sb.Append("<p class=\"muted\">").Append(E(b.Note)).Append("</p>");
        if (b.PreviousRunId != null)
        {
            sb.Append("<p>Compared with run <b>").Append(E(b.PreviousRunId)).Append("</b> (").Append(E(b.PreviousUtc ?? ""))
              .Append(b.PreviousGitCommit != null ? ", commit " + E(b.PreviousGitCommit[..Math.Min(10, b.PreviousGitCommit.Length)]) : "")
              .Append("). A metric without a threshold in the scenario that is more than ").Append(b.WarnPercent.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))
              .Append("% worse (higher) is a warning, never a failure.</p>");
            sb.Append("<table><tr><th>Metric</th><th>Previous</th><th>Current</th><th>Change %</th><th>Threshold</th><th></th></tr>");
            foreach (BaselineRow row in b.Rows)
            {
                string change = row.ChangePercent is double c ? (c >= 0 ? "+" : "") + c.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "%" : "-";
                sb.Append("<tr><td>").Append(E(row.Name)).Append("</td><td>").Append(BaselineHistory.Fmt(row.Previous)).Append("</td><td>")
                  .Append(BaselineHistory.Fmt(row.Current)).Append("</td><td>").Append(E(change)).Append("</td><td>").Append(E(row.Threshold))
                  .Append("</td>").Append(row.Warning ? "<td class=\"FAIL\">WARNING</td>" : "<td></td>").Append("</tr>");
            }
            sb.Append("</table>");
        }
        foreach (string w in b.Warnings) sb.Append("<p class=\"fail\">").Append(E(w)).Append("</p>");
        if (b.HistoryFile != null) sb.Append("<p class=\"muted\">History: ").Append(E(b.HistoryFile)).Append(b.Recorded ? "" : " (this run was not recorded)").Append("</p>");
    }

    private static void Row(StringBuilder sb, string name, string value) =>
        sb.Append("<tr><th>").Append(E(name)).Append("</th><td>").Append(E(value)).Append("</td></tr>");

    private static void Json(StringBuilder sb, string title, JsonElement? value)
    {
        sb.Append("<h3>").Append(E(title)).Append("</h3>");
        if (value == null)
        {
            sb.Append("<p class=\"muted\">Not available.</p>");
            return;
        }
        sb.Append("<pre>").Append(E(JsonSerializer.Serialize(value.Value, QaJson.Options))).Append("</pre>");
    }

    private static string E(string text) => WebUtility.HtmlEncode(text);
}
