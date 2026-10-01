# Phase 8 Optimization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Phase 7 측정에서 드러난 Snapshot 병목 두 가지를 고친다.
- 인원 상한 50명 → 100명: Snapshot을 최대 2개 패킷으로 나눈다.
- 대역폭: 엔티티를 23 B에서 13 B로 양자화한다.

그 다음 다시 측정해 50·64·100명 결과를 남긴다. Client는 측정 절차를 문서로 만들고 정적 점검을 한다. 측정 없이 Client 코드를 고치지 않는다.

**Architecture:**
- **Protocol v7.**
  - `WorldSnapshotHeader`에 `Part`, `PartCount`를 더한다(19 B).
  - `SnapshotEntity`는 위치·VelocityY를 1/256 고정소수 16비트로, Yaw를 16비트로 보낸다(13 B).
- **Server.** 플레이어를 90명씩 나눠 Part마다 한 버퍼를 만들고, 수신자별로 Ack·Self만 고쳐 보낸다.
- **수신 쪽.** 봇과 테스트 Client는 같은 Tick의 패킷을 더한다.
- **Unity Client.** 코드가 바뀌지 않는다. 패킷마다 엔티티를 보간기에 넣고, 제거는 이벤트로 한다.

**Tech Stack:** .NET 10, C# 9(Shared), xUnit, NUnit(Client EditMode, 스크래치), LiteNetLib 2.1.4. 새 패키지는 없다.

**Spec:** `Docs/specs/2026-10-01-phase8-optimization-design.md`

## Global Constraints

- **Commit:** 작업 Branch에서 Task마다 Commit한다. Push는 Phase가 끝난 뒤 `github-push` 스킬로 한다. Force Push는 하지 않는다.
- **코드 규칙:** 모든 코드는 `.claude/skills/game-core-rules/SKILL.md`를 따른다. `Shared/Runtime`은 netstandard2.1, C# 9이고 UnityEngine을 쓰지 않는다.
- **수치(spec):**
  - `MaxSnapshotEntities = 100`, `MaxEntitiesPerSnapshotPacket = 90`, `MaxSnapshotParts = 2`, `ProtocolVersion = 7`
  - 헤더 19 B(`SelfOffset` 13), 엔티티 13 B, 패킷 최대 1189 B ≤ 1200
  - 고정소수 1/256(±128), Yaw 65536/360
- **서버 CPU·할당·Lock 최적화는 하지 않는다(D1).** Snapshot 송신은 할당이 없어야 한다(테스트로 고정한다).
- **Unity Client 코드(`Client/Assets/Scripts`)는 바꾸지 않는다.** 테스트 하나만 더한다(`Client/Assets/Tests/EditMode/MapPredictionTests.cs`). Scene, Prefab, `.meta`는 만들거나 고치지 않는다.
- **부하 측정 프로세스:** 7790 같은 빈 포트를 쓴다. 자기가 띄운 프로세스는 pid로만 끈다. 이름으로 끄지 않는다(`taskkill /IM`, `pkill -f` 금지).
- **명령 실행 위치:** 저장소 루트(`E:/popol/ProjectH`)에서 실행한다. 서버 테스트 명령은 `dotnet test Server/ProjectH.Server.slnx`다. 시작 기준은 635개다.

## Review Focus

- **100명 경기에서 Snapshot 한 패킷을 잃는 경우.** 남은 패킷만으로 적용되어야 한다. 봇·테스트 Client의 목록이 섞이거나 남지 않아야 한다(Task 1 `ASnapshotInTwoParts_AddsUp_AndANewTickStartsOver`).
- **경사면이나 박스 위에서 양자화된 내 위치를 받는 경우.** 예측 보정이 일어나면 안 된다(Task 2 `QuantizedSnapshots_OverAHill_NeverCorrectThePrediction`).
- **맵 밖·비정상 값(NaN, ±무한대, ±500 m).** 패킷이 깨지지 않고 잘리거나 0이 되어야 한다(Task 1 `SnapshotEntity_Quantization_StaysWithinHalfAStep_AndClamps`).
- **잘못된 헤더(Part ≥ PartCount, 90명 초과).** 받는 쪽에서 거절해야 한다(Task 1 `SnapshotHeader_OutOfRange_IsRejected`).
- **100명이 실제 UDP로 접속하는 경우.** 모든 Client가 100명 전부를 봐야 한다(기존 `FullMatch_SnapshotWithMaxEntities_IsDelivered`가 이제 100명으로 돈다).

## Spec 해석

1. Unity `NetClient`의 엔티티 배열(`MaxSnapshotEntities`, 이제 100)은 패킷당 90명보다 크므로 그대로 둔다(spec §3).
2. 봇과 테스트 Client는 Tick이 바뀔 때만 다른 플레이어 목록을 비운다. 첫 Part를 잃어도 두 번째 Part가 새 Tick 목록을 시작한다.
3. 부하 측정은 Phase 7과 같은 방법을 쓴다(DevRespawn, 2분, 처음 약 20초 버림, Stats 줄 집계, 봇 CPU는 `TotalProcessorTime` 표본).

---

### Task 1: Protocol v7, Snapshot 분할·양자화, 서버·봇·테스트 Client

**Files:**
- Modify: `Shared/Runtime/Protocol/ProtocolConstants.cs`, `Shared/Runtime/Protocol/ServerPackets.cs`
- Modify: `Server/src/ProjectH.Server/Game/Match.cs`, `Server/src/ProjectH.Server/ServerOptions.cs`, `Server/src/ProjectH.Bots/BotView.cs`
- Modify (테스트): `Server/tests/ProjectH.Server.Tests/Integration/HeadlessClient.cs`, `Shared/PacketTests.cs`, `Shared/ProtocolConstantsTests.cs`, `Bots/BotPartsTests.cs`, `Bots/BotBrainTests.cs`, `Integration/ServerIntegrationTests.cs`
- Create (테스트): `Server/tests/ProjectH.Server.Tests/Game/SnapshotSplitTests.cs`

