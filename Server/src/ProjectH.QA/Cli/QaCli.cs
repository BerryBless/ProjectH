using System.Globalization;
using System.Text.Json;

namespace ProjectH.QA;

// D18 CLI: run / validate / list. Kept out of Program so tests can call it (exit codes, malformed files) without a
// child process. Exit: 0 all PASS or SKIPPED (SKIPPED is 1 with --fail-on-skip), 1 a scenario failed (or was stopped), 2 a tool error (bad file, server would not
// start, internal error); with several scenarios the highest wins.
public static class QaCli
{
    public const string Usage =
        "Usage:\n" +
        "  dotnet run --project Server/src/ProjectH.QA -- run <file.json | directory | category:<name> | suite:<name> | name> [options]\n" +
        "  dotnet run --project Server/src/ProjectH.QA -- validate <same targets>\n" +
        "    (a bare name is a category or a suite; if both exist, choose with category: or suite:)\n" +
        "  dotnet run --project Server/src/ProjectH.QA -- list\n" +
        "  dotnet run --project Server/src/ProjectH.QA -- ui [--port 5180]   (web UI on http://127.0.0.1:<port>/)\n" +
        "Options:\n" +
        "  --seed N            override the scenario seed\n" +
        "  --attach URL        use a running QA-mode server (e.g. http://127.0.0.1:7780) instead of launching one\n" +
        "  --server-dll PATH   ProjectH.Server.dll to launch (default: the newest build under Server/src/ProjectH.Server/bin)\n" +
        "  --report-dir DIR    where QA/Reports/<runId>/ goes (default QA/Reports)\n" +
        "  --poll-ms N         server polling interval for waitFor/waitForEvent, 20-5000 (default 100)\n" +
        "  --verbose           step details and the server's log on the console\n" +
        "  --fail-on-skip      a SKIPPED scenario (e.g. no Docker for a DB fault) exits 1 instead of 0\n" +
        "  --repo DIR          repository root (default: found from the current directory)\n" +
        "  --port N            ui only: the UI's port on 127.0.0.1, 0-65535 (default 5180; 0 = any free port)";

    public sealed class Parsed
    {
        public string Command { get; set; } = string.Empty;
        public string? Target { get; set; }
        public int? Seed { get; set; }
        public string? Attach { get; set; }
        public string? ServerDll { get; set; }
        public string? ReportDir { get; set; }
        public int PollMs { get; set; } = 100;
        public bool Verbose { get; set; }
        public bool FailOnSkip { get; set; }
        public string? Repo { get; set; }
        public int Port { get; set; } = UiHostOptions.DefaultPort;
    }

