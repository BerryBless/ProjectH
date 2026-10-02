using ProjectH.QA;

// QA Scenario Orchestrator CLI (Docs/specs/2026-10-02-qa-tool-design.md D18). See QaCli.Usage.
// Ctrl+C cancels the run: the current step stops, cleanup closes the actors and stops the launched server, and the
// report is still written.
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;   // let the run clean up instead of killing the process
    cancel.Cancel();
};

try
{
    return await QaCli.RunAsync(args, Console.Out, cancel.Token);
}
catch (Exception e)
{
    Console.Error.WriteLine($"QA tool error: {e}");
    return 2;
}