**Interfaces:**
- Produces:
  - `ProtocolConstants.MaxSnapshotEntities` (100), `MaxEntitiesPerSnapshotPacket` (90), `MaxSnapshotParts` (2), `ProtocolVersion` (7)
  - `WorldSnapshotHeader.Part`, `PartCount`, `Size` (19), `SelfOffset` (13)
  - `SnapshotEntity.Size` (13), `FixedScale`, `YawScale`, `static float Quantize(float)`

이 Task의 변경과 테스트는 계획 단계에서 스크래치 사본에서 함께 돌렸다. 서버 643개가 모두 통과했고, 100명 봇 부하도 확인했다(Tick p99 0.30 ms, 2.07 MB/s, 드롭 0). 아래 스크립트들은 그 사본에 적용한 그대로다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

아래 스크립트를 `plan8_tests.py`로 저장소 밖(스크래치)에 저장한다. 그런 다음 `python <경로>/plan8_tests.py E:/popol/ProjectH`로 실행한다. 기존 테스트 여섯 파일을 고친다.

```python
import os, sys
os.chdir(sys.argv[1])

def edit(path, pairs):
    s = open(path, encoding='utf-8', newline='').read()
    nl = '\r\n' if '\r\n' in s else '\n'
    for a, b in pairs:
        a2 = a.replace('\n', nl); b2 = b.replace('\n', nl)
        assert s.count(a2) == 1, (path, a[:70], s.count(a2))
        s = s.replace(a2, b2)
    open(path, 'w', encoding='utf-8', newline='').write(s)

T = 'Server/tests/ProjectH.Server.Tests/'

edit(T + 'Shared/PacketTests.cs', [
("""    [Fact]
    public void Snapshot_RoundTrip_AndRecipientPatch()
    {
        var writer = new PacketWriter(_buffer);
        WorldSnapshotHeader.Write(ref writer, new WorldSnapshotHeader { ServerTick = 7, AckInputSeq = 0, Count = 2 });""",
"""    [Fact]
    public void Snapshot_RoundTrip_AndRecipientPatch()
    {
        var writer = new PacketWriter(_buffer);
        WorldSnapshotHeader.Write(ref writer, new WorldSnapshotHeader { ServerTick = 7, AckInputSeq = 0, Count = 2, Part = 1, PartCount = 2 });"""),
("""        Assert.Equal(2, h.Count);
        Assert.Equal(70, h.Self.Health);""",
"""        Assert.Equal(2, h.Count);
        Assert.Equal(1, h.Part);
        Assert.Equal(2, h.PartCount);
        Assert.Equal(70, h.Self.Health);"""),
("""    // D10: 11 + 6 + 23 * 50 = 1167 bytes must fit one unfragmented datagram (1200).
    [Fact]
    public void Snapshot_WithMaxEntities_Is1167Bytes_AndFitsOneDatagram()
    {
        var writer = new PacketWriter(_buffer);
        WorldSnapshotHeader.Write(ref writer, new WorldSnapshotHeader { ServerTick = uint.MaxValue, Count = ProtocolConstants.MaxSnapshotEntities });
        for (int i = 0; i < ProtocolConstants.MaxSnapshotEntities; i++)""",
"""    // Phase 8 D3: 19 + 13 * 90 = 1189 bytes must fit one unfragmented datagram (1200).
    [Fact]
    public void SnapshotPacket_WithMaxEntities_Is1189Bytes_AndFitsOneDatagram()
    {
        var writer = new PacketWriter(_buffer);
        WorldSnapshotHeader.Write(ref writer, new WorldSnapshotHeader
        {
            ServerTick = uint.MaxValue, Count = ProtocolConstants.MaxEntitiesPerSnapshotPacket, Part = 1, PartCount = ProtocolConstants.MaxSnapshotParts,
        });
        for (int i = 0; i < ProtocolConstants.MaxEntitiesPerSnapshotPacket; i++)"""),
("""        Assert.False(writer.Overflowed);
        Assert.Equal(1167, writer.Length);
        Assert.True(writer.Length <= ProtocolConstants.MaxPacketSize);
    }

    [Fact]
    public void Snapshot_CountAboveLimit_IsRejected()
    {
        var writer = new PacketWriter(_buffer);
        WorldSnapshotHeader.Write(ref writer, new WorldSnapshotHeader { ServerTick = 1, Count = ProtocolConstants.MaxSnapshotEntities + 1 });
        var reader = ReaderAfterId(writer.Length, PacketId.WorldSnapshot);
        Assert.False(WorldSnapshotHeader.TryRead(ref reader, out _));
    }""",
"""        Assert.False(writer.Overflowed);
        Assert.Equal(1189, writer.Length);
        Assert.True(writer.Length <= ProtocolConstants.MaxPacketSize);
    }

    [Theory]
    [InlineData(ProtocolConstants.MaxEntitiesPerSnapshotPacket + 1, 0, 1)]   // too many entities in one packet
    [InlineData(1, 0, 0)]                                                       // no parts
    [InlineData(1, 0, ProtocolConstants.MaxSnapshotParts + 1)]                  // more parts than 100 players need
    [InlineData(1, 1, 1)]                                                       // part index outside the count
    public void SnapshotHeader_OutOfRange_IsRejected(int count, int part, int partCount)
    {
        var writer = new PacketWriter(_buffer);
        WorldSnapshotHeader.Write(ref writer, new WorldSnapshotHeader { ServerTick = 1, Count = (ushort)count, Part = (byte)part, PartCount = (byte)partCount });
        for (int i = 0; i < count && i < ProtocolConstants.MaxEntitiesPerSnapshotPacket + 1; i++)
            SnapshotEntity.Write(ref writer, new SnapshotEntity { EntityId = (ushort)(i + 1) });
        var reader = ReaderAfterId(writer.Length, PacketId.WorldSnapshot);
        Assert.False(WorldSnapshotHeader.TryRead(ref reader, out _));
    }

    // Phase 8 D4: positions and VelocityY within +-128 come back within half a step (1/512); outside they are clamped.
    [Fact]
    public void SnapshotEntity_Quantization_StaysWithinHalfAStep_AndClamps()
    {
        var rng = new Random(4);
        for (int i = 0; i < 2000; i++)
        {
            var original = new SnapshotEntity
            {
                EntityId = 9,
                Position = new Vector3((float)(rng.NextDouble() * 250 - 125), (float)(rng.NextDouble() * 30), (float)(rng.NextDouble() * 250 - 125)),
                VelocityY = (float)(rng.NextDouble() * 60 - 30),
                Yaw = (float)(rng.NextDouble() * 360),
                Flags = SnapshotEntity.AliveFlag,
            };
            SnapshotEntity back = RoundTrip(original);
            Assert.Equal(9, back.EntityId);
            Assert.True(back.IsAlive);
            Assert.InRange(back.Position.X - original.Position.X, -1f / 512f, 1f / 512f);
            Assert.InRange(back.Position.Y - original.Position.Y, -1f / 512f, 1f / 512f);
            Assert.InRange(back.Position.Z - original.Position.Z, -1f / 512f, 1f / 512f);
            Assert.InRange(back.VelocityY - original.VelocityY, -1f / 512f, 1f / 512f);
            float yawError = MathF.Abs(back.Yaw - original.Yaw);
            yawError = MathF.Min(yawError, 360f - yawError);
            Assert.True(yawError <= 360f / 65536f, $"yaw {original.Yaw} -> {back.Yaw}");
        }

        // Box tops and terrain vertices are multiples of 1/256 and arrive exactly.
        Assert.Equal(new Vector3(-46.75f, 6f, 1.03125f), RoundTrip(new SnapshotEntity { Position = new Vector3(-46.75f, 6f, 1.03125f) }).Position);
        Assert.Equal(SnapshotEntity.Quantize(12.3456f), RoundTrip(new SnapshotEntity { Position = new Vector3(12.3456f, 0f, 0f) }).Position.X);

        SnapshotEntity far = RoundTrip(new SnapshotEntity { Position = new Vector3(500f, -500f, float.NaN), VelocityY = float.PositiveInfinity, Yaw = float.NaN });
        Assert.Equal(short.MaxValue / 256f, far.Position.X);
        Assert.Equal(short.MinValue / 256f, far.Position.Y);
        Assert.Equal(0f, far.Position.Z);
        Assert.Equal(0f, far.VelocityY);
        Assert.Equal(0f, far.Yaw);

        Assert.InRange(RoundTrip(new SnapshotEntity { Yaw = -90f }).Yaw, 270f - 0.01f, 270f + 0.01f);
        float almostFull = RoundTrip(new SnapshotEntity { Yaw = 359.999f }).Yaw;
        Assert.True(almostFull < 0.01f || almostFull > 359.99f, $"359.999 -> {almostFull}");
    }

    private SnapshotEntity RoundTrip(SnapshotEntity e)
    {
        var writer = new PacketWriter(_buffer);
        SnapshotEntity.Write(ref writer, e);
        var reader = new PacketReader(_buffer.AsSpan(0, writer.Length));
        Assert.True(SnapshotEntity.TryRead(ref reader, out SnapshotEntity back));
        return back;
    }"""),
("""        Assert.True(SnapshotEntity.TryRead(ref reader, out var e1));
        Assert.True(SnapshotEntity.TryRead(ref reader, out var e2));
        Assert.Equal(new Vector3(1, 2, 3), e1.Position);
        Assert.Equal(-1f, e1.VelocityY);""",
"""        Assert.True(SnapshotEntity.TryRead(ref reader, out var e1));
        Assert.True(SnapshotEntity.TryRead(ref reader, out var e2));
        Assert.Equal(new Vector3(1, 2, 3), e1.Position);   // whole numbers are exact in 1/256 fixed point
        Assert.Equal(-1f, e1.VelocityY);"""),
])

edit(T + 'Shared/ProtocolConstantsTests.cs', [
("""    public void SnapshotEntityLimit_FitsInOneDatagram()
    {
        // D10: header 11 + self block 6, then 23 bytes per entity.
        Assert.Equal(17, WorldSnapshotHeader.Size);
        Assert.Equal(6, SnapshotSelf.Size);
        Assert.Equal(23, SnapshotEntity.Size);
        Assert.True(WorldSnapshotHeader.Size + ProtocolConstants.MaxSnapshotEntities * SnapshotEntity.Size <= ProtocolConstants.MaxPacketSize);
    }

    [Fact]
    public void ProtocolVersion_IsSix()
    {
        // Phase 6 changed the map (terrain and boxes), so movement results differ; v5 clients must be rejected at connect.
        Assert.Equal((ushort)6, ProtocolConstants.ProtocolVersion);
    }""",
"""    public void SnapshotPacket_FitsInOneDatagram_AndTheMatchFitsInTheParts()
    {
        // Phase 8: header 13 + self block 6, then 13 bytes per entity; 100 players in 2 packets of at most 90.
        Assert.Equal(19, WorldSnapshotHeader.Size);
        Assert.Equal(6, SnapshotSelf.Size);
        Assert.Equal(13, SnapshotEntity.Size);
        Assert.Equal(100, ProtocolConstants.MaxSnapshotEntities);
        Assert.True(WorldSnapshotHeader.Size + ProtocolConstants.MaxEntitiesPerSnapshotPacket * SnapshotEntity.Size <= ProtocolConstants.MaxPacketSize);
        Assert.True(WorldSnapshotHeader.Size + (ProtocolConstants.MaxEntitiesPerSnapshotPacket + 1) * SnapshotEntity.Size > ProtocolConstants.MaxPacketSize);
        Assert.Equal(2, ProtocolConstants.MaxSnapshotParts);
        Assert.True(ProtocolConstants.MaxSnapshotParts * ProtocolConstants.MaxEntitiesPerSnapshotPacket >= ProtocolConstants.MaxSnapshotEntities);
    }

    [Fact]
    public void ProtocolVersion_IsSeven()
    {
        // Phase 8 changed the snapshot layout (parts, 13-byte entities); v6 clients must be rejected at connect.
        Assert.Equal((ushort)7, ProtocolConstants.ProtocolVersion);
    }"""),
])

edit(T + 'Bots/BotPartsTests.cs', [
("""        Assert.False(BotOptions.TryParse(new[] { "--count", "51" }, out _, out _));""",
"""        Assert.False(BotOptions.TryParse(new[] { "--count", "101" }, out _, out _));
        Assert.True(BotOptions.TryParse(new[] { "--count", "100" }, out _, out _));"""),
])

edit(T + 'Integration/ServerIntegrationTests.cs', [
("""        // A 50-entity snapshot is 17 + 23 * 50 = 1167 bytes, sent Sequenced (never fragmented):
        // the server's MTU must allow a single packet of ProtocolConstants.MaxPacketSize.""",
"""        // Phase 8: 100 players arrive in two Sequenced packets (90 + 10), each at most 19 + 13 * 90 = 1189 bytes, never
        // fragmented: the server's MTU must allow a single packet of ProtocolConstants.MaxPacketSize. Every client must
        // end up with all 100 (the parts of one tick add up)."""),
])
print('tests ok')
```

