using System.Net;
using System.Net.Sockets;
using System.Numerics;
using ProjectH.Bots;
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Tests.Bots;

// Phase 7 D5-D7, D9: the bot's sight, aim, steering and options.
public class BotPartsTests
{
    [Fact]
    public void LineOfSight_OpenGround_IsClear()
    {
        Assert.True(LineOfSight.Clear(new Vector3(0f, 1.6f, 0f), new Vector3(0f, 1.2f, 10f), GameMap.Boxes, GameMap.Terrain));
        Assert.True(LineOfSight.Clear(new Vector3(0f, 1.6f, 0f), new Vector3(0f, 1.6f, 0.2f), GameMap.Boxes, GameMap.Terrain));   // shorter than a step
    }

    [Fact]
    public void LineOfSight_IsBlockedByAWall_AndByAHill()
    {
        Assert.False(LineOfSight.Clear(new Vector3(24f, 1.6f, 0f), new Vector3(28f, 1.2f, 0f), GameMap.Boxes, GameMap.Terrain));
        Assert.False(LineOfSight.Clear(new Vector3(-30f, 1.6f, 46f), new Vector3(25f, 1.2f, 46f), GameMap.Boxes, GameMap.Terrain));
    }

    [Fact]
    public void Aim_MatchesTheServersDirection()
    {
        var eye = new Vector3(3f, 1.6f, -2f);
        foreach (Vector3 target in new[] { new Vector3(10f, 1.2f, 5f), new Vector3(-20f, 4f, -1f), new Vector3(3f, 0f, 30f) })
        {
            BotAim.Solve(eye, target, out float yaw, out float pitch);
            Assert.True(CombatRules.TryAimDirection(yaw, pitch, out Vector3 direction));
            Assert.True(Vector3.Dot(direction, Vector3.Normalize(target - eye)) > 0.99999f, $"target {target}");
        }
    }

    [Fact]
    public void AimError_StaysWithinItsBound()
    {
        var rng = new Random(3);
        for (int i = 0; i < 1000; i++)
        {
            float distance = i % 80;
            float e = BotAim.RollError(rng, distance);
            Assert.InRange(e, -BotAim.MaxError(distance), BotAim.MaxError(distance));
        }
    }

    [Fact]
    public void Steering_WalksStraight_WhileMoving()
    {
        var s = new BotSteering();
        var rng = new Random(1);
        s.Reset(Vector3.Zero, new Vector3(10f, 0f, 0f), 0f);
        for (int i = 0; i < 30; i++)
        {
            var at = new Vector3(i * 0.15f, 0f, 0f);
            s.Steer(at, new Vector3(10f, 0f, 0f), i / 30f, rng, out float yaw, out bool jump, out bool giveUp);
            Assert.Equal(90f, yaw, 3);
            Assert.False(jump);
            Assert.False(giveUp);
        }
    }

    [Fact]
    public void Steering_WhenStuck_JumpsThenDetours_ThenGivesUp()
    {
        var s = new BotSteering();
        var rng = new Random(1);
        var goal = new Vector3(0f, 0f, 10f);
        s.Reset(Vector3.Zero, goal, 0f);
        bool jumped = false, detoured = false, gaveUp = false;
        for (int i = 0; i <= 30 * 9; i++)
        {
            s.Steer(Vector3.Zero, goal, i / 30f, rng, out float yaw, out bool jump, out bool giveUp);
            if (jump) jumped = true;
            if (jumped && BotTestView.AngleBetween(yaw, 0f) > 45f) detoured = true;   // the goal is due +Z (yaw 0)
            if (giveUp) gaveUp = true;
        }
        Assert.True(jumped, "never jumped");
        Assert.True(detoured, "never detoured");
        Assert.True(gaveUp, "never gave up");
    }

    [Fact]
    public void Steering_Progress_PostponesGivingUp()
    {
        var s = new BotSteering();
        var rng = new Random(1);
        var goal = new Vector3(0f, 0f, 100f);
        s.Reset(Vector3.Zero, goal, 0f);
        for (int i = 0; i <= 30 * 20; i++)
        {
            s.Steer(new Vector3(0f, 0f, i * 0.1f), goal, i / 30f, rng, out _, out _, out bool giveUp);
            Assert.False(giveUp, $"gave up at tick {i} while closing in");
        }
    }

    [Fact]
    public void Options_Parse_AndValidate()
    {
        Assert.True(BotOptions.TryParse(new[] { "--count", "16", "--port", "7000", "--seed", "4", "--host", "10.0.0.2" }, out BotOptions o, out string? error), error);
        Assert.Equal(16, o.Count);
        Assert.Equal(7000, o.Port);
        Assert.Equal(4, o.Seed);
        Assert.Equal("10.0.0.2", o.Host);
        Assert.Equal("bot-016", o.BotName(15));

        Assert.True(BotOptions.TryParse(Array.Empty<string>(), out BotOptions defaults, out _));
        Assert.Equal(1, defaults.Count);
        Assert.Equal(7777, defaults.Port);

        Assert.False(BotOptions.TryParse(new[] { "--count", "0" }, out _, out _));
        Assert.False(BotOptions.TryParse(new[] { "--count", "51" }, out _, out _));
        Assert.False(BotOptions.TryParse(new[] { "--count" }, out _, out _));
        Assert.False(BotOptions.TryParse(new[] { "--count", "many" }, out _, out _));
        Assert.False(BotOptions.TryParse(new[] { "--speed", "3" }, out _, out _));
        Assert.False(BotOptions.TryParse(new[] { "--port", "70000" }, out _, out _));

        Assert.True(BotOptions.TryParse(new[] { "--name-prefix", "bot" }, out _, out _));
        Assert.False(BotOptions.TryParse(new[] { "--name-prefix", new string('가', 20) }, out _, out string? tooLong));   // 20 characters, 60+ bytes
        Assert.Contains("bytes", tooLong);
    }

    [Fact]
    public void Runner_LogsEachDisconnectOnce_AndStopsWhenAllAreGone()
    {
        int port;
        using (var probe = new UdpClient(0)) port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;   // free now, nobody listens

        var lines = new List<string>();
        var options = new BotOptions { Port = port, Count = 2, ConnectIntervalMs = 0, DurationSeconds = 0, StatsIntervalSeconds = 1000 };
        using var runner = new BotRunner(options, lines.Add);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        runner.Run(cts.Token);

        Assert.False(cts.IsCancellationRequested, "Run did not return on its own");
        Assert.Equal(1, lines.Count(l => l.StartsWith("bot-001 disconnected:")));
        Assert.Equal(1, lines.Count(l => l.StartsWith("bot-002 disconnected:")));
        Assert.Equal(1, lines.Count(l => l == "All bots disconnected."));
    }
}