    public static bool TryParse(string[] args, out Parsed parsed, out string? error)
    {
        parsed = new Parsed();
        error = null;
        if (args.Length == 0)
        {
            error = "No command.";
            return false;
        }
        parsed.Command = args[0].ToLowerInvariant();
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                if (parsed.Target != null)
                {
                    error = $"Unexpected argument '{a}'.";
                    return false;
                }
                parsed.Target = a;
                continue;
            }
            if (a == "--verbose")
            {
                parsed.Verbose = true;
                continue;
            }
            if (a == "--fail-on-skip")
            {
                parsed.FailOnSkip = true;
                continue;
            }
            if (i + 1 >= args.Length)
            {
                error = $"{a} needs a value.";
                return false;
            }
            string value = args[++i];
            switch (a)
            {
                case "--seed":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed)) { error = "--seed must be an integer."; return false; }
                    parsed.Seed = seed;
                    break;
                case "--attach":
                    if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https")) { error = "--attach must be an http URL."; return false; }
                    parsed.Attach = value;
                    break;
                case "--server-dll":
                    parsed.ServerDll = Path.GetFullPath(value);
                    break;
                case "--report-dir":
                    parsed.ReportDir = Path.GetFullPath(value);
                    break;
                case "--poll-ms":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int poll) || poll < 20 || poll > 5000) { error = "--poll-ms must be 20-5000."; return false; }
                    parsed.PollMs = poll;
                    break;
                case "--port":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) || port < 0 || port > 65535) { error = "--port must be 0-65535."; return false; }
                    parsed.Port = port;
                    break;
                case "--repo":
                    parsed.Repo = Path.GetFullPath(value);
                    break;
                default:
                    error = $"Unknown option {a}.";
                    return false;
            }
        }
        if (parsed.Command is "run" or "validate" && parsed.Target == null)
        {
            error = $"'{parsed.Command}' needs a scenario file, directory, category or suite.";
            return false;
        }
        return true;
    }

    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken token,
        Func<Uri, IQaServerClient>? serverFactory = null, Func<string, string, IQaActor>? actorFactory = null)
    {
        if (!TryParse(args, out Parsed p, out string? error))
        {
            output.WriteLine(error);
            output.WriteLine(Usage);
            return 2;
        }
        string root = p.Repo ?? ServerLocator.FindRepoRoot(Directory.GetCurrentDirectory())
            ?? ServerLocator.FindRepoRoot(AppContext.BaseDirectory) ?? Directory.GetCurrentDirectory();

        MarkerStore markers;
        try
        {
            markers = MarkerStore.LoadFile(Path.Combine(root, "QA", "Markers.json"));
        }
        catch (Exception e) when (e is FormatException or JsonException or IOException)
        {
            output.WriteLine($"QA/Markers.json: {e.Message}");
            return 2;
        }
        ActionRegistry registry = ActionRegistry.CreateDefault();

        switch (p.Command)
        {
            case "list":
                List(root, output);
                return 0;
            case "ui":
                return await RunUiAsync(p, root, output, token, serverFactory, actorFactory).ConfigureAwait(false);
            case "validate":
            case "run":
                break;
            default:
                output.WriteLine($"Unknown command '{p.Command}'.");
                output.WriteLine(Usage);
                return 2;
        }

        IReadOnlyList<string> files;
        try
        {
            ScenarioSelection selection = ScenarioCatalog.Select(root, p.Target!);
            // First line: what was resolved, so a run never silently covers a different set than meant.
            output.WriteLine(selection.Description);
            files = selection.Files;
        }
        catch (QaToolException e)
        {
            output.WriteLine(e.Message);
            return 2;
        }

        int exit = 0;
        int passed = 0, failed = 0, skipped = 0, errors = 0;
        foreach (string file in files)
        {
            if (token.IsCancellationRequested)
            {
                output.WriteLine("Stopped.");
                return Math.Max(exit, 1);
            }
            ScenarioLoadResult load = ScenarioLoader.LoadFile(file);
            var issues = new List<ValidationIssue>();
            foreach (string e in load.Errors) issues.Add(new ValidationIssue(true, "file", e));
            if (load.Scenario != null) issues.AddRange(ScenarioValidator.Validate(load.Scenario, registry, markers));
            bool invalid = issues.Any(i => i.IsError);
            if (p.Command == "validate" || invalid)
            {
                output.WriteLine($"{(invalid ? "INVALID" : "OK")} {Relative(root, file)}");
                foreach (ValidationIssue i in issues) output.WriteLine("   " + i);
                if (invalid)
                {
                    exit = 2;
                    errors++;
                }
                continue;
            }
            foreach (ValidationIssue w in issues) output.WriteLine($"   {w}");

            var options = new QaRunOptions
            {
                RepoRoot = root,
                SeedOverride = p.Seed,
                AttachUrl = p.Attach,
                ServerDll = p.ServerDll,
                ReportDir = p.ReportDir,
                PollMs = p.PollMs,
                Verbose = p.Verbose,
                ServerClientFactory = serverFactory,
                ActorFactory = actorFactory,
                FailOnSkip = p.FailOnSkip,
            };
            RunReport report = await new QaOrchestrator(options, registry, markers, output).RunAsync(load.Scenario!, issues, token).ConfigureAwait(false);
            exit = Math.Max(exit, report.ExitCode);
            switch (report.Status)
            {
                case RunStatus.Passed: passed++; break;
                case RunStatus.Skipped: skipped++; break;
                case RunStatus.Error: errors++; break;
                default: failed++; break;
            }
        }
        if (files.Count > 1)
        {
            output.WriteLine(p.Command == "run"
                ? $"{files.Count} scenarios: {passed} passed, {failed} failed, {skipped} skipped, {errors} errors; exit code {exit}."
                : $"{files.Count} scenarios, exit code {exit}.");
        }
        return exit;
    }

    // D19: serve the UI until Ctrl+C. Shutdown order: stop the active run and wait for its cleanup (actors, launched
    // server, report), then stop the web host.
    private static async Task<int> RunUiAsync(Parsed p, string root, TextWriter output, CancellationToken token,
        Func<Uri, IQaServerClient>? serverFactory, Func<string, string, IQaActor>? actorFactory)
    {
        QaUiHost host;
        try
        {
            host = await QaUiHost.StartAsync(new UiHostOptions
            {
                RepoRoot = root,
                Port = p.Port,
                AttachUrl = p.Attach,
                ServerDll = p.ServerDll,
                ReportDir = p.ReportDir,
                PollMs = p.PollMs,
                ServerClientFactory = serverFactory,
                ActorFactory = actorFactory,
            }, token).ConfigureAwait(false);
        }
        catch (IOException e)
        {
            output.WriteLine($"The UI could not start on 127.0.0.1:{p.Port}: {e.Message}");
            return 2;
        }
        await using (host.ConfigureAwait(false))
        {
            output.WriteLine($"QA UI: {host.Url}  (127.0.0.1 only; Ctrl+C stops it)");
            try
            {
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                output.WriteLine("Stopping the UI (an active run is stopped and cleaned up first)...");
            }
        }
        return 0;
    }

    private static void List(string root, TextWriter output)
    {
        string scenarios = Path.Combine(root, "QA", "Scenarios");
        output.WriteLine("Scenarios:");
        if (Directory.Exists(scenarios))
        {
            foreach (string dir in Directory.GetDirectories(scenarios).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                output.WriteLine($"  {Path.GetFileName(dir)}");
                foreach (string f in Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    ScenarioLoadResult load = ScenarioLoader.LoadFile(f);
                    string name = load.Scenario?.Name ?? "(malformed)";
                    string tags = load.Scenario != null && load.Scenario.Tags.Count > 0 ? $" [{string.Join(", ", load.Scenario.Tags)}]" : string.Empty;
                    output.WriteLine($"    {Relative(scenarios, f)}  {name}{tags}");
                }
            }
        }
        string suites = Path.Combine(root, "QA", "Suites");
        output.WriteLine("Suites:");
        if (Directory.Exists(suites))
        {
            foreach (string f in Directory.GetFiles(suites, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                output.WriteLine($"  {Path.GetFileNameWithoutExtension(f)}");
        }
        output.WriteLine("Actions:");
        foreach (ActionSpec s in ActionRegistry.CreateDefault().Specs)
            output.WriteLine($"  {s.Name}{(s.ServerCommand ? " (server QA command)" : string.Empty)}");
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path);
}

// What `run X` / `validate X` means (D18):
//  - `suite:<name>`     QA/Suites/<name>.json ({ "scenarios": [ "Smoke/connect.json", "Combat" ] }, entries relative to
//                       QA/Scenarios only: never the repo root, where "Server" would be the Server/ source folder);
//  - `category:<name>`  every scenario under QA/Scenarios/<name>;
//  - a path             a .json file or a directory (as given, under QA/Scenarios, or under the repo root);
//  - a bare name        the category or the suite of that name. When both exist it is a tool error that names both
//                       choices: a run never silently picks one set over the other.
// The resolution is always described ("suite smoke: 3 scenarios") so the console shows what will run.
public sealed record ScenarioSelection(string Description, IReadOnlyList<string> Files);

public static class ScenarioCatalog
{
    public const int MaxScenarios = 1000;
    public const string SuitePrefix = "suite:";
    public const string CategoryPrefix = "category:";

    public static List<string> Resolve(string root, string target) => Select(root, target).Files.ToList();

    public static ScenarioSelection Select(string root, string target)
    {
        string scenarios = Path.Combine(root, "QA", "Scenarios");
        if (target.StartsWith(SuitePrefix, StringComparison.OrdinalIgnoreCase))
        {
            string name = target[SuitePrefix.Length..];
            return Done($"suite {name}", Suite(root, name) ?? throw new QaToolException($"No suite '{name}' (QA/Suites/{name}.json)."));
        }
        if (target.StartsWith(CategoryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string name = target[CategoryPrefix.Length..];
            string? dir = Category(scenarios, name) ?? throw new QaToolException($"No category '{name}' (QA/Scenarios/{name}/).");
            return Done($"category {Path.GetFileName(dir)}", Directory(dir));
        }

        bool bare = target.Length > 0 && target.IndexOfAny(new[] { '/', '\\' }) < 0 && !target.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    && target != "." && target != "..";
        if (bare)
        {
            string? category = Category(scenarios, target);
            bool suite = File.Exists(SuiteFile(root, target));
            if (category != null && suite)
                throw new QaToolException($"'{target}' is both a suite and a category. Choose one: '{SuitePrefix}{target}' (QA/Suites/{target}.json) or '{CategoryPrefix}{Path.GetFileName(category)}' (QA/Scenarios/{Path.GetFileName(category)}/).");
            if (category != null) return Done($"category {Path.GetFileName(category)}", Directory(category));
            if (suite) return Done($"suite {target}", Suite(root, target)!);
        }

        var files = new List<string>();
        string kind = AddPath(root, target, files) ?? throw new QaToolException(
            $"'{target}' is not a scenario file, directory, category (QA/Scenarios/<name>) or suite (QA/Suites/<name>.json).");
        return Done($"{kind} {target}", files);
    }

    private static ScenarioSelection Done(string what, List<string> files)
    {
        List<string> distinct = files.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxScenarios).ToList();
        if (distinct.Count == 0) throw new QaToolException($"No scenarios found for {what}.");
        return new ScenarioSelection($"{what}: {distinct.Count} scenario{(distinct.Count == 1 ? string.Empty : "s")}", distinct);
    }

    private static string SuiteFile(string root, string name) => Path.Combine(root, "QA", "Suites", name + ".json");

    // The category folder with this name (any case), or null.
    private static string? Category(string scenarios, string name)
    {
        if (name.Length == 0 || name.IndexOfAny(new[] { '/', '\\' }) >= 0 || name is "." or ".." || !System.IO.Directory.Exists(scenarios)) return null;
        return System.IO.Directory.GetDirectories(scenarios).FirstOrDefault(d => string.Equals(Path.GetFileName(d), name, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> Directory(string dir) =>
        System.IO.Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories).Select(Path.GetFullPath).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

    private static List<string>? Suite(string root, string name)
    {
        string file = SuiteFile(root, name);
        if (name.Length == 0 || name.IndexOfAny(new[] { '/', '\\' }) >= 0 || !File.Exists(file)) return null;
        var files = new List<string>();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!doc.RootElement.TryGetProperty("scenarios", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
                throw new QaToolException($"Suite {name}: 'scenarios' must be an array.");
            foreach (JsonElement e in list.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.String) throw new QaToolException($"Suite {name}: entries must be paths.");
                if (AddPath(root, e.GetString()!, files, scenariosOnly: true) == null)
                    throw new QaToolException($"Suite {name}: '{e.GetString()}' is not a scenario file or directory under QA/Scenarios.");
            }
        }
        catch (JsonException e)
        {
            throw new QaToolException($"Suite {name}: malformed JSON: {e.Message}");
        }
        return files;
    }

    // A file or a directory: as given, under QA/Scenarios, under the repo root (CLI targets). Suite entries
    // (scenariosOnly): under QA/Scenarios and nowhere else, and they may not climb out of it with "..".
    private static string? AddPath(string root, string target, List<string> files, bool scenariosOnly = false)
    {
        string scenarios = Path.Combine(root, "QA", "Scenarios");
        string[] candidates = scenariosOnly ? new[] { Path.Combine(scenarios, target) } : new[] { target, Path.Combine(scenarios, target), Path.Combine(root, target) };
        foreach (string candidate in candidates)
        {
            if (scenariosOnly && !Path.GetFullPath(candidate).StartsWith(Path.GetFullPath(scenarios) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(candidate))
            {
                files.Add(Path.GetFullPath(candidate));
                return "file";
            }
            if (System.IO.Directory.Exists(candidate))
            {
                files.AddRange(Directory(candidate));
                return "directory";
            }
        }
        return null;
    }
}