`Server/tests/ProjectH.Server.Tests/Game/SnapshotSplitTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using LiteNetLib;
using ProjectH.Server.Game;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Tests.Game;

// Phase 8 D3: a snapshot of more than MaxEntitiesPerSnapshotPacket players goes out as several packets of the same tick.
public class SnapshotSplitTests
{
    private sealed record Sent(int PeerId, byte[] Data, DeliveryMethod Method);

    private static (Match match, List<Sent> sent) MatchWith(int players)
    {
        var sent = new List<Sent>();
        var match = new Match(new ServerOptions { MaxPlayers = ProtocolConstants.MaxSnapshotEntities, DevRespawn = true }, TestGameData.Create(),
            (peer, data, method) => { if ((PacketId)data[0] == PacketId.WorldSnapshot) sent.Add(new Sent(peer, data.ToArray(), method)); },
            lootPoints: System.Array.Empty<LootPoint>());
        for (int peer = 1; peer <= players; peer++) Assert.Equal(JoinResult.Ok, match.TryJoin(peer, "p" + peer));
        return (match, sent);
    }

    private static (WorldSnapshotHeader header, List<SnapshotEntity> entities) Read(Sent s)
    {
        var reader = new PacketReader(s.Data);
        Assert.True(reader.TryReadPacketId(out _));
        Assert.True(WorldSnapshotHeader.TryRead(ref reader, out var header));
        var entities = new List<SnapshotEntity>();
        for (int i = 0; i < header.Count; i++)
        {
            Assert.True(SnapshotEntity.TryRead(ref reader, out var e));
            entities.Add(e);
        }
        Assert.Equal(0, reader.Remaining);
        return (header, entities);
    }

    [Fact]
    public void AHundredPlayers_GetTwoPacketsOfTheSameTick_ThatAddUpToEveryone()
    {
        (Match match, List<Sent> sent) = MatchWith(ProtocolConstants.MaxSnapshotEntities);
        match.Tick();
        match.Tick();   // SnapshotEveryTicks = 2: one snapshot within two ticks
        Assert.NotEmpty(sent);

        foreach (IGrouping<int, Sent> perPeer in sent.GroupBy(s => s.PeerId))
        {
            List<(WorldSnapshotHeader header, List<SnapshotEntity> entities)> packets = perPeer.Select(Read).ToList();
            Assert.Equal(2, packets.Count);
            Assert.All(perPeer, s => Assert.Equal(DeliveryMethod.Sequenced, s.Method));
            Assert.All(perPeer, s => Assert.True(s.Data.Length <= ProtocolConstants.MaxPacketSize));
            Assert.Equal(packets[0].header.ServerTick, packets[1].header.ServerTick);
            Assert.Equal(new[] { 0, 1 }, packets.Select(p => (int)p.header.Part));
            Assert.All(packets, p => Assert.Equal(2, p.header.PartCount));
            Assert.Equal(ProtocolConstants.MaxEntitiesPerSnapshotPacket, packets[0].header.Count);
            Assert.Equal(ProtocolConstants.MaxSnapshotEntities - ProtocolConstants.MaxEntitiesPerSnapshotPacket, packets[1].header.Count);
            var ids = packets.SelectMany(p => p.entities).Select(e => e.EntityId).ToList();
            Assert.Equal(ProtocolConstants.MaxSnapshotEntities, ids.Distinct().Count());
            Assert.True(match.TryGetPlayer(perPeer.Key, out PlayerEntity me));
            Assert.Contains(me.EntityId, ids);
            Assert.All(packets, p => Assert.Equal(me.Health, p.header.Self.Health));   // the self block is the recipient's own
        }
    }

    [Fact]
    public void UpToNinetyPlayers_StillGetOnePacket()
    {
        (Match match, List<Sent> sent) = MatchWith(ProtocolConstants.MaxEntitiesPerSnapshotPacket);
        match.Tick();
        match.Tick();
        foreach (IGrouping<int, Sent> perPeer in sent.GroupBy(s => s.PeerId))
        {
            var (header, entities) = Read(Assert.Single(perPeer));
            Assert.Equal(0, header.Part);
            Assert.Equal(1, header.PartCount);
            Assert.Equal(ProtocolConstants.MaxEntitiesPerSnapshotPacket, entities.Count);
        }
    }

    [Fact]
    public void SendingASplitSnapshot_AllocatesNothing()
    {
        var match = new Match(new ServerOptions { MaxPlayers = ProtocolConstants.MaxSnapshotEntities, DevRespawn = true }, TestGameData.Create(),
            static (_, _, _) => { }, lootPoints: System.Array.Empty<LootPoint>());
        for (int peer = 1; peer <= ProtocolConstants.MaxSnapshotEntities; peer++) match.TryJoin(peer, "p" + peer);
        for (int i = 0; i < 10; i++) match.Tick();
        long before = System.GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 60; i++) match.Tick();
        Assert.Equal(before, System.GC.GetAllocatedBytesForCurrentThread());
    }
}
```

