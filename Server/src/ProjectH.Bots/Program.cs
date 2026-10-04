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
// 기능: Ctrl+C를 가로채 프로세스를 바로 죽이지 않고 봇 루프 취소를 요청한다.
// 입력: e - 콘솔 취소 이벤트 인자.
// 출력: 반환값 없음. 프로세스 종료가 취소되고 토큰이 취소되어 Run이 끝난 뒤 소켓이 닫힌다.
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
