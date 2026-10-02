using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ProjectH.Client.Game;
using ProjectH.Server.Game;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 12 D9 (spec §2 문): the door boxes, the collision world, the E rule (and the client's copy of it), opening and
// closing, the occupied doorway, the shoulder bash, DoorStates and the round start. Door 0 is Rustvale's first house's
// south door: centre (-54, z 50.25), 1.5 m wide; the tests stand south of it, facing north (yaw 0).
public class DoorTests
{
    private static readonly Box Door0 = GameMap.Doors[0];
    private static readonly Vector3 SouthOfDoor0 = Ground(-54f, 48.5f);

    private static Vector3 Ground(float x, float z) => new(x, GameMap.Terrain.Height(x, z), z);

    private readonly List<(int Peer, PacketId Id, byte[] Data)> _sent = new();
    private readonly Dictionary<int, uint> _seq = new();
    private readonly Match _match;

    public DoorTests()
    {
        _match = new Match(new ServerOptions { MaxPlayers = 4, DevRespawn = true }, TestGameData.Create(),
            (peer, data, _) => _sent.Add((peer, (PacketId)data[0], data.ToArray())), TestGameData.CombatLoadout);
    }

    private PlayerEntity Join(int peer, Vector3 feet, float yaw = 0f)
    {
        Assert.Equal(JoinResult.Ok, _match.TryJoin(peer, "p" + peer));
        _match.TryGetPlayer(peer, out var player);
        player.State.Position = feet;
        player.State.Yaw = yaw;
        player.History.Reset(_match.ServerTick, feet);
        return player;
    }

    private void Send(PlayerEntity player, InputCommand command)
    {
        _seq.TryGetValue(player.PeerId, out uint seq);
        command.Seq = ++seq;
        _seq[player.PeerId] = seq;
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, command);
        _match.EnqueueInput(player.PeerId, packet);
    }

    private void Press(PlayerEntity player, InputButtons buttons, float yaw = 0f)
    {
        Send(player, new InputCommand { Buttons = buttons, Yaw = yaw });
        _match.Tick();
    }

    private List<byte> DoorStatesTo(int peer) => _sent.Where(s => s.Peer == peer && s.Id == PacketId.DoorStates).Select(s =>
    {
        var r = new PacketReader(s.Data);
        r.TryReadPacketId(out _);
        Assert.True(DoorStatesPacket.TryRead(ref r, out byte mask));
        return mask;
    }).ToList();

    // ---- The map's doors ----

    [Fact]
    public void FiveDoors_FillTheirWallGaps_AndTouchNoOtherBox()
    {
        ReadOnlySpan<Box> doors = GameMap.Doors;
        Assert.Equal(GameMap.DoorCount, doors.Length);
        for (int i = 0; i < doors.Length; i++)
        {
            Box d = doors[i];
            Assert.Equal(1.5f, d.Size.X, 4);
            Assert.Equal(GameMap.DoorThickness, d.Size.Z, 4);
            Assert.Equal(3f, d.Size.Y, 4);
            // A wall segment ends exactly at each side of the door, and the door sits inside the wall's thickness.
            int sides = 0;
            foreach (Box b in GameMap.Boxes)
            {
                bool sameWall = b.Min.Z <= d.Min.Z && b.Max.Z >= d.Max.Z && b.Max.Y == d.Max.Y;
                if (sameWall && (MathF.Abs(b.Max.X - d.Min.X) < 1e-4f || MathF.Abs(b.Min.X - d.Max.X) < 1e-4f)) sides++;
            }
            Assert.Equal(2, sides);
            Assert.False(Overlaps(d), $"door {i} overlaps a box");
        }
    }

    private static bool Overlaps(Box d)
    {
        foreach (Box b in GameMap.Boxes)
        {
            if (d.Min.X < b.Max.X && d.Max.X > b.Min.X && d.Min.Y < b.Max.Y && d.Max.Y > b.Min.Y && d.Min.Z < b.Max.Z && d.Max.Z > b.Min.Z)
                return true;
        }
        return false;
    }

    [Fact]
    public void DoorStates_WithABitForNoDoor_IsRefused()
    {
        var buffer = new byte[2];
        var writer = new PacketWriter(buffer);
        DoorStatesPacket.Write(ref writer, 1 << GameMap.DoorCount);
        var reader = new PacketReader(buffer);
        reader.TryReadPacketId(out _);
        Assert.False(DoorStatesPacket.TryRead(ref reader, out _));
    }

    [Fact]
    public void TheDoorSet_PutsClosedDoorsInTheWorld_AndOnlyThose()
    {
        var doors = new DoorSet();
        int boxes = GameMap.Boxes.Length;
        Assert.Equal(boxes + GameMap.DoorCount, doors.World.Length);
        Assert.Equal(3, doors.DoorAt(boxes + 3));
        Assert.Equal(-1, doors.DoorAt(0));

        doors.Set(1, true);
        Assert.Equal(0b10, doors.OpenMask);
        Assert.Equal(boxes + GameMap.DoorCount - 1, doors.World.Length);
        Assert.Equal(2, doors.DoorAt(boxes + 1));   // door 1 left the world: the next closed one moved up
        Assert.Equal(-1, doors.DoorAt(boxes + GameMap.DoorCount - 1));

        doors.CloseAll();
        Assert.Equal(0, doors.OpenMask);
        Assert.Equal(boxes + GameMap.DoorCount, doors.World.Length);
    }

    // Final review C15: the client's predicted door world is the server's: the same boxes in the same order and the same
    // door at every index, for every open mask.
    [Fact]
    public void TheClientsDoorWorld_IsTheServers_ForEveryMask()
    {
        var server = new DoorSet();
        var client = new PredictedDoors();
        for (int mask = 0; mask < 1 << GameMap.DoorCount; mask++)
        {
            for (int d = 0; d < GameMap.DoorCount; d++) server.Set(d, (mask & (1 << d)) != 0);
            client.ApplyServer((byte)mask);
            Assert.Equal(server.OpenMask, client.OpenMask);
            Assert.Equal(server.World.ToArray(), client.World.ToArray());
            for (int i = -1; i <= server.World.Length; i++) Assert.Equal(server.DoorAt(i), client.DoorAt(i));
        }
    }

    // ---- The E rule ----
    // ---- The E rule ----

    [Fact]
    public void TheRule_TakesTheNearestDoor_InFront_WithinTwoAndAHalfMetres()
    {
        Assert.Equal(0, DoorRules.FindTarget(SouthOfDoor0, 0f, GameMap.Doors));
        Assert.Equal(0, DoorRules.FindTarget(SouthOfDoor0, 55f, GameMap.Doors));        // within 60 degrees
        Assert.Equal(-1, DoorRules.FindTarget(SouthOfDoor0, 70f, GameMap.Doors));       // beyond
        Assert.Equal(-1, DoorRules.FindTarget(SouthOfDoor0, 180f, GameMap.Doors));      // facing away
        Assert.Equal(-1, DoorRules.FindTarget(Ground(-54f, 47.6f), 0f, GameMap.Doors)); // 2.65 m away
        Assert.Equal(-1, DoorRules.FindTarget(new Vector3(-54f, 3.25f, 50.25f), 0f, GameMap.Doors));   // on the roof
    }

    [Fact]
    public void TheClientsCopy_PicksTheSameDoor()
    {
        var random = new Random(12);
        for (int n = 0; n < 20_000; n++)
        {
            Box door = GameMap.Doors[random.Next(GameMap.DoorCount)];
            var feet = new Vector3(door.Center.X + (float)(random.NextDouble() * 8 - 4), (float)(random.NextDouble() * 5 - 1),
                door.Center.Z + (float)(random.NextDouble() * 8 - 4));
            float yaw = (float)(random.NextDouble() * 720 - 360);
            Assert.Equal(DoorRules.FindTarget(feet, yaw, GameMap.Doors), DoorRule.FindTarget(feet, yaw, GameMap.Doors));
        }
    }

    // ---- Opening and closing (Match) ----

    [Fact]
    public void AClosedDoor_Blocks_E_OpensIt_AndTheDoorwayPasses()
    {
        PlayerEntity a = Join(1, SouthOfDoor0);
        for (int i = 0; i < 30; i++)
        {
            Send(a, new InputCommand { MoveY = 1f, Yaw = 0f });
            _match.Tick();
        }
        Assert.True(a.State.Position.Z + MoveSettings.HalfWidth <= Door0.Min.Z);

        Press(a, InputButtons.Interact);
        Assert.True(_match.Doors.IsOpen(0));
        Assert.Equal(new byte[] { 0, 1 }, DoorStatesTo(1));   // at the join, then the change
        for (int i = 0; i < 30; i++)
        {
            Send(a, new InputCommand { MoveY = 1f, Yaw = 0f });
            _match.Tick();
        }
        Assert.True(a.State.Position.Z > Door0.Max.Z + 0.5f);
    }

    [Fact]
    public void E_OnAnOpenDoor_ClosesIt_UnlessSomeoneStandsInTheDoorway()
    {
        PlayerEntity a = Join(1, SouthOfDoor0);
        PlayerEntity b = Join(2, Ground(-54f, 50.25f));   // in the doorway (the door is closed: b stands in it)
        _match.Doors.Set(0, true);
        _match.Tick();

        Press(a, InputButtons.Interact);
        Assert.True(_match.Doors.IsOpen(0));   // b is in the way

        b.State.Position = Ground(-54f, 53f);  // inside the house
        Press(a, InputButtons.None);   // final review A5: released between presses
        Press(a, InputButtons.Interact);
        Assert.False(_match.Doors.IsOpen(0));
    }

    // Phase 13 final review A5: E held over several inputs (a modified client) toggles once; a new press toggles again.
    [Fact]
    public void HeldE_TogglesTheDoorOnce()
    {
        PlayerEntity a = Join(1, SouthOfDoor0);
        for (int i = 0; i < 4; i++) Press(a, InputButtons.Interact);
        Assert.True(_match.Doors.IsOpen(0));
        Press(a, InputButtons.None);
        Press(a, InputButtons.Interact);
        Assert.False(_match.Doors.IsOpen(0));
    }

    [Fact]
    public void E_ActsOnTheDoorFirst_AndOnItemsWithoutADoorInFront()
    {
        PlayerEntity a = Join(1, SouthOfDoor0);
        Press(a, InputButtons.Interact);
        Assert.True(_match.Doors.IsOpen(0));
        Assert.DoesNotContain(_sent, s => s.Peer == 1 && s.Id == PacketId.PickupResult);

        Press(a, InputButtons.None, yaw: 180f);   // final review A5: released between presses
        Press(a, InputButtons.Interact, yaw: 180f);   // facing away: no door, so a pickup (nothing here)
        Assert.True(_match.Doors.IsOpen(0));
        Assert.Single(_sent, s => s.Peer == 1 && s.Id == PacketId.PickupResult);
    }

    [Fact]
    public void SprintingIntoAClosedDoor_ShouldersItOpen_WalkingDoesNot()
    {
        PlayerEntity walker = Join(1, SouthOfDoor0);
        for (int i = 0; i < 30; i++)
        {
            Send(walker, new InputCommand { MoveY = 1f, Yaw = 0f });
            _match.Tick();
        }
        Assert.False(_match.Doors.IsOpen(0));

        walker.State.Position = Ground(-54f, 46f);
        for (int i = 0; i < 30 && !_match.Doors.IsOpen(0); i++)
        {
            Send(walker, new InputCommand { MoveY = 1f, Yaw = 0f, Buttons = InputButtons.Sprint });
            _match.Tick();
        }
        Assert.True(_match.Doors.IsOpen(0));
        for (int i = 0; i < 20; i++)
        {
            Send(walker, new InputCommand { MoveY = 1f, Yaw = 0f, Buttons = InputButtons.Sprint });
            _match.Tick();
        }
        Assert.True(walker.State.Position.Z > Door0.Max.Z + 0.5f);
    }

    // Final review C11: a sprint into the doorway a little off-centre, diagonally: the X sweep meets the jamb's side and
    // the Z sweep the door. The door is the one that counts, so it is shouldered open.
    private static readonly Vector3 OffCentre = Ground(-53.70f, 49.70f);   // 0.05 m from the right jamb, in the wall's Z range
    private static readonly InputCommand SprintDiagonal = new() { MoveX = 1f, MoveY = 1f, Yaw = 0f, Buttons = InputButtons.Sprint };

    [Fact]
    public void ADiagonalSprint_IntoTheJambAndTheDoor_ReportsBoth()
    {
        var doors = new DoorSet();
        var s = new MoveState { Position = OffCentre };
        // Phase 13 D3: the world gathered around the character names its colliders by kind and id.
        var world = new CollisionWorld();
        world.Gather(s.Position, doors.OpenMask, 0UL, null);
        MovementSimulation.Step(ref s, SprintDiagonal, 1f / 30f, world, GameMap.Terrain, out StepResult r);
        Assert.True(r.Charging);
        Assert.Equal(ColliderKind.Static, r.BlockedBy.Kind);                  // the X sweep: the jamb
        Assert.Equal(new ColliderId(ColliderKind.Door, 0), r.BlockedByZ);     // the Z sweep: door 0
        Assert.Equal(0, doors.DoorBlocking(r));
        Assert.Equal(0, new PredictedDoors().DoorBlocking(r));
    }

    [Fact]
    public void ADiagonalSprint_OffCentre_ShouldersTheDoorOpen()
    {
        PlayerEntity a = Join(1, OffCentre);
        Send(a, SprintDiagonal);
        _match.Tick();
        Assert.True(_match.Doors.IsOpen(0));
    }

    // Final review B5: a vault moves without collision, so the rest of a vaulter's straight path through the doorway
    // occupies the door: E cannot close it then. With one tick left (the path ends short of the door) it closes.
    [Theory]
    [InlineData(6, true)]
    [InlineData(1, false)]
    public void E_CannotCloseADoor_AcrossAVaultersRemainingPath(int ticksLeft, bool staysOpen)
    {
        PlayerEntity a = Join(1, SouthOfDoor0);
        // Joined after a, so it has not moved yet when a's E is handled in the same tick.
        PlayerEntity b = Join(2, Ground(-54f, 49.0f));
        _match.Doors.Set(0, true);
        _match.Tick();
        b.State.Position = Ground(-54f, 49.0f);
        b.State.Mode = MovementMode.Vault;
        b.State.ModeTicks = (byte)ticksLeft;
        b.State.HorizontalVelocity = new Vector2(0f, 6f);   // 0.2 m a tick, north through the doorway
        b.State.VelocityY = 0f;
        Assert.False(MovementSimulation.OverlapsAny(b.State.Position, GameMap.Doors.Slice(0, 1)));

        Press(a, InputButtons.Interact);
        Assert.Equal(staysOpen, _match.Doors.IsOpen(0));
    }

    [Fact]
    public void AClosedDoor_StopsAShot_AnOpenOneDoesNot()
    {
        PlayerEntity shooter = Join(1, Ground(-54f, 46f));
        PlayerEntity target = Join(2, Ground(-54f, 53f));
        int Hits() => _sent.Count(s => s.Peer == 1 && s.Id == PacketId.HitConfirmed);
        void Shoot()
        {
            TestAim.YawPitch(shooter.State.Position, target.State.Position + new Vector3(0f, 1.2f, 0f), out float yaw, out float pitch);
            Send(shooter, new InputCommand { Buttons = InputButtons.Fire, AimYaw = yaw, AimPitch = pitch, ViewTick = _match.ServerTick });
            _match.Tick();
            for (int i = 0; i < 3; i++) _match.Tick();   // the weapon's fire interval
        }
        Shoot();
        Assert.Equal(0, Hits());
        _match.Doors.Set(0, true);
        Shoot();
        Assert.Equal(1, Hits());
    }

    [Fact]
    public void ANewcomer_GetsTheDoors_AndChangesGoToEveryone()
    {
        _match.Doors.Set(3, true);
        Join(1, Ground(0f, 3f));
        Assert.Equal(new byte[] { 0b1000 }, DoorStatesTo(1));
        Join(2, Ground(0f, -3f));
        _match.Doors.Set(4, true);
        _match.Tick();
        Assert.Equal(0b11000, DoorStatesTo(1).Last());
        Assert.Equal(0b11000, DoorStatesTo(2).Last());
        _match.Tick();
        Assert.Equal(2, DoorStatesTo(1).Count);   // nothing more without a change
    }

    [Fact]
    public void TheMatchStart_ClosesEveryDoor()
    {
        var h = new RoyaleHarness();
        h.Join(1);
        h.Join(2);
        h.Match.Doors.Set(0, true);
        h.Match.Doors.Set(2, true);
        h.RunToMatch();
        Assert.Equal(0, h.Match.Doors.OpenMask);
        var last = h.SentTo(1, PacketId.DoorStates).Last();
        Assert.Equal(0, last.Data[1]);
    }
}
