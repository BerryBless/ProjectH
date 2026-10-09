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

        // 기능: 맵 상자를 복사하고 모든 문이 닫힌 월드를 만든다.
        // 입력: 없음.
        // 출력: 서버 상태 0(모두 닫힘), 예측 없음, Version 0인 객체.
        public PredictedDoors()
        {
            GameMap.Boxes.CopyTo(_world);
            Rebuild();
        }

        // Bit i = GameMap.Doors[i] is open, as predicted.
        public byte OpenMask { get; private set; }
        // Changes whenever OpenMask does (door views follow it).
        public int Version { get; private set; }
        // The map boxes and the closed doors in the server's DoorSet order (DoorTests compares the two). Phase 13 D3: the
        // prediction gathers its world around the character from OpenMask instead (LocalPlayerPredictor).
        public ReadOnlySpan<Box> World => new ReadOnlySpan<Box>(_world, 0, _length);

        // 기능: 문이 예측상 열려 있는지 본다.
        // 입력: door - GameMap.Doors 번호(0..DoorCount-1).
        // 출력: 열려 있으면 true.
        public bool IsOpen(int door) => (OpenMask & (1 << door)) != 0;

        // 기능: World의 색인이 어느 문의 상자인지 찾는다.
        // 입력: worldIndex - World 안의 색인.
        // 출력: 문 번호, 맵 상자이거나 범위 밖이면 -1.
        public int DoorAt(int worldIndex)
        {
            int slot = worldIndex - GameMap.Boxes.Length;
            return slot >= 0 && worldIndex < _length ? _doorOfSlot[slot] : -1;
        }

        // 기능: 이동 Step을 막은 문을 서버 DoorSet.DoorBlocking과 같은 규칙으로 찾는다(막은 Collider가 문이면 그것, 아니면 Z 스윕이 막힌 문;
        //   Phase 13 D3: Collider는 종류와 id로 가리킨다).
        // 입력: step - 이동 Step 결과.
        // 출력: 문 번호, 문에 막히지 않았으면 -1.
        public int DoorBlocking(in StepResult step)
        {
            if (step.BlockedBy.Kind == ColliderKind.Door) return (int)step.BlockedBy.Id;
            return step.BlockedByZ.Kind == ColliderKind.Door ? (int)step.BlockedByZ.Id : -1;
        }

        // 기능: 서버의 DoorStates를 적용하고 모든 예측을 버린다.
        // 입력: openMask - 서버가 말한 열린 문 비트.
        // 출력: 반환값 없음. OpenMask가 서버 값이 되고, 바뀌었으면 Version과 World가 갱신된다.
        public void ApplyServer(byte openMask)
        {
            _server = openMask;
            Array.Clear(_predictedUntil, 0, _predictedUntil.Length);
            _predictedOpen = 0;
            Update();
        }

        // 기능: 내 행동(어깨 박치기, E)으로 문 상태를 PredictionSeconds 동안 예측한다(범위 밖 번호는 무시).
        // 입력: door - 문 번호, open - 예측할 상태(true = 열림), now - 예측 시계(초).
        // 출력: 반환값 없음. OpenMask가 바뀌면 Version과 World가 갱신된다.
        public void Predict(int door, bool open, float now)
        {
            if (door < 0 || door >= GameMap.DoorCount) return;
            _predictedUntil[door] = now + PredictionSeconds;
            int bit = 1 << door;
            _predictedOpen = (byte)(open ? _predictedOpen | bit : _predictedOpen & ~bit);
            Update();
        }

        // 기능: PredictionSeconds가 지난 예측을 버려 서버 상태로 되돌린다.
        // 입력: now - 예측 시계(초).
        // 출력: 반환값 없음. 하나라도 만료되면 OpenMask를 다시 계산한다.
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

        // 기능: 끊김 때 서버 상태 없이 되돌린다(라운드 시작처럼 모든 문 닫힘, 예측 없음).
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Reset() => ApplyServer(0);

        // 기능: 서버 상태 위에 살아 있는 예측을 덮어 OpenMask를 다시 계산한다.
        // 입력: 없음.
        // 출력: 반환값 없음. 마스크가 바뀌었을 때만 Version이 오르고 World를 다시 만든다.
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

        // 기능: 맵 상자 뒤에 닫힌 문 상자를 서버 DoorSet 순서로 다시 채운다(할당 없음).
        // 입력: 없음(OpenMask를 읽는다).
        // 출력: 반환값 없음. World의 길이와 각 칸의 문 번호가 갱신된다.
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
