using System;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Game
{
    // Phase 12 D9: the doors as the local prediction sees them: the server's DoorStates with the local player's own
    // predicted changes (a shoulder bash, E) laid over it, each until the next DoorStates or PredictionSeconds, whichever
    // comes first (a change the server never made then stops mispredicting). The collision world is GameMap.Boxes followed
    // by the closed doors, in the same order as the server's DoorSet, in a fixed array rewritten only on a change.
    // Pure, no allocation after construction, no UnityEngine (EditMode tests run it outside Unity). Main thread only.
    public sealed class PredictedDoors
    {
        public const float PredictionSeconds = 1f;

        private readonly Box[] _world = new Box[GameMap.Boxes.Length + GameMap.DoorCount];
        private readonly int[] _doorOfSlot = new int[GameMap.DoorCount];
        // Per door: when its predicted state stops counting (0 = no prediction), and that state.
        private readonly float[] _predictedUntil = new float[GameMap.DoorCount];
        private byte _predictedOpen;
        private byte _server;
        private int _length;

        // 기능: 맵 박스를 충돌 월드에 복사하고 모든 문이 닫힌 월드를 만든다.
        // 입력: 없음.
        // 출력: 서버 상태 0(모든 문 닫힘)·예측 없음으로 초기화된 PredictedDoors.
        public PredictedDoors()
        {
            GameMap.Boxes.CopyTo(_world);
            Rebuild();
        }

        // Bit i = GameMap.Doors[i] is open, as predicted.
        public byte OpenMask { get; private set; }
        // Changes whenever OpenMask does (door views follow it).
        public int Version { get; private set; }
        // 기능: 맵 박스 뒤에 닫힌 문을 서버 DoorSet 순서로 이은 충돌 월드를 돌려준다.
        // 입력: 없음.
        // 출력: 내부 배열의 유효 길이만큼의 ReadOnlySpan(할당 없음).
        // The map boxes and the closed doors in the server's DoorSet order (DoorTests compares the two). Phase 13 D3: the
        // prediction gathers its world around the character from OpenMask instead (LocalPlayerPredictor).
        public ReadOnlySpan<Box> World => new ReadOnlySpan<Box>(_world, 0, _length);

        // 기능: 예측 기준으로 문이 열려 있는지 확인한다.
        // 입력: door - GameMap.Doors 인덱스.
        // 출력: OpenMask에서 열림이면 true, 닫힘이면 false.
        public bool IsOpen(int door) => (OpenMask & (1 << door)) != 0;

        // 기능: World 인덱스가 어떤 문의 상자인지 찾는다.
        // 입력: worldIndex - World 안의 인덱스.
        // 출력: 문 인덱스, 맵 박스이거나 범위 밖이면 -1.
        // The door whose box is at this index of World, or -1 for a map box.
        public int DoorAt(int worldIndex)
        {
            int slot = worldIndex - GameMap.Boxes.Length;
            return slot >= 0 && worldIndex < _length ? _doorOfSlot[slot] : -1;
        }

        // 기능: 이동 Step을 막은 문을 서버 DoorSet.DoorBlocking과 같은 규칙으로 찾는다.
        // 입력: step - 이동 계산 결과.
        // 출력: 막은 충돌체가 문이면 그 문 인덱스, 아니면 Z Sweep을 막은 문, 둘 다 아니면 -1.
        // The door a step was stopped by, the same rule as the server's DoorSet.DoorBlocking: the collider that stopped it,
        // or when that is no door, the Z sweep's. -1 = no door. Phase 13 D3: colliders are named by kind and id.
        public int DoorBlocking(in StepResult step)
        {
            if (step.BlockedBy.Kind == ColliderKind.Door) return (int)step.BlockedBy.Id;
            return step.BlockedByZ.Kind == ColliderKind.Door ? (int)step.BlockedByZ.Id : -1;
        }

        // 기능: 서버 DoorStates의 열림 상태를 적용하고 모든 로컬 예측을 버린다.
        // 입력: openMask - 서버가 보낸 문 열림 Bit Mask.
        // 출력: 반환값 없음. OpenMask가 서버 값이 되고, 바뀌었으면 Version 증가와 World 재구성이 일어난다.
        // DoorStates: the server's word replaces every prediction.
        public void ApplyServer(byte openMask)
        {
            _server = openMask;
            Array.Clear(_predictedUntil, 0, _predictedUntil.Length);
            _predictedOpen = 0;
            Update();
        }

        // 기능: 로컬 플레이어의 문 조작 결과를 PredictionSeconds 동안 서버 상태 위에 덧씌운다.
        // 입력: door - 문 인덱스(범위 밖이면 무시), open - 예측한 열림 여부, now - 예측기의 시뮬레이션 시간(초, 입력 Step마다 증가).
        // 출력: 반환값 없음. 예측 만료 시각이 기록되고 OpenMask·Version·World가 갱신될 수 있다.
        public void Predict(int door, bool open, float now)
        {
            if (door < 0 || door >= GameMap.DoorCount) return;
            _predictedUntil[door] = now + PredictionSeconds;
            int bit = 1 << door;
            _predictedOpen = (byte)(open ? _predictedOpen | bit : _predictedOpen & ~bit);
            Update();
        }

        // 기능: 만료 시각이 지난 문 예측을 지워 서버 상태로 되돌린다.
        // 입력: now - Predict와 같은 기준의 현재 시뮬레이션 시간(초).
        // 출력: 반환값 없음. 만료된 예측이 있으면 OpenMask·Version·World가 갱신될 수 있다.
        // Predictions older than PredictionSeconds give way to the server's state.
        public void Expire(float now)
        {
            bool changed = false;
            for (int i = 0; i < _predictedUntil.Length; i++)
            {
                if (_predictedUntil[i] > 0f && now >= _predictedUntil[i])
                {
                    _predictedUntil[i] = 0f;
                    changed = true;
                }
            }
            if (changed) Update();
        }

        // 기능: 연결 종료 시 서버 상태를 모든 문 닫힘으로 되돌린다.
        // 입력: 없음.
        // 출력: 반환값 없음. 예측이 모두 지워지고 OpenMask가 0이 된다.
        // Disconnect: no server state any more (every door closed, as at a round start).
        public void Reset() => ApplyServer(0);

        // 기능: 서버 상태에 유효한 예측을 덧씌워 OpenMask를 다시 계산한다.
        // 입력: 없음.
        // 출력: 반환값 없음. Mask가 바뀐 경우에만 OpenMask 갱신, Version 증가, World 재구성이 일어난다.
        private void Update()
        {
            int mask = _server;
            for (int i = 0; i < _predictedUntil.Length; i++)
            {
                if (_predictedUntil[i] <= 0f) continue;
                int bit = 1 << i;
                mask = (_predictedOpen & bit) != 0 ? mask | bit : mask & ~bit;
            }
            if (mask == OpenMask) return;
            OpenMask = (byte)mask;
            Version++;
            Rebuild();
        }

        // 기능: 맵 박스 뒤에 현재 닫힌 문 상자를 문 순서대로 채워 충돌 월드를 다시 만든다.
        // 입력: 없음.
        // 출력: 반환값 없음. _world·_doorOfSlot·_length가 바뀐다(할당 없음).
        private void Rebuild()
        {
            int length = GameMap.Boxes.Length;
            ReadOnlySpan<Box> doors = GameMap.Doors;
            for (int i = 0; i < doors.Length; i++)
            {
                if (IsOpen(i)) continue;
                _doorOfSlot[length - GameMap.Boxes.Length] = i;
                _world[length++] = doors[i];
            }
            _length = length;
        }
    }
}
