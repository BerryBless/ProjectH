using System;
using System.Numerics;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Game
{
    // What E may open (for the prompt only; the server decides).
    public enum LootTargetKind : byte
    {
        None = 0,
        Chest = 1,
        AmmoBox = 2,
        SupplyDrop = 3,
    }

    // Phase 16 D3, D7: the loot containers and supply drops as the server last said. ContainerStates replaces the two masks,
    // SupplyDrops replaces the whole list (both idempotent; no prediction). Fixed arrays of the packet's limits, so nothing
    // grows. Emptied by the server's own packets at a match start and a round reset (0 masks, empty list) and by Clear on a
    // disconnect. Active follows the server's rule for when a container can be opened (in a match: Playing or FinalPhase;
    // without a match: the dev sandbox), so the prompt and the door prediction use the same targets as the server. Pure (no
    // UnityEngine): the EditMode tests run it.
    public sealed class LootState
    {
        private readonly SupplyDropInfo[] _drops = new SupplyDropInfo[SupplyDropsPacket.MaxSupplyDrops];
        // Indexed by the drop's Id (the server's slot k): ContainerRule.FindTarget returns SupplyDropTargetBase + k.
        private readonly Vector3[] _dropPositions = new Vector3[SupplyDropsPacket.MaxSupplyDrops];

        public ulong SpawnedMask { get; private set; }
        public ulong OpenedMask { get; private set; }
        // Spawned and not opened: what E can open.
        public ulong ClosedMask => SpawnedMask & ~OpenedMask;
        public int DropCount { get; private set; }
        // Bit k: the drop with Id k has landed and is not opened.
        public int LandedClosedDropMask { get; private set; }
        public bool Active { get; private set; } = true;
        // Count changes (views redraw only when they moved).
        public int ContainerVersion { get; private set; }
        public int DropVersion { get; private set; }

        // 기능: 받은 목록의 index번째 Supply Drop을 돌려준다.
        // 입력: index - 0..DropCount-1.
        // 출력: 그 Supply Drop.
        public SupplyDropInfo Drop(int index) => _drops[index];

        // 기능: Supply Drop 착지 위치 배열(칸 = Id)을 돌려준다(ContainerRule.FindTarget 입력).
        // 입력: 없음.
        // 출력: 고정 4칸 배열의 읽기 전용 Span. 착지·닫힘이 아닌 칸의 값은 LandedClosedDropMask가 가린다.
        public ReadOnlySpan<Vector3> DropPositions => _dropPositions;

        // 기능: ContainerStates의 두 마스크를 적용한다(검증은 ContainerStatesPacket.TryRead가 했다).
        // 입력: spawned - 생성된 Container 비트, opened - 열린 Container 비트.
        // 출력: 바뀌었으면 true(ContainerVersion이 오른다), 같으면 false.
        public bool ApplyContainers(ulong spawned, ulong opened)
        {
            if (spawned == SpawnedMask && opened == OpenedMask) return false;
            SpawnedMask = spawned;
            OpenedMask = opened;
            ContainerVersion++;
            return true;
        }

        // 기능: 받은 SupplyDrops 목록으로 Supply Drop을 통째로 바꾸고 착지 위치·착지 마스크를 다시 만든다. 할당 없음.
        // 입력: drops·count - NetClient의 재사용 배열(호출 동안만 유효, 복사한다). count는 배열 길이와 상한으로 자른다.
        // 출력: 반환값 없음. DropVersion이 오른다. 상한 밖 Id는 위치·마스크에 넣지 않는다.
        public void ApplyDrops(SupplyDropInfo[] drops, int count)
        {
            int n = Math.Max(0, Math.Min(Math.Min(count, _drops.Length), drops != null ? drops.Length : 0));
            int landed = 0;
            for (int i = 0; i < n; i++)
            {
                SupplyDropInfo d = drops[i];
                _drops[i] = d;
                if (d.Id >= _dropPositions.Length) continue;
                _dropPositions[d.Id] = new Vector3(d.X, d.LandY, d.Z);
                if (d.State == SupplyDropState.Landed) landed |= 1 << d.Id;
            }
            for (int i = n; i < DropCount; i++) _drops[i] = default;
            DropCount = n;
            LandedClosedDropMask = landed;
            DropVersion++;
        }

        // 기능: 열기 가능 조건을 경기 상태로 정한다(서버: 경기 중 또는 개발 모드).
        // 입력: hasMatch - MatchState를 받은 적이 있는지(없으면 개발 모드), state - 최신 경기 상태.
        // 출력: 반환값 없음. Active가 바뀐다.
        public void SetMatch(bool hasMatch, MatchFlowState state)
        {
            Active = !hasMatch || state == MatchFlowState.Playing || state == MatchFlowState.FinalPhase;
        }

        // 기능: 지금 E가 열 대상을 서버 규칙 복사본(ContainerRule)으로 고른다. Active가 아니면 없음.
        // 입력: feet - 예측된 발, yaw - 예측된 방향(도), distanceSq - 결과(고른 대상의 수평 거리 제곱, 없으면 0).
        // 출력: ContainerRule.FindTarget 결과(Container id, SupplyDropTargetBase + 칸, 없으면 -1). 할당 없음.
        public int FindTarget(Vector3 feet, float yaw, out float distanceSq)
        {
            if (!Active)
            {
                distanceSq = 0f;
                return -1;
            }
            return ContainerRule.FindTarget(feet, yaw, LootContainers.All, ClosedMask, _dropPositions, LandedClosedDropMask, out distanceSq);
        }

        // 기능: FindTarget 결과가 무엇인지 알려 준다(안내 문구·QA).
        // 입력: target - FindTarget 결과.
        // 출력: Chest, AmmoBox, SupplyDrop 또는 None(-1이거나 맵에 없는 id).
        public static LootTargetKind KindOf(int target)
        {
            if (target >= ContainerRule.SupplyDropTargetBase) return LootTargetKind.SupplyDrop;
            if (target < 0 || target >= LootContainers.Count) return LootTargetKind.None;
            return LootContainers.All[target].Kind == LootContainerKind.Chest ? LootTargetKind.Chest : LootTargetKind.AmmoBox;
        }

        // 기능: Supply Drop이 그 서버 Tick에 그려질 높이를 구한다. 낙하 중일 때만 SupplyDropFall.HeightAt을 쓰고, 착지·열림은 착지 높이다
        //   (착지 패킷이 보간 지연보다 먼저 와도 공중에 뜨지 않는다).
        // 입력: drop - Supply Drop, tick - 그리는 서버 Tick(소수 가능).
        // 출력: 상자 바닥의 Y.
        public static float HeightOf(in SupplyDropInfo drop, double tick) =>
            drop.State == SupplyDropState.Falling ? SupplyDropFall.HeightAt(drop.LandY, drop.StartTick, drop.LandTick, tick) : drop.LandY;

        // 기능: 모두 비운다(끊김). Active는 개발 모드 기본값(true)으로 돌아간다.
        // 입력: 없음.
        // 출력: 반환값 없음. 비울 것이 있었으면 해당 Version이 오른다.
        public void Clear()
        {
            Active = true;
            ApplyContainers(0, 0);
            if (DropCount == 0 && LandedClosedDropMask == 0) return;
            Array.Clear(_drops, 0, _drops.Length);
            Array.Clear(_dropPositions, 0, _dropPositions.Length);
            DropCount = 0;
            LandedClosedDropMask = 0;
            DropVersion++;
        }
    }
}