`Server/tests/ProjectH.Server.Tests/Bots/BotBrainTests.cs`에서 클래스의 마지막 `}` 바로 앞에 넣는다(파일 범위 namespace다).

```csharp
    // Phase 8 D8: the packets of one tick add up; the first packet of a new tick starts the list over.
    [Fact]
    public void ASnapshotInTwoParts_AddsUp_AndANewTickStartsOver()
    {
        BotView view = Create();
        view.ApplySnapshot(new WorldSnapshotHeader { ServerTick = 10, Part = 0, PartCount = 2 });
        view.ApplyEntity(new SnapshotEntity { EntityId = 2, Flags = SnapshotEntity.AliveFlag });
        view.ApplyEntity(new SnapshotEntity { EntityId = 1, Position = new Vector3(3f, 0f, 4f), Flags = SnapshotEntity.AliveFlag });   // ourselves
        view.ApplySnapshot(new WorldSnapshotHeader { ServerTick = 10, Part = 1, PartCount = 2 });
        view.ApplyEntity(new SnapshotEntity { EntityId = 3, Flags = SnapshotEntity.AliveFlag });
        Assert.Equal(2, view.OtherCount);
        Assert.Equal(new Vector3(3f, 0f, 4f), view.MyPosition);

        view.ApplySnapshot(new WorldSnapshotHeader { ServerTick = 12, Part = 0, PartCount = 2 });
        view.ApplyEntity(new SnapshotEntity { EntityId = 4, Flags = SnapshotEntity.AliveFlag });
        Assert.Equal(1, view.OtherCount);
        Assert.Equal((ushort)4, view.Others[0].EntityId);

        // A lost first part: the second part of a new tick still starts that tick's list.
        view.ApplySnapshot(new WorldSnapshotHeader { ServerTick = 14, Part = 1, PartCount = 2 });
        view.ApplyEntity(new SnapshotEntity { EntityId = 5, Flags = SnapshotEntity.AliveFlag });
        Assert.Equal(1, view.OtherCount);
        Assert.Equal((ushort)5, view.Others[0].EntityId);
    }
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 빌드 실패. `WorldSnapshotHeader.Part`, `ProtocolConstants.MaxEntitiesPerSnapshotPacket`, `SnapshotEntity.Quantize`가 없다(CS0117/CS1061).

- [ ] **Step 3: Shared Protocol을 바꾼다**

아래 스크립트를 `plan8_shared.py`로 저장하고 `python <경로>/plan8_shared.py E:/popol/ProjectH`로 실행한다. 원래 줄바꿈을 지키면서 고친다.

```python
import os, sys
os.chdir(sys.argv[1])

