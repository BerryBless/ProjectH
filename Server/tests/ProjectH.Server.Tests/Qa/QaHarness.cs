using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectH.Server.Game;
using ProjectH.Server.Qa;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Qa;

// A GameLoop (never started: the test thread runs its ticks) with the QA executor attached, and players joined straight
// into its match (no connection: their packets go nowhere). A work item submitted here runs in the next RunTick.
internal sealed class QaHarness : IDisposable
{
    // 기능: 스레드를 시작하지 않은 8인 GameLoop(카운트다운·결과 1초, 수송기 없음)에 Development 환경의 QaControl을 붙인다.
    // 입력: qa - QA 설정(null = 기본), minPlayers - 시작 최소 인원, teamSize - 팀 크기.
    // 출력: Tick을 직접 돌릴 수 있는 QaHarness(Dispose가 GameLoop를 닫는다).
    public QaHarness(QaOptions? qa = null, int minPlayers = 2, int teamSize = 1)
    {
        Options = new ServerOptions
        {
            Port = 0, MaxPlayers = 8, MinPlayers = minPlayers, StartCountdownSeconds = 1, ResultSeconds = 1, AirDrop = false, TeamSize = teamSize,
        };
        Loop = new GameLoop(Options, TestGameData.Create(), NullLogger.Instance);
        Qa = new QaControl(Options, qa ?? new QaOptions(), NullLogger.Instance, "Development");
        Loop.AttachQa(Qa);
    }

    public ServerOptions Options { get; }
    public GameLoop Loop { get; }
    public QaControl Qa { get; }
    public Match Match => Loop.Match;

    // 기능: 연결 없이 플레이어를 현재 경기에 바로 넣는다.
    // 입력: peer - peer id, devPlayerId - 플레이어 이름.
    // 출력: 들어간 PlayerEntity. Join이 Ok가 아니면 테스트가 실패한다.
    public PlayerEntity Join(int peer, string devPlayerId)
    {
        Assert.Equal(JoinResult.Ok, Loop.Match.TryJoin(peer, devPlayerId));
        Loop.Match.TryGetPlayer(peer, out PlayerEntity player);
        return player;
    }

    // 기능: GameLoop의 Tick을 주어진 횟수만큼 이 스레드에서 돌린다.
    // 입력: count - 돌릴 Tick 수.
    // 출력: 반환값 없음. 경기 상태가 count Tick만큼 진행되고 제출된 QA 작업이 실행된다.
    public void Ticks(int count)
    {
        for (int i = 0; i < count; i++) Loop.RunTick();
    }

    // 기능: qa-a·qa-b 둘을 넣고 경기가 시작될 때까지(최대 200 Tick) Tick을 돌린다.
    // 입력: 없음.
    // 출력: 두 PlayerEntity. 경기가 시작되지 않으면 테스트가 실패한다.
    // Two players, then ticks until the match runs.
    public (PlayerEntity A, PlayerEntity B) StartMatch()
    {
        PlayerEntity a = Join(1, "qa-a");
        PlayerEntity b = Join(2, "qa-b");
        for (int i = 0; i < 200 && !Match.Flow.InMatch; i++) Loop.RunTick();
        Assert.True(Match.Flow.InMatch);
        return (a, b);
    }

    // 기능: QA 작업 하나를 제출하고 Tick 하나를 돌려 실행시킨 뒤 결과를 HTTP 응답과 같은 JSON으로 바꾼다.
    // 입력: work - GameLoop 스레드에서 실행할 작업.
    // 출력: 상태 코드와 JSON 본문.
    public async Task<(int Status, JsonElement Body)> Run(QaWork work)
    {
        Task<QaResult> task = Qa.SubmitAsync(work);
        Loop.RunTick();
        QaResult result = await task;
        return (result.Status, JsonSerializer.SerializeToElement(result.Body, result.Body.GetType(), QaHttpService.Json));
    }

    // 기능: QA 명령 하나를 runId "test-run"으로 실행한다(Run을 거쳐 다음 Tick에서 실행).
    // 입력: command - 명령 이름, player - 대상 플레이어 이름(null = 없음), args - 인자 객체(null = 빈 객체).
    // 출력: 상태 코드와 JSON 본문.
    public Task<(int Status, JsonElement Body)> Command(string command, string? player = null, object? args = null)
    {
        JsonElement json = JsonSerializer.SerializeToElement(args ?? new { });
        return Run(t => QaCommands.Execute(t, command, player, "test-run", json, NullLogger.Instance));
    }

    // 기능: 명령 응답 본문에서 result 항목을 꺼낸다.
    // 입력: body - 명령 응답 본문.
    // 출력: body의 result 요소. 없으면 KeyNotFoundException.
    public static JsonElement Result(JsonElement body) => body.GetProperty("result");

    // 기능: GameLoop를 닫는다.
    // 입력: 없음.
    // 출력: 반환값 없음. GameLoop와 소켓이 닫힌다.
    public void Dispose() => Loop.Dispose();
}
