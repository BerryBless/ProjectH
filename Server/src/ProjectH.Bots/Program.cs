using ProjectH.Bots;

// Phase 7: headless bots for match and load tests (Docs/LoadTest.md).
// Example: dotnet run -c Release --project Server/src/ProjectH.Bots -- --count 16 --port 7777
const string Usage = "Options: --host 127.0.0.1 --port 7777 --count 1-50 --seed 1 --duration 0 (s, 0 = until Ctrl+C) " +
                     "--connect-interval-ms 100 --name-prefix bot --stats-interval 10 --reconnect false";

if (!BotOptions.TryParse(args, out BotOptions options, out string? error))
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine(Usage);
    return 2;
}

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;   // stop the loop and close the sockets instead of killing the process
    cancel.Cancel();
};

using var runner = new BotRunner(options, Console.WriteLine);
Console.WriteLine($"Starting {options.Count} bots against {options.Host}:{options.Port} (seed {options.Seed}).");
runner.Run(cancel.Token);
Console.WriteLine("Bots stopped.");
return 0;