def edit(path, pairs):
    s = open(path, encoding='utf-8', newline='').read()
    nl = '\r\n' if '\r\n' in s else '\n'
    for a, b in pairs:
        a2 = a.replace('\n', nl); b2 = b.replace('\n', nl)
        assert s.count(a2) == 1, (path, a[:70], s.count(a2))
        s = s.replace(a2, b2)
    open(path, 'w', encoding='utf-8', newline='').write(s)

edit('Shared/Runtime/Protocol/ProtocolConstants.cs', [
("""        // 6: Phase 6 map (terrain and the new boxes change movement results; packet layouts are unchanged).
        public const ushort ProtocolVersion = 6;""",
"""        // 6: Phase 6 map (terrain and the new boxes change movement results; packet layouts are unchanged).
        // 7: Phase 8 snapshots (13-byte quantized entities, a snapshot split into up to MaxSnapshotParts packets).
        public const ushort ProtocolVersion = 7;"""),
("""        // (1200 - 17 header bytes) / 23 bytes per entity = 51; 50 players = 1167 bytes (pinned by PacketTests).
        public const int MaxSnapshotEntities = 50;""",
"""        // Phase 8 D3: the most players a match (and so a snapshot) can hold. A snapshot is split into packets of at most
        // MaxEntitiesPerSnapshotPacket entities: (1200 - 19 header bytes) / 13 bytes per entity = 90, so 100 players
        // take MaxSnapshotParts = 2 packets (pinned by PacketTests).
        public const int MaxSnapshotEntities = 100;
        public const int MaxEntitiesPerSnapshotPacket = 90;
        public const int MaxSnapshotParts = (MaxSnapshotEntities + MaxEntitiesPerSnapshotPacket - 1) / MaxEntitiesPerSnapshotPacket;"""),
])

