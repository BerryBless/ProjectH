using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectH.Bots;
using ProjectH.Server.Game;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Tests.Bots;

// Phase 13 D17 (request §133-§137): the bots' building: a wall against a hit, a ramp up to a higher target, and the
// --build-spam load mode, plus one run against a real server.
public class BotBuilderTests
{
    private const float Dt = 1f / 30f;

    private static BotView View(ToolKind tool = ToolKind.Weapon)
    {
        BotView view = BotTestView.Create(new Vector3(2.5f, 0f, 2.5f));   // plaza cell 16
        view.Self = new SnapshotSelf { Health = 100, Tool = tool };
        return view;
    }

    // Steps the builder like BotRunner: the brain's command (empty here) plus the builder's additions.
    private static List<(InputCommand Command, BuildRequest? Request)> Run(BotBuilder builder, BotView view, float start, int ticks, ushort target = 0)
    {
        var result = new List<(InputCommand, BuildRequest?)>();
        for (int i = 0; i < ticks; i++)
        {
            var command = new InputCommand { Buttons = InputButtons.Fire };
            bool send = builder.Tick(view, start + i * Dt, target, ref command, out BuildRequest request);
            result.Add((command, send ? request : null));
            // The server's answer to a tool press shows in the next snapshot.
            if ((command.Buttons & InputButtons.ToolBuild) != 0) view.Self = view.Self with { Tool = ToolKind.Build };
            if ((command.Buttons & InputButtons.Slot1) != 0) view.Self = view.Self with { Tool = ToolKind.Weapon };
        }
        return result;
    }

    [Fact]
    public void AHit_GetsAWallTowardsTheAttacker_ThenTheWeaponsComeOut()
    {
        BotView view = View();
        var builder = new BotBuilder(seed: 1, chance: 1f);
        view.ApplyDamage(new DamageTaken { AttackerId = 5, Damage = 20, FromDirection = new Vector3(1f, 0f, 0.2f) });
        var steps = Run(builder, view, 10f, 6);
        Assert.True((steps[0].Command.Buttons & InputButtons.ToolBuild) != 0);
        BuildRequest request = Assert.Single(steps, s => s.Request.HasValue).Request!.Value;
        Assert.Equal((byte)BuildPieceType.Wall, request.Piece);
        Assert.Equal((16, 0, 16, 3), (request.X, request.Y, request.Z, request.Rotation));   // the east edge
        Assert.Equal(1, request.Sequence);
        Assert.Contains(steps, s => (s.Command.Buttons & InputButtons.Slot1) != 0);
        Assert.Equal(ToolKind.Weapon, view.Self.Tool);
        // The aim at the request's tick points at the wall (east of the bot).
        var sent = steps.Find(s => s.Request.HasValue).Command;
        Assert.InRange(sent.AimYaw, 60f, 120f);
        Assert.Equal(1, builder.Requests);
    }

    [Fact]
    public void WithoutLuck_OrInCooldown_NoWall()
    {
        BotView view = View();
        var never = new BotBuilder(seed: 1, chance: 0f);
        view.ApplyDamage(new DamageTaken { FromDirection = Vector3.UnitX });
        Assert.DoesNotContain(Run(never, view, 10f, 10), s => s.Request.HasValue);

        var always = new BotBuilder(seed: 1, chance: 1f);
        view.ApplyDamage(new DamageTaken { FromDirection = Vector3.UnitX });
        Assert.Single(Run(always, view, 10f, 10), s => s.Request.HasValue);
        view.ApplyDamage(new DamageTaken { FromDirection = Vector3.UnitX });
        Assert.DoesNotContain(Run(always, view, 11f, 10), s => s.Request.HasValue);   // within the 3 s cooldown
        view.ApplyDamage(new DamageTaken { FromDirection = Vector3.UnitX });
        Assert.Single(Run(always, view, 14f, 10), s => s.Request.HasValue);
    }

    // Final review C: a hit during the cooldown is used up then (no wall when the cooldown ends), a death drops the
    // placement in progress, a placement that never reaches build mode is given up, and Q is not pressed in build mode.
    [Fact]
    public void HitsAreTakenAtOnce_DeathAndTimeoutDropThePlacement_AndBuildModeIsNotLeft()
    {
        BotView view = View();
        var always = new BotBuilder(seed: 1, chance: 1f);
        view.ApplyDamage(new DamageTaken { FromDirection = Vector3.UnitX });
        Assert.Single(Run(always, view, 10f, 10), s => s.Request.HasValue);
        view.ApplyDamage(new DamageTaken { FromDirection = Vector3.UnitX });
        Assert.DoesNotContain(Run(always, view, 11f, 10), s => s.Request.HasValue);   // within the cooldown
        Assert.DoesNotContain(Run(always, view, 14f, 10), s => s.Request.HasValue);   // and not later either

        // Dies while aiming: nothing is sent after the respawn.
        var dying = new BotBuilder(seed: 1, chance: 1f);
        BotView v2 = View();
        v2.ApplyDamage(new DamageTaken { FromDirection = Vector3.UnitX });
        var command = new InputCommand();
        dying.Tick(v2, 20f, 0, ref command, out _);                       // presses Q (phase 1)
        v2.Alive = false;
        command = new InputCommand();
        dying.Tick(v2, 20.1f, 0, ref command, out _);
        v2.Alive = true;
        v2.Self = v2.Self with { Tool = ToolKind.Build };
        Assert.DoesNotContain(Run(dying, v2, 20.2f, 10), s => s.Request.HasValue);

        // Build mode never comes: given up after the timeout.
        var stuck = new BotBuilder(seed: 1, chance: 1f);
        BotView v3 = View();
        v3.ApplyDamage(new DamageTaken { FromDirection = Vector3.UnitX });
        for (int i = 0; i < 90; i++)
        {
            command = new InputCommand();
            Assert.False(stuck.Tick(v3, 30f + i * Dt, 0, ref command, out _));   // the tool stays Weapon
        }
        v3.Self = v3.Self with { Tool = ToolKind.Build };
        Assert.DoesNotContain(Run(stuck, v3, 33.1f, 5), s => s.Request.HasValue);

        // Already in build mode: the placement goes without a Q press.
        var inMode = new BotBuilder(seed: 1, chance: 1f);
        BotView v4 = View(ToolKind.Build);
        v4.ApplyDamage(new DamageTaken { FromDirection = Vector3.UnitX });
        var steps = Run(inMode, v4, 40f, 3);
        Assert.DoesNotContain(steps, s => (s.Command.Buttons & InputButtons.ToolBuild) != 0);
        Assert.Single(steps, s => s.Request.HasValue);
    }

