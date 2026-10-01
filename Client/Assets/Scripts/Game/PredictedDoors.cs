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

        public PredictedDoors()
        {
            GameMap.Boxes.CopyTo(_world);
            Rebuild();
        }

        // Bit i = GameMap.Doors[i] is open, as predicted.
        public byte OpenMask { get; private set; }
        // Changes whenever OpenMask does (door views follow it).
        public int Version { get; private set; }
        // What the local prediction moves against.
        public ReadOnlySpan<Box> World => new ReadOnlySpan<Box>(_world, 0, _length);

        public bool IsOpen(int door) => (OpenMask & (1 << door)) != 0;

        // The door whose box is at this index of World, or -1 for a map box.
        public int DoorAt(int worldIndex)
        {
            int slot = worldIndex - GameMap.Boxes.Length;
            return slot >= 0 && worldIndex < _length ? _doorOfSlot[slot] : -1;
        }

        // The door a step was stopped by, the same rule as the server's DoorSet.DoorBlocking: the box that stopped it, or
        // when that is no door, the Z sweep's. -1 = no door.
        public int DoorBlocking(in StepResult step)
        {
            int door = DoorAt(step.BlockedBy);
            return door >= 0 || step.BlockedByZ < 0 ? door : DoorAt(step.BlockedByZ);
        }

        // DoorStates: the server's word replaces every prediction.
        public void ApplyServer(byte openMask)
        {
            _server = openMask;
            Array.Clear(_predictedUntil, 0, _predictedUntil.Length);
            _predictedOpen = 0;
            Update();
        }

        public void Predict(int door, bool open, float now)
        {
            if (door < 0 || door >= GameMap.DoorCount) return;
            _predictedUntil[door] = now + PredictionSeconds;
            int bit = 1 << door;
            _predictedOpen = (byte)(open ? _predictedOpen | bit : _predictedOpen & ~bit);
            Update();
        }

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

        // Disconnect: no server state any more (every door closed, as at a round start).
        public void Reset() => ApplyServer(0);

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