edit('Shared/Runtime/Protocol/ServerPackets.cs', [
("""    public struct WorldSnapshotHeader
    {
        public const int Size = 17;
        public const int AckInputSeqOffset = 5;
        public const int SelfOffset = 11;

        public uint ServerTick;
        public uint AckInputSeq;
        public ushort Count;
        public SnapshotSelf Self;

        public static void Write(ref PacketWriter writer, in WorldSnapshotHeader h)
        {
            writer.WriteByte((byte)PacketId.WorldSnapshot);
            writer.WriteUInt32(h.ServerTick);
            writer.WriteUInt32(h.AckInputSeq);
            writer.WriteUInt16(h.Count);
            SnapshotSelf.Write(ref writer, h.Self);
        }

        public static bool TryRead(ref PacketReader reader, out WorldSnapshotHeader h)
        {
            h = default;
            if (reader.Remaining < Size - 1) return false;
            reader.TryReadUInt32(out h.ServerTick);
            reader.TryReadUInt32(out h.AckInputSeq);
            reader.TryReadUInt16(out h.Count);
            SnapshotSelf.TryRead(ref reader, out h.Self);
            if (h.Count > ProtocolConstants.MaxSnapshotEntities) return false;
            return reader.Remaining >= h.Count * SnapshotEntity.Size;
        }""",
"""    // Phase 8 D3: one packet of a snapshot. A tick's snapshot is PartCount packets (Part 0..PartCount-1), each a complete
    // header (same tick, ack and self block) with its own slice of the entities, so every packet can be applied on its
    // own: a lost part only means those entities get no sample for that tick.
    public struct WorldSnapshotHeader
    {
        public const int Size = 19;
        public const int AckInputSeqOffset = 5;
        public const int SelfOffset = 13;

        public uint ServerTick;
        public uint AckInputSeq;
        public ushort Count;       // entities in this packet
        public byte Part;
        public byte PartCount;
        public SnapshotSelf Self;

        public static void Write(ref PacketWriter writer, in WorldSnapshotHeader h)
        {
            writer.WriteByte((byte)PacketId.WorldSnapshot);
            writer.WriteUInt32(h.ServerTick);
            writer.WriteUInt32(h.AckInputSeq);
            writer.WriteUInt16(h.Count);
            writer.WriteByte(h.Part);
            writer.WriteByte(h.PartCount);
            SnapshotSelf.Write(ref writer, h.Self);
        }

        public static bool TryRead(ref PacketReader reader, out WorldSnapshotHeader h)
        {
            h = default;
            if (reader.Remaining < Size - 1) return false;
            reader.TryReadUInt32(out h.ServerTick);
            reader.TryReadUInt32(out h.AckInputSeq);
            reader.TryReadUInt16(out h.Count);
            reader.TryReadByte(out h.Part);
            reader.TryReadByte(out h.PartCount);
            SnapshotSelf.TryRead(ref reader, out h.Self);
            if (h.Count > ProtocolConstants.MaxEntitiesPerSnapshotPacket) return false;
            if (h.PartCount < 1 || h.PartCount > ProtocolConstants.MaxSnapshotParts || h.Part >= h.PartCount) return false;
            return reader.Remaining >= h.Count * SnapshotEntity.Size;
        }"""),
("""    public struct SnapshotEntity
    {
        public const int Size = 23; // id 2 + position 12 + velocityY 4 + yaw 4 + flags 1
        public const byte AliveFlag = 1;

        public ushort EntityId;
        public Vector3 Position;
        public float VelocityY;
        public float Yaw;
        public byte Flags;

        public bool IsAlive => (Flags & AliveFlag) != 0;

        public static void Write(ref PacketWriter writer, in SnapshotEntity e)
        {
            writer.WriteUInt16(e.EntityId);
            writer.WriteVector3(e.Position);
            writer.WriteSingle(e.VelocityY);
            writer.WriteSingle(e.Yaw);
            writer.WriteByte(e.Flags);
        }

        public static bool TryRead(ref PacketReader reader, out SnapshotEntity e)
        {
            e = default;
            if (reader.Remaining < Size) return false;
            reader.TryReadUInt16(out e.EntityId);
            reader.TryReadVector3(out e.Position);
            reader.TryReadSingle(out e.VelocityY);
            reader.TryReadSingle(out e.Yaw);
            reader.TryReadByte(out e.Flags);
            return true;
        }
    }""",
"""    // Phase 8 D4: quantized on the wire. Position and VelocityY are signed 16-bit fixed point with 1/256 resolution
    // (range +-128 m and +-128 m/s; the map is +-80 m), Yaw is 16 bits over 360 degrees. The error is at most half a
    // step (about 0.002 m per axis), well inside the client's reconcile tolerance (0.01 m). Values outside the range
    // are clamped; non-finite values are written as 0.
    public struct SnapshotEntity
    {
        public const int Size = 13; // id 2 + position 3 x 2 + velocityY 2 + yaw 2 + flags 1
        public const byte AliveFlag = 1;
        public const float FixedScale = 256f;
        public const float YawScale = 65536f / 360f;

        public ushort EntityId;
        public Vector3 Position;
        public float VelocityY;
        public float Yaw;
        public byte Flags;

        public bool IsAlive => (Flags & AliveFlag) != 0;

        public static void Write(ref PacketWriter writer, in SnapshotEntity e)
        {
            writer.WriteUInt16(e.EntityId);
            writer.WriteUInt16(ToFixed(e.Position.X));
            writer.WriteUInt16(ToFixed(e.Position.Y));
            writer.WriteUInt16(ToFixed(e.Position.Z));
            writer.WriteUInt16(ToFixed(e.VelocityY));
            writer.WriteUInt16(ToYaw(e.Yaw));
            writer.WriteByte(e.Flags);
        }

        public static bool TryRead(ref PacketReader reader, out SnapshotEntity e)
        {
            e = default;
            if (reader.Remaining < Size) return false;
            reader.TryReadUInt16(out e.EntityId);
            reader.TryReadUInt16(out ushort x);
            reader.TryReadUInt16(out ushort y);
            reader.TryReadUInt16(out ushort z);
            reader.TryReadUInt16(out ushort velocityY);
            reader.TryReadUInt16(out ushort yaw);
            reader.TryReadByte(out e.Flags);
            e.Position = new Vector3(FromFixed(x), FromFixed(y), FromFixed(z));
            e.VelocityY = FromFixed(velocityY);
            e.Yaw = yaw / YawScale;
            return true;
        }

        // What the receiver will read back for this value (tests and the server's own comparisons).
        public static float Quantize(float value) => FromFixed(ToFixed(value));

        private static ushort ToFixed(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 0;
            float scaled = (float)Math.Round(value * FixedScale);
            if (scaled > short.MaxValue) scaled = short.MaxValue;
            if (scaled < short.MinValue) scaled = short.MinValue;
            return unchecked((ushort)(short)scaled);
        }

        private static float FromFixed(ushort raw) => unchecked((short)raw) / FixedScale;

        private static ushort ToYaw(float yaw)
        {
            if (float.IsNaN(yaw) || float.IsInfinity(yaw)) return 0;
            float degrees = yaw % 360f;
            if (degrees < 0f) degrees += 360f;
            return unchecked((ushort)(int)Math.Round(degrees * YawScale));
        }
    }"""),
])
print('shared ok')
```

- [ ] **Step 4: 서버·봇·테스트 Client를 바꾼다**

아래 스크립트를 `plan8_server.py`로 저장하고 `python <경로>/plan8_server.py E:/popol/ProjectH`로 실행한다.

```python
import os, sys
os.chdir(sys.argv[1])

def edit(path, pairs):
    s = open(path, encoding='utf-8', newline='').read()
    nl = '\r\n' if '\r\n' in s else '\n'
    for a, b in pairs:
        a2 = a.replace('\n', nl); b2 = b.replace('\n', nl)
        assert s.count(a2) == 1, (path, a[:70], s.count(a2))
        s = s.replace(a2, b2)
    open(path, 'w', encoding='utf-8', newline='').write(s)

