using System.Globalization;
using System.Text.Json;

namespace ProjectH.QA;

// D18 CLI: run / validate / list. Kept out of Program so tests can call it (exit codes, malformed files) without a
// child process. Exit: 0 all PASS, 1 a scenario failed (or was stopped), 2 a tool error (bad file, server would not
// start, internal error); with several scenarios the highest wins.
public static class QaCli
{
    public const string Usage =
        "Usage:\n" +
        "  dotnet run --project Server/src/ProjectH.QA -- run <file.json | directory | category | suite> [options]\n" +
        "  dotnet run --project Server/src/ProjectH.QA -- validate <file.json | directory | category | suite>\n" +
        "  dotnet run --project Server/src/ProjectH.QA -- list\n" +
        "Options:\n" +
        "  --seed N            override the scenario seed\n" +
        "  --attach URL        use a running QA-mode server (e.g. http://127.0.0.1:7780) instead of launching one\n" +
        "  --server-dll PATH   ProjectH.Server.dll to launch (default: the newest build under Server/src/ProjectH.Server/bin)\n" +
        "  --report-dir DIR    where QA/Reports/<runId>/ goes (default QA/Reports)\n" +
        "  --poll-ms N         server polling interval for waitFor/waitForEvent, 20-5000 (default 100)\n" +
        "  --verbose           step details and the server's log on the console\n" +
        "  --repo DIR          repository root (default: found from the current directory)";

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
        public string? Repo { get; set; }
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
            case "validate":
            case "run":
                break;
            default:
                output.WriteLine($"Unknown command '{p.Command}'.");
                output.WriteLine(Usage);
                return 2;
        }

        List<string> files;
        try
        {
            files = ScenarioCatalog.Resolve(root, p.Target!);
        }
        catch (QaToolException e)
        {
            output.WriteLine(e.Message);
            return 2;
        }

        int exit = 0;
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
                if (invalid) exit = 2;
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
            };
            RunReport report = await new QaOrchestrator(options, registry, markers, output).RunAsync(load.Scenario!, issues, token).ConfigureAwait(false);
            exit = Math.Max(exit, report.ExitCode);
        }
        if (files.Count > 1) output.WriteLine($"{files.Count} scenarios, exit code {exit}.");
        return exit;
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

// What `run X` means: a file, a directory, a category under QA/Scenarios, or a suite QA/Suites/X.json
// ({ "scenarios": [ "Smoke/connect.json", "Combat" ] }, entries relative to QA/Scenarios, then to the repo root).
public static class ScenarioCatalog
{
    public const int MaxScenarios = 1000;

    public static List<string> Resolve(string root, string target)
    {
        var files = new List<string>();
        Add(root, target, files, allowSuite: true);
        if (files.Count == 0) throw new QaToolException($"No scenarios found for '{target}'.");
        return files.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxScenarios).ToList();
    }

    private static void Add(string root, string target, List<string> files, bool allowSuite)
    {
        string scenarios = Path.Combine(root, "QA", "Scenarios");
        foreach (string candidate in new[] { target, Path.Combine(scenarios, target), Path.Combine(root, target) })
        {
            if (File.Exists(candidate))
            {
                files.Add(Path.GetFullPath(candidate));
                return;
            }
            if (Directory.Exists(candidate))
            {
                files.AddRange(Directory.GetFiles(candidate, "*.json", SearchOption.AllDirectories)
                    .Select(Path.GetFullPath).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
                return;
            }
        }
        string suite = Path.Combine(root, "QA", "Suites", target + ".json");
        if (!allowSuite || !File.Exists(suite)) throw new QaToolException($"'{target}' is not a scenario file, directory, category (QA/Scenarios/<name>) or suite (QA/Suites/<name>.json).");
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(suite), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!doc.RootElement.TryGetProperty("scenarios", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
                throw new QaToolException($"Suite {target}: 'scenarios' must be an array.");
            foreach (JsonElement e in list.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.String) throw new QaToolException($"Suite {target}: entries must be paths.");
                Add(root, e.GetString()!, files, allowSuite: false);
            }
        }
        catch (JsonException e)
        {
            throw new QaToolException($"Suite {target}: malformed JSON: {e.Message}");
        }
    }
}
