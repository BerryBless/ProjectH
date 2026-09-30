using System;
using ProjectH.Client.Net;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using UnityEngine;

namespace ProjectH.Client.Game
{
    // Client-side prediction for the local player (Docs/Networking.md).
    // Collides with Shared TestArena.Boxes, the same boxes the server passes in Match.Tick.
    // Runs MovementSimulation at the server's tick rate, keeps a fixed 64-entry history of inputs and
    // results, and on each snapshot replays the inputs the server has not processed yet.
    public sealed class LocalPlayerPredictor
    {
        private const int HistorySize = 64;               // ~2 s at 30 Hz; older unacked input -> snap
        private const float SnapDistance = 2f;             // corrections larger than this are not smoothed
        private const float ErrorDecayPerSecond = 10f;
        private const float MatchEpsilon = 0.01f;
        private const float MaxAccumulatedSeconds = 0.25f; // after a hitch, do not burst-simulate

        private readonly InputCommand[] _inputs = new InputCommand[HistorySize];
        private readonly MoveState[] _results = new MoveState[HistorySize];
        private readonly float _stepSeconds;
        private MoveState _state;
        private MoveState _previous;
        private float _accumulator;
        private Vector3 _renderError;

        public LocalPlayerPredictor(int simHz, MoveState spawnState)
        {
            _stepSeconds = 1f / simHz;
            _state = spawnState;
            _previous = spawnState;
            RenderPosition = spawnState.Position.ToUnity();
        }

        public uint LastSeq { get; private set; }
        public Vector3 RenderPosition { get; private set; }
        public float RenderYaw => _state.Yaw;
        public Vector3 PredictedPosition => _state.Position.ToUnity();

        // Returns how many simulation steps ran (each generated one input).
        public int Advance(float deltaTime, Vector2 move, float yaw, bool sprint, ref bool jumpQueued)
        {
            _accumulator = Mathf.Min(_accumulator + deltaTime, MaxAccumulatedSeconds);
            int steps = 0;
            while (_accumulator >= _stepSeconds)
            {
                _accumulator -= _stepSeconds;
                // Same test as the loop condition, so this is true exactly on the frame's final step.
                bool lastStep = _accumulator < _stepSeconds;

                var buttons = InputButtons.None;
                if (sprint) buttons |= InputButtons.Sprint;
                // The jump rides on the last step: GameClient sends one packet per frame holding only the
                // newest MaxInputsPerPacket inputs, so on a hitch frame an earlier step may never be sent.
                if (jumpQueued && lastStep)
                {
                    buttons |= InputButtons.Jump;
                    jumpQueued = false;
                }

                var command = new InputCommand { Seq = ++LastSeq, MoveX = move.x, MoveY = move.y, Yaw = yaw, Buttons = buttons };
                _previous = _state;
                MovementSimulation.Step(ref _state, command, _stepSeconds, TestArena.Boxes);

                int slot = (int)(command.Seq % HistorySize);
                _inputs[slot] = command;
                _results[slot] = _state;
                steps++;
            }

            float alpha = _accumulator / _stepSeconds;
            _renderError = Vector3.Lerp(_renderError, Vector3.zero, 1f - Mathf.Exp(-ErrorDecayPerSecond * deltaTime));
            RenderPosition = Vector3.Lerp(_previous.Position.ToUnity(), _state.Position.ToUnity(), alpha) + _renderError;
            return steps;
        }

        // Newest inputs, oldest first (up to 3). Resending recent inputs covers single packet loss.
        public bool TryBuildInputPacket(out PlayerInputPacket packet)
        {
            packet = default;
            if (LastSeq == 0) return false;

            int count = (int)Math.Min(LastSeq, (uint)ProtocolConstants.MaxInputsPerPacket);
            packet.Count = (byte)count;
            for (int i = 0; i < count; i++)
            {
                uint seq = LastSeq - (uint)(count - 1 - i);
                packet.Set(i, _inputs[(int)(seq % HistorySize)]);
            }
            return true;
        }

        public void Reconcile(in SnapshotEntity server, uint ackSeq)
        {
            // Snapshot data is untrusted and NetClient does not validate it. A non-finite value would
            // replace the predicted state and every later step would stay NaN, so the entity is ignored.
            if (!IsFinite(server.Position.X) || !IsFinite(server.Position.Y) || !IsFinite(server.Position.Z) ||
                !IsFinite(server.VelocityY) || !IsFinite(server.Yaw))
            {
                return;
            }

            var authoritative = new MoveState { Position = server.Position, VelocityY = server.VelocityY, Yaw = server.Yaw };

            // Ack 0 says nothing about our inputs: the server has not processed any yet. Once inputs are
            // predicted the local state is newer than this snapshot, so snapping would stutter on join.
            if (ackSeq == 0 && LastSeq > 0) return;

            if (ackSeq == 0 || ackSeq > LastSeq || LastSeq - ackSeq >= HistorySize)
            {
                // Nothing to replay from (no input sent yet, or history already overwritten).
                _state = authoritative;
                _previous = authoritative;
                _renderError = Vector3.zero;
                return;
            }

            MoveState predicted = _results[(int)(ackSeq % HistorySize)];
            if (System.Numerics.Vector3.DistanceSquared(predicted.Position, authoritative.Position) < MatchEpsilon * MatchEpsilon &&
                Mathf.Abs(predicted.VelocityY - authoritative.VelocityY) < MatchEpsilon)
            {
                return;
            }

            // Misprediction: restart from the server state and replay unacknowledged inputs.
            System.Numerics.Vector3 oldPosition = _state.Position;
            _state = authoritative;
            _previous = authoritative;
            _results[(int)(ackSeq % HistorySize)] = authoritative;
            for (uint seq = ackSeq + 1; seq <= LastSeq; seq++)
            {
                int slot = (int)(seq % HistorySize);
                _previous = _state;
                MovementSimulation.Step(ref _state, _inputs[slot], _stepSeconds, TestArena.Boxes);
                _results[slot] = _state;
            }

            // Keep the rendered position continuous and let the difference decay, unless it is large.
            Vector3 correction = (oldPosition - _state.Position).ToUnity();
            _renderError = correction.sqrMagnitude > SnapDistance * SnapDistance ? Vector3.zero : _renderError + correction;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