edit('Server/src/ProjectH.Server/Game/Match.cs', [
("""    private void SendSnapshots()
    {
        if (_players.Count == 0) return;

        // One payload for everyone; AckInputSeq and the self block differ per recipient and are patched in place.
        var writer = new PacketWriter(_sendBuffer);
        WorldSnapshotHeader.Write(ref writer, new WorldSnapshotHeader { ServerTick = ServerTick, AckInputSeq = 0, Count = (ushort)_players.Count });
        foreach (var p in _players)
        {
            SnapshotEntity.Write(ref writer, new SnapshotEntity
            {
                EntityId = p.EntityId,
                Position = p.State.Position,
                VelocityY = p.State.VelocityY,
                Yaw = p.State.Yaw,
                Flags = p.Alive ? SnapshotEntity.AliveFlag : (byte)0,
            });
        }
        // Cannot overflow: ServerOptions.Validate caps MaxPlayers at MaxSnapshotEntities (17 + 23 * 50 = 1167 bytes).
        if (writer.Overflowed) return;

        Span<byte> packet = _sendBuffer.AsSpan(0, writer.Length);
        foreach (var p in _players)
        {
            WorldSnapshotHeader.PatchRecipient(packet, p.LastProcessedSeq, SelfBlock(p));
            _send(p.PeerId, packet, DeliveryMethod.Sequenced);
        }
    }""",
"""    // Phase 8 D3: the snapshot is split into packets of at most MaxEntitiesPerSnapshotPacket players. Each packet is one
    // payload for everyone; AckInputSeq and the self block differ per recipient and are patched in place.
    private void SendSnapshots()
    {
        int total = _players.Count;
        if (total == 0) return;
        int perPart = ProtocolConstants.MaxEntitiesPerSnapshotPacket;
        int parts = (total + perPart - 1) / perPart;

        for (int part = 0; part < parts; part++)
        {
            int first = part * perPart;
            int count = Math.Min(perPart, total - first);
            var writer = new PacketWriter(_sendBuffer);
            WorldSnapshotHeader.Write(ref writer, new WorldSnapshotHeader
            {
                ServerTick = ServerTick, AckInputSeq = 0, Count = (ushort)count, Part = (byte)part, PartCount = (byte)parts,
            });
            for (int i = first; i < first + count; i++)
            {
                PlayerEntity p = _players[i];
                SnapshotEntity.Write(ref writer, new SnapshotEntity
                {
                    EntityId = p.EntityId,
                    Position = p.State.Position,
                    VelocityY = p.State.VelocityY,
                    Yaw = p.State.Yaw,
                    Flags = p.Alive ? SnapshotEntity.AliveFlag : (byte)0,
                });
            }
            // Cannot overflow: 19 + 13 * 90 = 1189 bytes, and ServerOptions.Validate caps MaxPlayers at MaxSnapshotEntities.
            if (writer.Overflowed) return;

            Span<byte> packet = _sendBuffer.AsSpan(0, writer.Length);
            foreach (var p in _players)
            {
                WorldSnapshotHeader.PatchRecipient(packet, p.LastProcessedSeq, SelfBlock(p));
                _send(p.PeerId, packet, DeliveryMethod.Sequenced);
            }
        }
    }"""),
])

edit('Server/src/ProjectH.Server/ServerOptions.cs', [
("""            return $"MaxPlayers must be 1-{ProtocolConstants.MaxSnapshotEntities}: a full snapshot must fit one unfragmented datagram.";""",
"""            return $"MaxPlayers must be 1-{ProtocolConstants.MaxSnapshotEntities}: a snapshot is at most {ProtocolConstants.MaxSnapshotParts} unfragmented datagrams.";"""),
])

edit('Server/src/ProjectH.Bots/BotView.cs', [
("""    public void ApplySnapshot(in WorldSnapshotHeader header)
    {
        HasSnapshot = true;
        ServerTick = header.ServerTick;
        AckInputSeq = header.AckInputSeq;
        Self = header.Self;
        OtherCount = 0;
    }""",
"""    // Phase 8: a snapshot can come in several packets of the same tick. The first packet of a new tick starts the list
    // of others over; later packets of that tick add to it.
    public void ApplySnapshot(in WorldSnapshotHeader header)
    {
        if (!HasSnapshot || header.ServerTick != ServerTick) OtherCount = 0;
        HasSnapshot = true;
        ServerTick = header.ServerTick;
        AckInputSeq = header.AckInputSeq;
        Self = header.Self;
    }"""),
])

edit('Server/tests/ProjectH.Server.Tests/Integration/HeadlessClient.cs', [
("""                if (!WorldSnapshotHeader.TryRead(ref r, out var header)) return;
                LastSnapshot.Clear();""",
"""                if (!WorldSnapshotHeader.TryRead(ref r, out var header)) return;
                // Phase 8: the packets of one tick add up; a new tick starts over.
                if (header.ServerTick != LastServerTick) LastSnapshot.Clear();"""),
])
print('server ok')
```

- [ ] **Step 5: 테스트가 통과하는지 확인한다**

Run: `dotnet build Server/ProjectH.Server.slnx --no-incremental` → 경고 0, 오류 0

Run: `dotnet test Server/ProjectH.Server.slnx` → 모두 통과(643). `FullMatch_SnapshotWithMaxEntities_IsDelivered`가 100명으로 통과한다. 전체 테스트를 두 번 돌려 두 번 다 통과하는지 확인한다.

Run: `git diff --stat`. 위 파일들만, 고친 줄만 바뀌어야 한다(줄바꿈 전체 변경이 없다).

Run: 스크래치 `UnityCompile.csproj` 빌드(`dotnet build C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/unitycompile/UnityCompile.csproj`) → `error` 0. Shared가 Unity 조건(C# 9)에서 컴파일된다.

- [ ] **Step 6: Commit** — `feat(protocol): split and quantized snapshots for 100 players (protocol v7)`

---

### Task 2: Client 확인 — 양자화된 Snapshot으로 예측 보정이 없다

**Files:**
- Modify: `Client/Assets/Tests/EditMode/MapPredictionTests.cs`

**Interfaces:**
- Consumes: Task 1의 `SnapshotEntity` 양자화

- [ ] **Step 1: 테스트를 더한다**

`MapPredictionTests.cs`의 "Review Focus: standing on a box top must not jitter" 주석 바로 앞에 넣는다.

```csharp
        // Phase 8 D4: snapshots arrive quantized (1/256 m). Reconciling every tick against the quantized server state over a
        // hill must never correct the prediction: the error stays inside the 0.01 m match tolerance, so the predicted
        // position stays bit-identical to the server's exact one.
        [Test]
        public void QuantizedSnapshots_OverAHill_NeverCorrectThePrediction()
        {
            var spawn = new MoveState { Position = new Num.Vector3(0f, 0f, 26f) };
            var predictor = new LocalPlayerPredictor(SimHz, spawn);
            var server = spawn;
            var buffer = new byte[SnapshotEntity.Size];
            for (uint seq = 1; seq <= 250; seq++)
            {
                AdvanceOneStep(predictor, Vector2.up);
                MovementSimulation.Step(ref server, new InputCommand { MoveY = 1f }, Step, GameMap.Boxes, GameMap.Terrain);

                var writer = new PacketWriter(buffer);
                SnapshotEntity.Write(ref writer, ToEntity(server));
                var reader = new PacketReader(buffer);
                Assert.IsTrue(SnapshotEntity.TryRead(ref reader, out SnapshotEntity wire));
                predictor.Reconcile(wire, seq);

                Assert.AreEqual(server.Position.X, predictor.PredictedPosition.x, $"seq {seq}");
                Assert.AreEqual(server.Position.Y, predictor.PredictedPosition.y, $"seq {seq}");
                Assert.AreEqual(server.Position.Z, predictor.PredictedPosition.z, $"seq {seq}");
            }
        }