    [Fact]
    public void AHigherTarget_GetsARampTowardsIt()
    {
        BotView view = View();
        view.Others[0] = new SnapshotEntity { EntityId = 9, Position = new Vector3(2.5f, 3.2f, 9f), Flags = SnapshotEntity.MakeFlags(true, MovementMode.Ground, false, false) };
        view.OtherCount = 1;
        var builder = new BotBuilder(seed: 1, chance: 0f);
        var steps = Run(builder, view, 10f, 6, target: 9);
        BuildRequest request = Assert.Single(steps, s => s.Request.HasValue).Request!.Value;
        Assert.Equal((byte)BuildPieceType.Ramp, request.Piece);
        Assert.Equal((16, 0, 17, 0), (request.X, request.Y, request.Z, request.Rotation));   // the next cell north, rising north
    }

    [Fact]
    public void BuildSpam_StaysInBuildMode_AndSendsAboutNASecond_ToDifferentSlots()
    {
        BotView view = View();
        var builder = new BotBuilder(seed: 1, spamPerSecond: 10);
        var steps = Run(builder, view, 10f, 90);   // 3 s
        int sent = 0;
        var slots = new HashSet<uint>();
        foreach (var s in steps)
        {
            Assert.True((s.Command.Buttons & (InputButtons.Fire | InputButtons.Slot1)) == 0);
            if (!s.Request.HasValue) continue;
            sent++;
            BuildRequest r = s.Request.Value;
            Assert.True(BuildGrid.TryNormalize((BuildPieceType)r.Piece, r.X, r.Y, r.Z, r.Rotation, out BuildPieceShape shape));
            slots.Add(BuildGrid.SlotKey(shape));
        }
        Assert.InRange(sent, 27, 31);
        Assert.True(slots.Count >= 27);
        Assert.Equal(ToolKind.Build, view.Self.Tool);
    }

    [Fact]
    public void ATick_AllocatesNothing()
    {
        BotView view = View();
        var builder = new BotBuilder(seed: 1, spamPerSecond: 20);
        for (int i = 0; i < 60; i++)
        {
            var c = new InputCommand();
            builder.Tick(view, i * Dt, 0, ref c, out _);
            view.Self = view.Self with { Tool = ToolKind.Build };
        }
        long before = System.GC.GetAllocatedBytesForCurrentThread();
        for (int i = 60; i < 360; i++)
        {
            var c = new InputCommand();
            builder.Tick(view, i * Dt, 0, ref c, out _);
        }
        Assert.Equal(before, System.GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void TheBuildSpamOption_IsParsedAndBounded()
    {
        Assert.True(BotOptions.TryParse(new[] { "--build-spam", "5" }, out BotOptions options, out _));
        Assert.Equal(5, options.BuildSpam);
        Assert.False(BotOptions.TryParse(new[] { "--build-spam", "25" }, out _, out string? error));
        Assert.Contains("--build-spam", error);
        // --build false turns building off (load scenario A); spam then makes no sense.
        Assert.True(BotOptions.TryParse(new[] { "--build", "false" }, out options, out _));
        Assert.False(options.Build);
        Assert.True(new BotOptions().Build);
        Assert.False(BotOptions.TryParse(new[] { "--build", "false", "--build-spam", "5" }, out _, out error));
        Assert.Contains("--build true", error);
    }

    [Fact]
    public void ASpammingBot_BuildsOnARealServer()
    {
        var server = new GameLoop(new ServerOptions
        {
            Port = 0, MaxPlayers = 4, DevRespawn = true, BuildInfiniteResources = true, DisconnectTimeoutMs = 3000, StatsIntervalSeconds = 60,
        }, TestGameData.Create(), NullLogger.Instance);
        server.Start();
        using (server)
        {
            using var bots = new BotRunner(new BotOptions { Port = server.LocalPort, Count = 1, ConnectIntervalMs = 0, BuildSpam = 10 }, _ => { });
            BotView view = bots.Connection(0).View;
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 15000 && view.BuildResults[(int)BuildResultCode.Ok] < 3)
            {
                bots.Step();
                System.Threading.Thread.Sleep(33);
            }
            Assert.True(view.BuildResults[(int)BuildResultCode.Ok] >= 3, $"accepted {view.BuildResults[0]}, results {string.Join(",", view.BuildResults)}");
            Assert.True(view.Pieces.Count >= 3);
            Assert.True(server.Health.Build.Accepted >= 3);
        }
    }
}
