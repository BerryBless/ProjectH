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

    public PlayerEntity Join(int peer, string devPlayerId)
    {
        Assert.Equal(JoinResult.Ok, Loop.Match.TryJoin(peer, devPlayerId));
        Loop.Match.TryGetPlayer(peer, out PlayerEntity player);
        return player;
    }

    public void Ticks(int count)
    {
        for (int i = 0; i < count; i++) Loop.RunTick();
    }

    // Two players, then ticks until the match runs.
    public (PlayerEntity A, PlayerEntity B) StartMatch()
    {
        PlayerEntity a = Join(1, "qa-a");
        PlayerEntity b = Join(2, "qa-b");
        for (int i = 0; i < 200 && !Match.Flow.InMatch; i++) Loop.RunTick();
        Assert.True(Match.Flow.InMatch);
        return (a, b);
    }

    public async Task<(int Status, JsonElement Body)> Run(QaWork work)
    {
        Task<QaResult> task = Qa.SubmitAsync(work);
        Loop.RunTick();
        QaResult result = await task;
        return (result.Status, JsonSerializer.SerializeToElement(result.Body, result.Body.GetType(), QaHttpService.Json));
    }

    public Task<(int Status, JsonElement Body)> Command(string command, string? player = null, object? args = null)
    {
        JsonElement json = JsonSerializer.SerializeToElement(args ?? new { });
        return Run(t => QaCommands.Execute(t, command, player, "test-run", json, NullLogger.Instance));
    }

    public static JsonElement Result(JsonElement body) => body.GetProperty("result");

    public void Dispose() => Loop.Dispose();
}