```

- [ ] **Step 2: 테스트를 돌린다**

Run: `dotnet test C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/predictortests/PredictorTests.csproj`
Expected: 모두 통과(기존 127 + 1). 계획 단계에서 새 Protocol로 128개가 통과했다.

Run: `dotnet build C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/unitycompile/UnityCompile.csproj` → `error` 0

Unity Editor 확인은 사용자 대기로 기록한다(`Editor.log`의 `error CS`, EditMode Test Runner).

- [ ] **Step 3: Commit** — `test(client): quantized snapshots never correct the prediction`

---

### Task 3: 다시 측정하고 문서를 남긴다

**Files:**
- Modify: `Docs/LoadTest.md`, `Docs/Networking.md`, `Docs/Server.md`, `Docs/Architecture.md`, `Docs/Bots.md`
- Create: `Docs/ClientPerf.md`

- [ ] **Step 1: 부하를 측정한다 (D9)**

Phase 7과 같은 절차(`Docs/LoadTest.md`)로 봇 50, 64, 100명을 측정한다. 공통 조건은 다음과 같다.
- DevRespawn, 각 2분, Release, 포트 7790
- 서버 옵션 `--Server:MaxPlayers=100`
- 처음 약 20초는 버린다.
- 봇 CPU는 `TotalProcessorTime` 표본으로 잰다.

자기가 띄운 프로세스는 pid로만 끄고, 끝나면 포트가 비었는지 확인한다.

- [ ] **Step 2: `Docs/LoadTest.md`를 고친다**

- 새 절 "Phase 8 (Protocol v7: 분할·양자화 Snapshot)"을 둔다.
  - 같은 형식의 표를 만든다. 행은 50, 64, 100이다.
  - 50명 행은 Phase 7 50명 행과 나란히 비교한다. `bytesOut/s`가 얼마나 줄었는지(%) 적는다.
- 64·100명을 측정하지 못했다는 Phase 7의 문장은 "Phase 8에서 측정"으로 바꾼다. 기존 Phase 7 표는 그대로 둔다(기록이다).
- 관찰을 적는다.
  - 100명에서 서버 송신 대역폭과 Client당 수신량
  - Interest Management를 넣지 않은 근거(spec D5)와, 넣어야 할 조건(인터넷 서버 대역폭 비용)
- 숫자는 실제로 잰 값만 적는다.

- [ ] **Step 3: `Docs/ClientPerf.md`를 쓴다 (D6)**

- **목적:** Client Frame Time, GC, Draw Call, UI, Physics, Memory를 측정한다(요청서 Phase 8).
- **절차:**
  1. 서버를 띄운다(`--Server:MaxPlayers=100 --Server:DevRespawn=true`).
  2. 봇 99명을 붙인다(`ProjectH.Bots --count 99`).
  3. Unity Editor에서 Play를 누른다.
  4. Unity Profiler(CPU, Rendering, Memory, UI, Physics)로 30초를 기록한다.
  5. Development Build에서 한 번 더 기록한다.
- **기록할 값:** Frame Time p50·p95·최대, GC Alloc/frame, Draw Calls·SetPass, UI Canvas Rebuild, Physics 시간, Total Used Memory
- **결과 표:** 값 칸은 "측정 대기(사용자)"로 둔다.
- **정적 점검 결과:** 아래 "정적 점검" 절. 컨트롤러가 리뷰어의 점검 결과를 이 Task의 보고에 넘겨준다. 측정 없이 고치지 않는다는 원칙(§63)을 적는다.

- [ ] **Step 4: 기존 문서를 고친다**

- **`Docs/Networking.md`**
  - Snapshot 형식을 v7로 바꾼다: 헤더 19 B(`Part`, `PartCount`), 엔티티 13 B(양자화 규칙), 패킷당 최대 90명, 최대 2패킷
  - 수신 쪽 규칙: 패킷마다 독립 적용, 제거는 이벤트
  - `ProtocolVersion` 7
  - 50명 1167 B 같은 옛 수치는 새 수치로 바꾼다.
- **`Docs/Server.md`:** `MaxPlayers` 상한 100, Snapshot 분할 송신
- **`Docs/Architecture.md`:** Phase 8 spec을 설계 근거에 더한다.
- **`Docs/Bots.md`:** `--count` 상한 100, 여러 패킷 Snapshot 병합

- [ ] **Step 5: Commit** — `docs: Phase 8 load test, client profiling procedure, protocol v7`

---

## Phase 완료 확인

1. `dotnet build Server/ProjectH.Server.slnx --no-incremental` (경고 0)와 `dotnet test Server/ProjectH.Server.slnx` (모두 통과)를 실행한다. 스크래치 Client NUnit도 모두 통과해야 한다.
2. `git diff --stat main -- Client/Assets/Scripts`가 비어 있어야 한다. Unity Client 코드는 바뀌지 않는다.
3. `github-push` 스킬로 `main`에 Squash Commit·Push한다.
