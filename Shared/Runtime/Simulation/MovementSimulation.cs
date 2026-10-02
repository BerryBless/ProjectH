using System;
using System.Numerics;

namespace ProjectH.Shared.Simulation
{
    // Phase 12: what one step reports besides the new state. The server acts on it (fall damage D10, shoulder bash D9);
    // client prediction uses the bash only.
    public struct StepResult
    {
        // Vertical speed (m/s, positive) the character hit the ground with this tick in Ground, Crouch or Slide mode.
        // 0 = no landing. Vault, glide and freefall landings report 0 (D10).
        public float LandingSpeed;
        // Phase 13 D3: the collider that stopped a horizontal move this tick (the X sweep's first), None = none. Named by
        // kind and id, not by its place in the gathered world.
        public ColliderId BlockedBy;
        // The collider that stopped the Z sweep, None = none (equal to BlockedBy when only Z was stopped). A door counts
        // when either sweep met it (D9: a doorway entered a little off-centre meets the jamb on one axis).
        public ColliderId BlockedByZ;
        // Sprinting this tick (snapshot flag, D11).
        public bool Sprinting;
        // Sprinting or sliding: a closed door in BlockedBy is shouldered open (D9).
        public bool Charging;
    }

    // The one piece of game logic allowed in Shared (see game-core-rules §4): client prediction and
    // the authoritative server run exactly this code. Pure math, no allocation, no engine types.
    // The character is an axis-aligned box (MoveSettings.HalfWidth, and a height that depends on the mode) with its feet
    // at MoveState.Position; the world is the terrain (Phase 6 D2-D4), the boxes passed in and, since Phase 13, the
    // slopes (building ramps and roofs, D2). Every terrain slope is walkable (GameMapTests), so the terrain never blocks a
    // horizontal move: it only sets the floor height under the feet. A slope is walked like the terrain where the feet
    // can climb onto it, and its slab blocks like a wall elsewhere.
    // Phase 12 D1: Step branches on MoveState.Mode into a few plain functions; there is no state object or class
    // hierarchy. Every number is in MovementTuning (new) or MoveSettings (Phase 1-6).
    public static class MovementSimulation
    {
        private const float DegToRad = 0.017453292f;
        private const int AxisX = 0;
        private const int AxisY = 1;
        private const int AxisZ = 2;

        // Phase 13: everything one step collides with. Box i is named by BoxIds[i] (or Static i without ids: the plain
        // span overloads); slopes by SlopeIds.
        private readonly ref struct Scene
        {
            public readonly ReadOnlySpan<Box> Boxes;
            public readonly ReadOnlySpan<ColliderId> BoxIds;
            public readonly ReadOnlySpan<Slope> Slopes;
            public readonly ReadOnlySpan<ColliderId> SlopeIds;
            public readonly HeightField Terrain;

            public Scene(ReadOnlySpan<Box> boxes, ReadOnlySpan<ColliderId> boxIds, ReadOnlySpan<Slope> slopes, ReadOnlySpan<ColliderId> slopeIds,
                HeightField terrain)
            {
                Boxes = boxes;
                BoxIds = boxIds;
                Slopes = slopes;
                SlopeIds = slopeIds;
                Terrain = terrain;
            }

            public ColliderId BoxId(int i) =>
                i < 0 ? ColliderId.None : i < BoxIds.Length ? BoxIds[i] : new ColliderId(ColliderKind.Static, (uint)i);

            public ColliderId SlopeId(int i) => i < 0 || i >= SlopeIds.Length ? ColliderId.None : SlopeIds[i];
        }

        public static void Step(ref MoveState state, in InputCommand input, float deltaTime, ReadOnlySpan<Box> world, HeightField terrain)
        {
            Step(ref state, input, deltaTime, world, terrain, out _);
        }

        public static void Step(ref MoveState state, in InputCommand input, float deltaTime, ReadOnlySpan<Box> world, HeightField terrain,
            out StepResult result)
        {
            Run(ref state, input, deltaTime, new Scene(world, default, default, default, terrain), out result);
        }

        // Phase 13 D3: the world gathered around the character (CollisionWorld.Gather at its position before this step).
        public static void Step(ref MoveState state, in InputCommand input, float deltaTime, CollisionWorld world, HeightField terrain,
            out StepResult result)
        {
            Run(ref state, input, deltaTime, new Scene(world.Boxes, world.BoxIds, world.Slopes, world.SlopeIds, terrain), out result);
        }

        private static void Run(ref MoveState state, in InputCommand input, float deltaTime, Scene scene, out StepResult result)
        {
            result = new StepResult();

            // Untrusted input: non-finite values become 0 and the move vector is clamped to length 1,
            // so no input can exceed the configured speed.
            float moveX = Finite(input.MoveX);
            float moveY = Finite(input.MoveY);
            float lengthSq = moveX * moveX + moveY * moveY;
            if (lengthSq > 1f)
            {
                float inv = 1f / MathF.Sqrt(lengthSq);
                moveX *= inv;
                moveY *= inv;
            }

            ApplyYaw(ref state, input.Yaw);

            // Unity convention: yaw rotates around +Y and yaw 0 faces +Z.
            // right = (cos, 0, -sin), forward = (sin, 0, cos).
            float yawRad = state.Yaw * DegToRad;
            float sin = MathF.Sin(yawRad);
            float cos = MathF.Cos(yawRad);
            var move = new Vector2(cos * moveX + sin * moveY, -sin * moveX + cos * moveY);

            switch (state.Mode)
            {
                case MovementMode.Vault:
                    StepVault(ref state, deltaTime);
                    return;
                case MovementMode.Transport:
                    // D5: the rider only looks around; DropTransport.Ride places it and handles the jump.
                    return;
                case MovementMode.Freefall:
                case MovementMode.Glide:
                    StepAir(ref state, input.Buttons, moveX, moveY, sin, cos, deltaTime, scene);
                    return;
            }
            // D8: a vault may start on a jump press while moving forward; it faces where the character looks.
            var forward = new Vector2(sin, cos);
            StepGround(ref state, input.Buttons, move, moveY > 0f ? forward : Vector2.Zero, deltaTime, scene, ref result);
        }

        // Ground, Crouch and Slide (D7), walking, sprinting, jumping and falling (D3 air momentum).
        // move: the input direction in world X/Z, length 0..1. vaultDirection: the facing direction when the input moves
        // forward (a vault may start), else zero.
        private static void StepGround(ref MoveState state, InputButtons buttons, Vector2 move, Vector2 vaultDirection, float deltaTime,
            Scene scene, ref StepResult result)
        {
            bool jump = (buttons & InputButtons.Jump) != 0;
            bool crouchHeld = (buttons & InputButtons.Crouch) != 0;
            bool sprintHeld = (buttons & InputButtons.Sprint) != 0;
            bool moving = move.X != 0f || move.Y != 0f;
            Vector3 position = state.Position;

            // 1) Leave any box or slab we start inside (reconcile snap, rounding, a piece built onto us): sweeps assume a
            //    free start (D4).
            Depenetrate(ref position, CollisionHeight(state.Mode), scene);

            // 2) Stateless ground check (D3). Snapping Y onto the surface keeps a standing player at an
            //    exact height, so grounded/airborne never alternates from rounding. A fall that ends in the snap is a
            //    landing too (D10).
            bool grounded = false;
            bool onTerrain = false;
            if (state.VelocityY <= 0f && TryFindGround(position, scene, out float groundY, out onTerrain))
            {
                grounded = true;
                if (state.VelocityY < 0f) result.LandingSpeed = -state.VelocityY;
                position.Y = groundY;
                state.VelocityY = 0f;
            }

            // 3) Posture (D7), on the ground only: crouch pressed while sprinting starts a slide, otherwise a crouch;
            //    released, the character stands up where the standing box fits.
            bool slideStarted = grounded && UpdatePosture(ref state, position, crouchHeld, sprintHeld, scene, deltaTime);

            // 4) Sprint and energy (D3, D7): Shift while moving in Ground mode on the ground, with energy. In the air Sprint
            //    is ignored (no cost, no charge): a sprint jump's speed was decided at the takeoff.
            bool sprinting = sprintHeld && grounded && state.Mode == MovementMode.Ground && !state.Exhausted && moving;
            if (!slideStarted) UpdateEnergy(ref state, sprinting, deltaTime);   // a slide start already paid and reset the delay
            result.Sprinting = sprinting;

            // 5) Horizontal velocity: the input on the ground, the slide's own speed, momentum plus air control in the air.
            //    Phase 13: a slide speeds up downhill on the terrain only, not on ramps or roofs (no gradient there).
            if (grounded && state.Mode == MovementMode.Slide)
            {
                SlideVelocity(ref state, position, onTerrain, scene.Terrain, deltaTime);
            }
            else if (grounded)
            {
                float speed = state.Mode == MovementMode.Crouch ? MovementTuning.CrouchSpeed
                    : sprinting ? MoveSettings.SprintSpeed : MoveSettings.WalkSpeed;
                state.HorizontalVelocity = move * speed;
            }
            else
            {
                AirControl(ref state, move, deltaTime);
            }

            // 6) Jump. On the ground, moving forward, an obstacle ahead turns it into a vault (D8), which moves this tick
            //    already. Otherwise a jump: from a crouch only where the standing box fits; a slide jump keeps the slide's
            //    speed; a sprint jump takes off faster (D3) at the same height.
            if (grounded && jump && state.Mode == MovementMode.Ground && vaultDirection != Vector2.Zero &&
                TryStartVault(ref state, position, vaultDirection, scene, deltaTime))
            {
                state.Position = position;
                StepVault(ref state, deltaTime);
                return;
            }
            bool walking = grounded;
            if (grounded && jump && TryStartJump(ref state, position, scene))
            {
                state.VelocityY = MoveSettings.JumpSpeed;
                if (sprinting) state.HorizontalVelocity = move * (MoveSettings.SprintSpeed * MovementTuning.SprintJumpSpeedScale);
                walking = false;
            }
            else if (!grounded)
            {
                state.VelocityY += MoveSettings.Gravity * deltaTime;
            }
            result.Charging = sprinting || state.Mode == MovementMode.Slide;
            float height = CollisionHeight(state.Mode);

            // 7) Axis-separated sweeps X -> Z (D2): a blocked axis stops, the other keeps moving. The blocked part of the
            //    velocity is gone (it matters in the air and in a slide, which carry it over).
            float startX = position.X;
            float startZ = position.Z;
            var start = new Vector3(startX, position.Y, startZ);   // before a step-up raises Y
            bool blocked = false;
            float wantX = state.HorizontalVelocity.X * deltaTime;
            if (MoveAxis(ref position, height, AxisX, wantX, grounded, scene, out ColliderId hitX))
            {
                state.HorizontalVelocity.X = 0f;
                result.BlockedBy = hitX;
                blocked = true;
            }
            Vector3 afterX = position;
            float wantZ = state.HorizontalVelocity.Y * deltaTime;
            if (MoveAxis(ref position, height, AxisZ, wantZ, grounded, scene, out ColliderId hitZ))
            {
                state.HorizontalVelocity.Y = 0f;
                if (result.BlockedBy.IsNone) result.BlockedBy = hitZ;
                result.BlockedByZ = hitZ;
                blocked = true;
            }
            // D7: a slide that runs into something ends in a crouch.
            if (blocked && state.Mode == MovementMode.Slide) state.Mode = MovementMode.Crouch;

            // 8) Phase 6 D4: uphill the terrain lifts the feet; downhill a walking character follows the slope instead
            //    of leaving the ground for a tick. MaxSlope bounds the drop over the distance moved, and the Y sweep
            //    stops on a box top on the way down. Phase 13: a ramp or roof is a floor like the terrain where the feet
            //    can climb onto it (its surface at most MaxSlope times the distance moved above them).
            float dx = position.X - startX;
            float dz = position.Z - startZ;
            float reach = MathF.Sqrt(dx * dx + dz * dz) * MoveSettings.MaxSlope + MoveSettings.GroundProbe;
            float floor = FloorUnder(position, position.Y + reach, scene, out _);
            if (position.Y < floor)
            {
                // Only where the body fits up there (a ramp under a roof or a floor): otherwise the move does not happen.
                // Phase 13 final review B11: a diagonal move then still goes along one axis where that fits (X first, then
                // Z), so walking along the headroom line does not stick.
                if (!TryLift(ref position, floor, height, start, scene, out ColliderId overhead))
                {
                    if (result.BlockedBy.IsNone) result.BlockedBy = overhead;
                    if (TryOneAxis(ref position, afterX, start, height, scene))
                    {
                        state.HorizontalVelocity.Y = 0f;
                        if (result.BlockedByZ.IsNone) result.BlockedByZ = overhead;
                    }
                    else
                    {
                        Vector3 zOnly = start;
                        MoveAxis(ref zOnly, height, AxisZ, wantZ, grounded, scene, out _);
                        if (TryOneAxis(ref position, zOnly, start, height, scene))
                        {
                            state.HorizontalVelocity.X = 0f;
                        }
                        else
                        {
                            state.HorizontalVelocity = Vector2.Zero;
                            if (result.BlockedByZ.IsNone) result.BlockedByZ = overhead;
                        }
                    }
                    if (state.Mode == MovementMode.Slide) state.Mode = MovementMode.Crouch;
                }
            }
            else if (walking)
            {
                float drop = position.Y - floor;
                if (drop > 0f && drop <= reach) position.Y += Sweep(position, height, AxisY, -drop, scene, out _);
            }

            // 9) Y sweep, the floor being the terrain and the slopes under the feet. Stopped on the way down is a landing
            //    (D10).
            float wantY = state.VelocityY * deltaTime;
            float movedY = Sweep(position, height, AxisY, wantY, scene, out _);
            if (movedY != wantY)
            {
                if (wantY < 0f) result.LandingSpeed = -state.VelocityY;
                state.VelocityY = 0f;   // landed or hit a ceiling
            }
            position.Y += movedY;

            state.Position = position;
        }

        // One horizontal axis: the box sweep (on the ground a box top within StepUpHeight is stepped onto when the body
        // fits there), then the slopes (D2): a move that would put the body into a slope's slab without being a climb onto
        // its surface does not happen at all. Returns true when the axis was blocked (hit says by what).
        private static bool MoveAxis(ref Vector3 position, float height, int axis, float want, bool stepUp, Scene scene, out ColliderId hit)
        {
            float moved = Sweep(position, height, axis, want, scene, out int boxHit);
            // The move starts here: the position, or the step-up's lifted one (kept only if the slopes let the move happen).
            Vector3 from = position;
            if (stepUp && moved != want && boxHit >= 0)
            {
                float top = scene.Boxes[boxHit].Max.Y;
                float rise = top - position.Y;
                if (rise > 0f && rise <= MovementTuning.StepUpHeight)
                {
                    Vector3 lifted = position;
                    lifted.Y = top;
                    if (Fits(lifted, height, scene))
                    {
                        float liftedMoved = Sweep(lifted, height, axis, want, scene, out int liftedHit);
                        if (MathF.Abs(liftedMoved) > MathF.Abs(moved))
                        {
                            from = lifted;
                            moved = liftedMoved;
                            boxHit = liftedHit;
                        }
                    }
                }
            }
            hit = scene.BoxId(boxHit);
            if (moved != 0f)
            {
                Vector3 to = from;
                if (axis == AxisX) to.X += moved;
                else to.Z += moved;
                int slope = SlopeBlocking(from, to, height, MathF.Abs(moved), scene);
                if (slope >= 0)
                {
                    hit = scene.SlopeId(slope);
                    return want != 0f;
                }
                position = to;
            }
            return moved != want;
        }

        // The first slope whose slab the body would enter at `to` (moved from `from` by `moved` metres) other than by
        // climbing onto it, or -1. A slab the body was already in at `from` does not block (it may walk out).
        private static int SlopeBlocking(Vector3 from, Vector3 to, float height, float moved, Scene scene)
        {
            ReadOnlySpan<Slope> slopes = scene.Slopes;
            float climb = moved * MoveSettings.MaxSlope + MoveSettings.GroundProbe;
            for (int i = 0; i < slopes.Length; i++)
            {
                if (!InSlab(to, height, slopes[i], out float high)) continue;
                if (high - to.Y <= climb) continue;
                if (InSlab(from, height, slopes[i], out _)) continue;
                return i;
            }
            return -1;
        }

        // The body at these feet is inside the slope's slab (between its bottom and its surface, under the footprint),
        // by more than Skin. high: the surface's highest point under the footprint.
        private static bool InSlab(Vector3 feet, float height, in Slope slope, out float high)
        {
            if (!slope.Range(feet.X - MoveSettings.HalfWidth, feet.Z - MoveSettings.HalfWidth, feet.X + MoveSettings.HalfWidth,
                    feet.Z + MoveSettings.HalfWidth, out _, out high, out float bottom)) return false;
            return feet.Y < high - MoveSettings.Skin && feet.Y + height > bottom + MoveSettings.Skin;
        }

        // Task 1 fix round 1: raises the feet onto the floor found under them (a slope's surface) only where the body fits
        // there: no box by more than Skin (as Depenetrate counts it) and no slab. Otherwise the feet go back to `start` (this
        // tick's horizontal move does not happen) and overhead names the box or slope in the way; false.
        private static bool TryLift(ref Vector3 position, float floor, float height, Vector3 start, Scene scene, out ColliderId overhead)
        {
            Vector3 lifted = position;
            lifted.Y = floor;
            if (!Obstructed(lifted, height, scene, out overhead))
            {
                position = lifted;
                return true;
            }
            position = start;
            return false;
        }

        // Final review B11: the feet moved along one axis only (candidate, from start), lifted onto the floor there when it is
        // higher and the body fits up there. False (position unchanged) when that axis did not move or the lift does not fit.
        private static bool TryOneAxis(ref Vector3 position, Vector3 candidate, Vector3 start, float height, Scene scene)
        {
            float dx = candidate.X - start.X;
            float dz = candidate.Z - start.Z;
            if (dx == 0f && dz == 0f) return false;
            float reach = MathF.Sqrt(dx * dx + dz * dz) * MoveSettings.MaxSlope + MoveSettings.GroundProbe;
            float floor = FloorUnder(candidate, candidate.Y + reach, scene, out _);
            if (candidate.Y < floor)
            {
                candidate.Y = floor;
                if (Obstructed(candidate, height, scene, out _)) return false;
            }
            position = candidate;
            return true;
        }

        // The body of this height at these feet is in a box by more than Skin or in a slope's slab; blocker names the first
        // such box, else the first such slope.
        private static bool Obstructed(Vector3 feet, float height, Scene scene, out ColliderId blocker)
        {
            GetBounds(feet, height, out Vector3 min, out Vector3 max);
            ReadOnlySpan<Box> world = scene.Boxes;
            for (int i = 0; i < world.Length; i++)
            {
                if (OverlapDepth(min, max, world[i]) <= MoveSettings.Skin) continue;
                blocker = scene.BoxId(i);
                return true;
            }
            ReadOnlySpan<Slope> slopes = scene.Slopes;
            for (int i = 0; i < slopes.Length; i++)
            {
                if (!InSlab(feet, height, slopes[i], out _)) continue;
                blocker = scene.SlopeId(i);
                return true;
            }
            blocker = ColliderId.None;
            return false;
        }

        // The highest floor under the footprint at or below `upTo`: the terrain under the feet or a slope's surface.
        // onTerrain: the terrain is that floor.
        private static float FloorUnder(Vector3 feet, float upTo, Scene scene, out bool onTerrain)
        {
            float floor = scene.Terrain.Height(feet.X, feet.Z);
            onTerrain = true;
            ReadOnlySpan<Slope> slopes = scene.Slopes;
            for (int i = 0; i < slopes.Length; i++)
            {
                if (!slopes[i].Range(feet.X - MoveSettings.HalfWidth, feet.Z - MoveSettings.HalfWidth, feet.X + MoveSettings.HalfWidth,
                        feet.Z + MoveSettings.HalfWidth, out _, out float high, out _)) continue;
                if (high > upTo || high <= floor) continue;
                floor = high;
                onTerrain = false;
            }
            return floor;
        }

        // D7: crouch held on the ground: a slide while sprinting (Sprint held, not exhausted, at least SlideMinStartSpeed),
        // otherwise a crouch. So a crouch held through a landing, or pressed at walking speed, never slides. Released: stand
        // up where the standing box fits (a slide that cannot stand becomes a crouch).
        // Starting a slide costs SlideStartEnergyCost and restarts the recovery delay, like a sprint tick; reaching 0 sets
        // Exhausted (and an exhausted character cannot start a slide). Without this cost a Sprint+Crouch hop chain is free:
        // each landing starts a slide before any sprint tick is counted, and the air costs nothing.
        // Returns true when a slide started this tick.
        private static bool UpdatePosture(ref MoveState state, Vector3 position, bool crouchHeld, bool sprintHeld, Scene scene,
            float deltaTime)
        {
            switch (state.Mode)
            {
                case MovementMode.Ground:
                    if (!crouchHeld) return false;
                    float speed = state.HorizontalVelocity.Length();
                    if (sprintHeld && !state.Exhausted && speed >= MovementTuning.SlideMinStartSpeed)
                    {
                        state.Mode = MovementMode.Slide;
                        state.HorizontalVelocity *= MathF.Max(speed, MovementTuning.SlideStartSpeed) / speed;
                        Spend(ref state, (int)MathF.Round(MovementTuning.SlideStartEnergyCost * MovementTuning.EnergyScale), deltaTime);
                        return true;
                    }
                    state.Mode = MovementMode.Crouch;
                    return false;

                case MovementMode.Crouch:
                    if (!crouchHeld && CanStand(position, scene)) state.Mode = MovementMode.Ground;
                    return false;

                case MovementMode.Slide:
                    if (!crouchHeld) state.Mode = CanStand(position, scene) ? MovementMode.Ground : MovementMode.Crouch;
                    return false;
            }
            return false;
        }

        // D3: the slide keeps its direction and loses SlideFriction per second; on the terrain a downhill slope adds
        // slope x gravity x SlideSlopeFactor. Below SlideMinSpeed it ends in a crouch.
        private static void SlideVelocity(ref MoveState state, Vector3 position, bool onTerrain, HeightField terrain, float deltaTime)
        {
            Vector2 velocity = state.HorizontalVelocity;
            float speed = velocity.Length();
            if (speed < MovementTuning.SlideMinSpeed)
            {
                state.Mode = MovementMode.Crouch;
                return;
            }
            Vector2 direction = velocity / speed;
            speed -= MovementTuning.SlideFriction * deltaTime;
            if (onTerrain)
            {
                // Rise per metre along the slide; negative is downhill.
                float rise = Vector2.Dot(terrain.Gradient(position.X, position.Z), direction);
                if (rise < 0f) speed += -rise * -MoveSettings.Gravity * MovementTuning.SlideSlopeFactor * deltaTime;
            }
            if (speed > MovementTuning.SlideMaxSpeed) speed = MovementTuning.SlideMaxSpeed;
            if (speed < MovementTuning.SlideMinSpeed) state.Mode = MovementMode.Crouch;
            state.HorizontalVelocity = direction * speed;
        }

        // D3: in the air the velocity carries over. The input accelerates it by AirAcceleration, and the speed never
        // grows above the larger of what it was and the walking speed (a standing jump can still drift).
        private static void AirControl(ref MoveState state, Vector2 move, float deltaTime)
        {
            if (move.X == 0f && move.Y == 0f) return;
            Vector2 velocity = state.HorizontalVelocity;
            float limit = MathF.Max(velocity.Length(), state.Mode == MovementMode.Crouch ? MovementTuning.CrouchSpeed : MoveSettings.WalkSpeed);
            velocity += move * (MovementTuning.AirAcceleration * deltaTime);
            float speed = velocity.Length();
            if (speed > limit) velocity *= limit / speed;
            state.HorizontalVelocity = velocity;
        }

        // D6: freefall and glide. A jump press or the ground within GlideAutoDeployHeight opens the glider (never the
        // other way). Freefall accelerates down to its terminal speed, the glider sinks at a steady speed; the input
        // steers the horizontal velocity in the character's frame towards the mode's forward, side and back limits.
        // Crouch is ignored (D12). Touching the ground lands in Ground mode with no horizontal velocity and no fall damage
        // (LandingSpeed stays 0, D10). The outer walls are only 4 m high, so the air is bounded by them too.
        private static void StepAir(ref MoveState state, InputButtons buttons, float moveX, float moveY, float sin, float cos, float deltaTime,
            Scene scene)
        {
            Vector3 position = state.Position;
            Depenetrate(ref position, MoveSettings.Height, scene);

            bool freefall = state.Mode == MovementMode.Freefall;
            if (freefall && ((buttons & InputButtons.Jump) != 0 || GroundDistance(position, scene) <= MovementTuning.GlideAutoDeployHeight))
            {
                state.Mode = MovementMode.Glide;
                freefall = false;
            }

            if (freefall)
            {
                state.VelocityY += MoveSettings.Gravity * deltaTime;
                if (state.VelocityY < -MovementTuning.FreefallTerminalSpeed) state.VelocityY = -MovementTuning.FreefallTerminalSpeed;
            }
            else
            {
                state.VelocityY = -MovementTuning.GlideFallSpeed;
            }

            // The horizontal velocity in the character's frame, moved towards the input's target by at most accel x dt.
            var forward = new Vector2(sin, cos);
            var right = new Vector2(cos, -sin);
            float alongForward = Vector2.Dot(state.HorizontalVelocity, forward);
            float alongRight = Vector2.Dot(state.HorizontalVelocity, right);
            float targetForward = moveY >= 0f
                ? moveY * (freefall ? MovementTuning.FreefallForwardSpeed : MovementTuning.GlideForwardSpeed)
                : moveY * (freefall ? MovementTuning.FreefallBackSpeed : MovementTuning.GlideBackSpeed);
            float targetRight = moveX * (freefall ? MovementTuning.FreefallSideSpeed : MovementTuning.GlideSideSpeed);
            var change = new Vector2(targetForward - alongForward, targetRight - alongRight);
            float maxChange = (freefall ? MovementTuning.FreefallAcceleration : MovementTuning.GlideAcceleration) * deltaTime;
            float length = change.Length();
            if (length > maxChange) change *= maxChange / length;
            alongForward += change.X;
            alongRight += change.Y;
            state.HorizontalVelocity = forward * alongForward + right * alongRight;

            float startX = position.X;
            float startZ = position.Z;
            Vector3 start = position;
            if (MoveAxis(ref position, MoveSettings.Height, AxisX, state.HorizontalVelocity.X * deltaTime, false, scene, out _)) state.HorizontalVelocity.X = 0f;
            if (MoveAxis(ref position, MoveSettings.Height, AxisZ, state.HorizontalVelocity.Y * deltaTime, false, scene, out _)) state.HorizontalVelocity.Y = 0f;

            const float bound = GameMap.HalfSize - MoveSettings.HalfWidth - MoveSettings.Skin;
            if (position.X > bound || position.X < -bound)
            {
                position.X = position.X > 0f ? bound : -bound;
                state.HorizontalVelocity.X = 0f;
            }
            if (position.Z > bound || position.Z < -bound)
            {
                position.Z = position.Z > 0f ? bound : -bound;
                state.HorizontalVelocity.Y = 0f;
            }

            bool landed = false;
            float dx = position.X - startX;
            float dz = position.Z - startZ;
            float reach = MathF.Sqrt(dx * dx + dz * dz) * MoveSettings.MaxSlope + MoveSettings.GroundProbe;
            float floor = FloorUnder(position, position.Y + reach, scene, out _);
            if (position.Y < floor)
            {
                // Came over rising terrain (or onto a ramp), where the body fits: otherwise the move does not happen.
                if (TryLift(ref position, floor, MoveSettings.Height, start, scene, out _)) landed = true;
                else state.HorizontalVelocity = Vector2.Zero;
            }
            float wantY = state.VelocityY * deltaTime;
            float movedY = Sweep(position, MoveSettings.Height, AxisY, wantY, scene, out _);
            position.Y += movedY;
            if (landed || movedY != wantY)
            {
                state.Mode = MovementMode.Ground;
                state.VelocityY = 0f;
                state.HorizontalVelocity = Vector2.Zero;
            }
            state.Position = position;
        }

        // D6: height of the feet above the ground under them: the terrain or the highest box top below the feet under
        // the character's footprint.
        public static float GroundDistance(Vector3 feet, ReadOnlySpan<Box> world, HeightField terrain) =>
            GroundDistance(feet, new Scene(world, default, default, default, terrain));

        // Phase 13: the same in a gathered world: the terrain, the gathered boxes and slopes. Building pieces are gathered
        // only within CollisionWorld.PieceLevelRadius levels of the feet, so a piece further below is not seen: a freefall
        // opens the glider by the terrain and the map's boxes, and by a piece only once it is that close (glide and
        // freefall landings do no damage, D10).
        public static float GroundDistance(Vector3 feet, CollisionWorld world, HeightField terrain) =>
            GroundDistance(feet, new Scene(world.Boxes, world.BoxIds, world.Slopes, world.SlopeIds, terrain));

        private static float GroundDistance(Vector3 feet, Scene scene)
        {
            float ground = FloorUnder(feet, feet.Y + MoveSettings.GroundProbe, scene, out _);
            float minX = feet.X - MoveSettings.HalfWidth;
            float maxX = feet.X + MoveSettings.HalfWidth;
            float minZ = feet.Z - MoveSettings.HalfWidth;
            float maxZ = feet.Z + MoveSettings.HalfWidth;
            ReadOnlySpan<Box> world = scene.Boxes;
            for (int i = 0; i < world.Length; i++)
            {
                ref readonly Box box = ref world[i];
                if (box.Max.X <= minX || box.Min.X >= maxX || box.Max.Z <= minZ || box.Min.Z >= maxZ) continue;
                if (box.Max.Y <= feet.Y + MoveSettings.GroundProbe && box.Max.Y > ground) ground = box.Max.Y;
            }
            return feet.Y - ground;
        }

        // D8: a vault starts only when all four checks pass: (1) an obstacle within VaultReach ahead, standing on the
        // feet's level, (2) its top in hurdle or mantle range, (3) room to stand at the destination, (4) the destination
        // inside no box and above the terrain. A low obstacle is hurdled only at sprint speed (else this is a normal jump);
        // a hurdle lands HurdleLandingGap past the far side, or on the top when the obstacle is deeper than HurdleMaxDepth
        // or there is no room behind it. A mantle stands MantleInset inside the top's edge. The vault then moves at one
        // constant velocity for its ticks, so the snapshot's velocities and ModeTicks are all a replay needs.
        // Linear passes over the boxes; it runs on every grounded tick that reads the jump level (forward + jump held).
        // The straight path must be clear of every box except the obstacle itself (a wall or door between is a blocker),
        // and a hurdle lands on a surface within VaultBaseTolerance of the feet's level. Phase 13: obstacles are boxes
        // (walls and floors included); a ramp or roof is never one, but a destination on one counts as ground.
        private static bool TryStartVault(ref MoveState state, Vector3 feet, Vector2 direction, Scene scene, float deltaTime)
        {
            ReadOnlySpan<Box> world = scene.Boxes;
            int obstacle = -1;
            float nearest = MovementTuning.VaultReach;
            for (int i = 0; i < world.Length; i++)
            {
                ref readonly Box box = ref world[i];
                float rise = box.Max.Y - feet.Y;
                if (rise < MovementTuning.HurdleMinHeight || rise > MovementTuning.MantleMaxHeight) continue;
                if (box.Min.Y > feet.Y + MovementTuning.VaultBaseTolerance) continue;   // not standing on our level
                // When the standing footprint moving along direction would touch the box.
                if (!RayBox2D(feet.X, feet.Z, direction, box.Min.X - MoveSettings.HalfWidth, box.Min.Z - MoveSettings.HalfWidth,
                        box.Max.X + MoveSettings.HalfWidth, box.Max.Z + MoveSettings.HalfWidth, out float enter, out _)) continue;
                if (enter < -MoveSettings.Skin || enter > nearest) continue;
                nearest = enter;
                obstacle = i;
            }
            if (obstacle < 0) return false;

            Box target = world[obstacle];
            float height = target.Max.Y - feet.Y;
            bool hurdle = height <= MovementTuning.HurdleMaxHeight;
            if (hurdle && state.HorizontalVelocity.Length() < MovementTuning.HurdleMinSpeed) return false;
            // Where the line through the feet crosses the obstacle itself (a corner graze is no vault).
            if (!RayBox2D(feet.X, feet.Z, direction, target.Min.X, target.Min.Z, target.Max.X, target.Max.Z, out float face, out float back))
                return false;

            Vector3 destination = default;
            bool found = false;
            if (hurdle && back - face <= MovementTuning.HurdleMaxDepth)
            {
                float reach = back + MoveSettings.HalfWidth + MovementTuning.HurdleLandingGap;
                float x = feet.X + direction.X * reach;
                float z = feet.Z + direction.Y * reach;
                float surface = SurfaceUnder(x, z, feet.Y, scene);
                destination = new Vector3(x, surface, z);
                found = MathF.Abs(surface - feet.Y) <= MovementTuning.VaultBaseTolerance && IsFreeStand(destination, scene);
            }
            if (!found)
            {
                float reach = MathF.Min(face + MovementTuning.MantleInset, (face + back) * 0.5f);
                destination = new Vector3(feet.X + direction.X * reach, target.Max.Y, feet.Z + direction.Y * reach);
                found = IsFreeStand(destination, scene);
            }
            if (!found || !IsPathClear(feet, destination, obstacle, world)) return false;
            // Skin above the surface: the constant-velocity sum may land a hair low, and the next ground check snaps the
            // feet onto it anyway.
            destination.Y += MoveSettings.Skin;

            byte ticks = TicksOf(hurdle ? MovementTuning.HurdleSeconds : MovementTuning.MantleSeconds, deltaTime);
            float seconds = ticks * deltaTime;
            state.Mode = MovementMode.Vault;
            state.ModeTicks = ticks;
            state.HorizontalVelocity = new Vector2(destination.X - feet.X, destination.Z - feet.Z) / seconds;
            state.VelocityY = (destination.Y - feet.Y) / seconds;
            return true;
        }

        // The highest surface under a footprint at (x, z) that is not above the feet's level (plus the vault tolerance): the
        // terrain, a slope or a box top.
        private static float SurfaceUnder(float x, float z, float feetY, Scene scene)
        {
            float surface = FloorUnder(new Vector3(x, feetY, z), feetY + MovementTuning.VaultBaseTolerance, scene, out _);
            ReadOnlySpan<Box> world = scene.Boxes;
            for (int i = 0; i < world.Length; i++)
            {
                ref readonly Box box = ref world[i];
                if (box.Max.X <= x - MoveSettings.HalfWidth || box.Min.X >= x + MoveSettings.HalfWidth ||
                    box.Max.Z <= z - MoveSettings.HalfWidth || box.Min.Z >= z + MoveSettings.HalfWidth) continue;
                if (box.Max.Y > surface && box.Max.Y <= feetY + MovementTuning.VaultBaseTolerance) surface = box.Max.Y;
            }
            return surface;
        }

        // The swept box of the straight vault path (start to destination, standing height) overlaps no box except the
        // vault's own obstacle, which the path passes through or over by design. Boxes whose top is at the feet are floor.
        private static bool IsPathClear(Vector3 from, Vector3 to, int skip, ReadOnlySpan<Box> world)
        {
            float minX = MathF.Min(from.X, to.X) - MoveSettings.HalfWidth;
            float maxX = MathF.Max(from.X, to.X) + MoveSettings.HalfWidth;
            float minZ = MathF.Min(from.Z, to.Z) - MoveSettings.HalfWidth;
            float maxZ = MathF.Max(from.Z, to.Z) + MoveSettings.HalfWidth;
            // The bottom is the start's feet: a hurdle that lands lower (a step down behind it) passes over the floor there.
            float minY = from.Y + MoveSettings.Skin;
            float maxY = MathF.Max(from.Y, to.Y) + MoveSettings.Height;
            for (int i = 0; i < world.Length; i++)
            {
                if (i == skip) continue;
                ref readonly Box box = ref world[i];
                if (minX < box.Max.X && maxX > box.Min.X && minY < box.Max.Y && maxY > box.Min.Y && minZ < box.Max.Z && maxZ > box.Min.Z)
                    return false;
            }
            return true;
        }

        // The standing box fits at these feet: no box or slab overlaps it and the terrain is not above the feet.
        private static bool IsFreeStand(Vector3 feet, Scene scene) =>
            feet.Y >= scene.Terrain.Height(feet.X, feet.Z) - MoveSettings.GroundProbe && CanStand(feet, scene);

        // 2D ray from (x, z) along a unit direction against an X/Z rectangle: the distances where it enters and leaves.
        // False when it misses or the rectangle is behind.
        private static bool RayBox2D(float x, float z, Vector2 direction, float minX, float minZ, float maxX, float maxZ, out float enter, out float leave)
        {
            enter = float.NegativeInfinity;
            leave = float.PositiveInfinity;
            if (!Slab(x, direction.X, minX, maxX, ref enter, ref leave)) return false;
            if (!Slab(z, direction.Y, minZ, maxZ, ref enter, ref leave)) return false;
            return leave >= 0f && enter <= leave;
        }

        private static bool Slab(float origin, float direction, float min, float max, ref float enter, ref float leave)
        {
            if (MathF.Abs(direction) < 1e-6f) return origin > min && origin < max;
            float t1 = (min - origin) / direction;
            float t2 = (max - origin) / direction;
            if (t1 > t2)
            {
                float swap = t1;
                t1 = t2;
                t2 = swap;
            }
            if (t1 > enter) enter = t1;
            if (t2 < leave) leave = t2;
            return enter <= leave;
        }

        // D8: one tick of a vault: the constant velocity set at its start, no collision (the path was checked then). The
        // last tick ends it standing in Ground mode.
        private static void StepVault(ref MoveState state, float deltaTime)
        {
            if (state.ModeTicks > 0)
            {
                state.Position += new Vector3(state.HorizontalVelocity.X, state.VelocityY, state.HorizontalVelocity.Y) * deltaTime;
                state.ModeTicks--;
            }
            if (state.ModeTicks > 0) return;
            state.Mode = MovementMode.Ground;
            state.VelocityY = 0f;
            state.HorizontalVelocity = Vector2.Zero;
        }

        // A jump from the ground. A crouch stands up first and cannot jump where the standing box does not fit; a slide
        // jump becomes a normal jump with the slide's velocity.
        private static bool TryStartJump(ref MoveState state, Vector3 position, Scene scene)
        {
            if (state.Mode == MovementMode.Crouch || state.Mode == MovementMode.Slide)
            {
                if (!CanStand(position, scene)) return false;
                state.Mode = MovementMode.Ground;
            }
            return true;
        }

        // D3: sprinting costs SprintEnergyCostPerSecond and restarts the recovery delay; after the delay the energy
        // recovers at EnergyRecoveryPerSecond. Running out sets Exhausted (the sprint of this tick is already paid), which
        // stays until SprintResumeEnergy is back. Integer hundredths per tick, so both sides and the wire agree exactly.
        private static void UpdateEnergy(ref MoveState state, bool sprinting, float deltaTime)
        {
            if (sprinting)
            {
                Spend(ref state, PerTick(MovementTuning.SprintEnergyCostPerSecond, deltaTime), deltaTime);
                return;
            }
            if (state.EnergyDelayTicks > 0)
            {
                state.EnergyDelayTicks--;
                return;
            }
            int left = state.EnergySpent - PerTick(MovementTuning.EnergyRecoveryPerSecond, deltaTime);
            state.EnergySpent = (ushort)(left > 0 ? left : 0);
            if (state.Exhausted && state.Energy >= MovementTuning.SprintResumeEnergy) state.Exhausted = false;
        }

        // Spends energy hundredths (a sprint tick, a slide start): 0 left sets Exhausted; the recovery delay starts over.
        private static void Spend(ref MoveState state, int hundredths, float deltaTime)
        {
            int spent = state.EnergySpent + hundredths;
            if (spent >= MoveState.MaxEnergyHundredths)
            {
                spent = MoveState.MaxEnergyHundredths;
                state.Exhausted = true;
            }
            state.EnergySpent = (ushort)spent;
            state.EnergyDelayTicks = TicksOf(MovementTuning.EnergyRecoveryDelaySeconds, deltaTime);
        }

        // Energy hundredths per tick for a per-second rate.
        private static int PerTick(float perSecond, float deltaTime) =>
            (int)MathF.Round(perSecond * MovementTuning.EnergyScale * deltaTime);

        // Whole ticks for a duration (at least 1, at most 255).
        private static byte TicksOf(float seconds, float deltaTime)
        {
            float ticks = MathF.Round(seconds / deltaTime);
            if (!(ticks >= 1f)) return 1;
            return ticks > 255f ? (byte)255 : (byte)ticks;
        }

        // D7, D13: the collision (and hit) box height of a mode.
        public static float CollisionHeight(MovementMode mode) =>
            mode == MovementMode.Crouch || mode == MovementMode.Slide ? MovementTuning.CrouchHeight : MoveSettings.Height;

        // D7: the standing box fits at these feet (nothing in the 0.6 m above a crouch).
        public static bool CanStand(Vector3 feet, ReadOnlySpan<Box> world) => !OverlapsAny(feet, MoveSettings.Height, world);

        // Phase 13: neither a box nor a slope's slab (a ramp or roof overhead) is in the standing box.
        private static bool CanStand(Vector3 feet, Scene scene) => Fits(feet, MoveSettings.Height, scene);

        // A character box of this height at these feet is in no box and no slab.
        private static bool Fits(Vector3 feet, float height, Scene scene)
        {
            if (OverlapsAny(feet, height, scene.Boxes)) return false;
            ReadOnlySpan<Slope> slopes = scene.Slopes;
            for (int i = 0; i < slopes.Length; i++)
            {
                if (InSlab(feet, height, slopes[i], out _)) return false;
            }
            return true;
        }

        public static bool IsGrounded(in MoveState state, ReadOnlySpan<Box> world, HeightField terrain)
        {
            return state.VelocityY <= 0f && TryFindGround(state.Position, new Scene(world, default, default, default, terrain), out _, out _);
        }

        public static bool IsGrounded(in MoveState state, CollisionWorld world, HeightField terrain)
        {
            return state.VelocityY <= 0f &&
                   TryFindGround(state.Position, new Scene(world.Boxes, world.BoxIds, world.Slopes, world.SlopeIds, terrain), out _, out _);
        }

        // True if the standing character box at these feet overlaps any box by more than zero on every axis.
        // Touching faces (as after a sweep or a ground snap) do not count.
        public static bool OverlapsAny(Vector3 feet, ReadOnlySpan<Box> world) => OverlapsAny(feet, MoveSettings.Height, world);

        // The same for a character box of this height.
        public static bool OverlapsAny(Vector3 feet, float height, ReadOnlySpan<Box> world)
        {
            GetBounds(feet, height, out Vector3 min, out Vector3 max);
            for (int i = 0; i < world.Length; i++)
            {
                ref readonly Box box = ref world[i];
                if (min.X < box.Max.X && max.X > box.Min.X &&
                    min.Y < box.Max.Y && max.Y > box.Min.Y &&
                    min.Z < box.Max.Z && max.Z > box.Min.Z)
                {
                    return true;
                }
            }
            return false;
        }

        // Phase 13: a character box of this height is inside a gathered box by more than Skin on every axis, or inside a
        // slope's slab. Tests use it for "nothing ever traps or swallows the character". piecesOnly: building pieces only
        // (every slope is a piece), for the server's movement self-check.
        public static bool Penetrates(Vector3 feet, float height, CollisionWorld world, bool piecesOnly = false)
        {
            GetBounds(feet, height, out Vector3 min, out Vector3 max);
            ReadOnlySpan<Box> boxes = world.Boxes;
            ReadOnlySpan<ColliderId> ids = world.BoxIds;
            for (int i = 0; i < boxes.Length; i++)
            {
                if (piecesOnly && ids[i].Kind != ColliderKind.Piece) continue;
                if (OverlapDepth(min, max, boxes[i]) > MoveSettings.Skin) return true;
            }
            ReadOnlySpan<Slope> slopes = world.Slopes;
            for (int i = 0; i < slopes.Length; i++)
            {
                if (InSlab(feet, height, slopes[i], out _)) return true;
            }
            return false;
        }

        // Phase 13 D2: the start of a step leaves every box and slab it is in.
        //  - Below the terrain: onto it.
        //  - In a slope's slab (a ramp or roof built onto the character, rounding): onto its surface ("올라선다", D9).
        //  - In a box: out through the face that needs the shortest push, in the fixed order -X, +X, -Z, +Z, +Y, -Y for
        //    ties. Phase 13 (pieces touch side by side, unlike the map's boxes): a push that would put the character into
        //    another box it was not in is skipped for the next shortest one, so two touching walls never push it back
        //    and forth between them; only when every direction does that is the shortest taken. Down only while the feet
        //    stay above the floor. A second pass catches what the first moved it into.
        private static void Depenetrate(ref Vector3 feet, float height, Scene scene)
        {
            float terrain = scene.Terrain.Height(feet.X, feet.Z);
            if (feet.Y < terrain) feet.Y = terrain;

            ReadOnlySpan<Slope> slopes = scene.Slopes;
            for (int i = 0; i < slopes.Length; i++)
            {
                // Task 1 fix round 1: not into a box or slab overhead (a roof or floor above the ramp); the body then stays
                // in this slab, which never blocks walking out of it.
                if (InSlab(feet, height, slopes[i], out float high))
                {
                    Vector3 lifted = feet;
                    lifted.Y = high;
                    if (!EntersAnother(feet, lifted, height, -1, scene)) feet = lifted;
                }
            }

            ReadOnlySpan<Box> world = scene.Boxes;
            Span<float> push = stackalloc float[6];
            for (int pass = 0; pass < 2; pass++)
            {
                bool moved = false;
                for (int i = 0; i < world.Length; i++)
                {
                    ref readonly Box box = ref world[i];
                    GetBounds(feet, height, out Vector3 min, out Vector3 max);
                    if (OverlapDepth(min, max, box) <= MoveSettings.Skin) continue;

                    // Distance to clear each face.
                    push[0] = max.X - box.Min.X;
                    push[1] = box.Max.X - min.X;
                    push[2] = max.Z - box.Min.Z;
                    push[3] = box.Max.Z - min.Z;
                    push[4] = box.Max.Y - min.Y;
                    push[5] = max.Y - box.Min.Y;
                    // Pushing down is only allowed while the feet stay above the floor.
                    float floor = FloorUnder(feet, feet.Y + MoveSettings.GroundProbe, scene, out _);
                    if (feet.Y - push[5] - MoveSettings.Skin < floor) push[5] = float.PositiveInfinity;

                    int chosen = -1;
                    int shortest = -1;
                    for (int round = 0; round < 6; round++)
                    {
                        int best = -1;
                        for (int d = 0; d < 6; d++)
                        {
                            if (push[d] >= 0f && (best < 0 || push[d] < push[best])) best = d;
                        }
                        if (best < 0 || float.IsPositiveInfinity(push[best])) break;
                        if (shortest < 0) shortest = best;
                        if (!EntersAnother(feet, Pushed(feet, best, push[best] + MoveSettings.Skin), height, i, scene))
                        {
                            chosen = best;
                            break;
                        }
                        push[best] = -1f;   // tried
                    }
                    if (chosen < 0) chosen = shortest;
                    if (chosen < 0) continue;
                    // The tried pushes were marked -1; recompute the chosen one's distance.
                    float distance = Distance(chosen, min, max, box) + MoveSettings.Skin;
                    feet = Pushed(feet, chosen, distance);
                    moved = true;
                }
                if (!moved) break;
            }
        }

        private static float Distance(int direction, Vector3 min, Vector3 max, in Box box)
        {
            switch (direction)
            {
                case 0: return max.X - box.Min.X;
                case 1: return box.Max.X - min.X;
                case 2: return max.Z - box.Min.Z;
                case 3: return box.Max.Z - min.Z;
                case 4: return box.Max.Y - min.Y;
                default: return max.Y - box.Min.Y;
            }
        }

        private static Vector3 Pushed(Vector3 feet, int direction, float distance)
        {
            switch (direction)
            {
                case 0: feet.X -= distance; break;
                case 1: feet.X += distance; break;
                case 2: feet.Z -= distance; break;
                case 3: feet.Z += distance; break;
                case 4: feet.Y += distance; break;
                default: feet.Y -= distance; break;
            }
            return feet;
        }

        // The character box at `to` is inside some box other than `skip` (by more than Skin), or a slope's slab, that it was
        // not inside at `from` (fix round 1: slabs too, so a push never moves the body into one unchecked).
        private static bool EntersAnother(Vector3 from, Vector3 to, float height, int skip, Scene scene)
        {
            ReadOnlySpan<Box> world = scene.Boxes;
            GetBounds(from, height, out Vector3 fromMin, out Vector3 fromMax);
            GetBounds(to, height, out Vector3 toMin, out Vector3 toMax);
            for (int j = 0; j < world.Length; j++)
            {
                if (j == skip) continue;
                if (OverlapDepth(toMin, toMax, world[j]) > MoveSettings.Skin && OverlapDepth(fromMin, fromMax, world[j]) <= MoveSettings.Skin)
                    return true;
            }
            ReadOnlySpan<Slope> slopes = scene.Slopes;
            for (int j = 0; j < slopes.Length; j++)
            {
                if (InSlab(to, height, slopes[j], out _) && !InSlab(from, height, slopes[j], out _)) return true;
            }
            return false;
        }

        // The smallest overlap of the character box with the box over the three axes (<= 0: apart or touching).
        private static float OverlapDepth(Vector3 min, Vector3 max, in Box box)
        {
            float x = MathF.Min(max.X, box.Max.X) - MathF.Max(min.X, box.Min.X);
            float y = MathF.Min(max.Y, box.Max.Y) - MathF.Max(min.Y, box.Min.Y);
            float z = MathF.Min(max.Z, box.Max.Z) - MathF.Max(min.Z, box.Min.Z);
            return MathF.Min(x, MathF.Min(y, z));
        }

        // Highest floor or box top within GroundProbe of the feet, under the character's footprint. The terrain floor
        // is its height under the feet (Phase 6 D4); a slope's floor is its highest point under the footprint (Phase 13).
        // onTerrain: the ground found is the terrain, not a box top or a slope.
        private static bool TryFindGround(Vector3 feet, Scene scene, out float groundY, out bool onTerrain)
        {
            float floor = scene.Terrain.Height(feet.X, feet.Z);
            bool found = feet.Y <= floor + MoveSettings.GroundProbe;
            groundY = floor;
            onTerrain = found;

            float minX = feet.X - MoveSettings.HalfWidth;
            float maxX = feet.X + MoveSettings.HalfWidth;
            float minZ = feet.Z - MoveSettings.HalfWidth;
            float maxZ = feet.Z + MoveSettings.HalfWidth;
            ReadOnlySpan<Box> world = scene.Boxes;
            for (int i = 0; i < world.Length; i++)
            {
                ref readonly Box box = ref world[i];
                if (box.Max.X <= minX || box.Min.X >= maxX || box.Max.Z <= minZ || box.Min.Z >= maxZ) continue;

                float top = box.Max.Y;
                if (top < feet.Y - MoveSettings.GroundProbe || top > feet.Y + MoveSettings.GroundProbe) continue;
                if (!found || top > groundY)
                {
                    groundY = top;
                    found = true;
                    onTerrain = false;
                }
            }
            ReadOnlySpan<Slope> slopes = scene.Slopes;
            for (int i = 0; i < slopes.Length; i++)
            {
                if (!slopes[i].Range(minX, minZ, maxX, maxZ, out _, out float top, out _)) continue;
                if (top < feet.Y - MoveSettings.GroundProbe || top > feet.Y + MoveSettings.GroundProbe) continue;
                if (!found || top > groundY)
                {
                    groundY = top;
                    found = true;
                    onTerrain = false;
                }
            }
            return found;
        }

        // How far the character may move along one axis (same sign as delta, |result| <= |delta|).
        // Every box that overlaps on the other two axes and lies ahead limits the move to its near
        // face minus Skin, whatever the distance, so a fast fall cannot pass through a thin box.
        // hit: the index of the box that limited the move, -1 = none (the floor, a slope or nothing).
        // Phase 13: down, the floor is the terrain or a slope surface under the feet; up, a slope's slab overhead is a
        // ceiling.
        private static float Sweep(Vector3 feet, float height, int axis, float delta, Scene scene, out int hit)
        {
            hit = -1;
            if (delta == 0f) return 0f;

            GetBounds(feet, height, out Vector3 min, out Vector3 max);
            float limit = MathF.Abs(delta);
            if (axis == AxisY && delta < 0f)
            {
                // The floor under the feet, no Skin.
                float above = min.Y - FloorUnder(feet, feet.Y + MoveSettings.GroundProbe, scene, out _);
                if (above < 0f) above = 0f;
                if (above < limit) limit = above;
            }
            else if (axis == AxisY)
            {
                ReadOnlySpan<Slope> slopes = scene.Slopes;
                for (int i = 0; i < slopes.Length; i++)
                {
                    if (!slopes[i].Range(min.X, min.Z, max.X, max.Z, out _, out float high, out float bottom)) continue;
                    if (high <= feet.Y + MoveSettings.GroundProbe || bottom < max.Y - MoveSettings.Skin) continue;   // a floor, or not overhead
                    float gap = bottom - max.Y - MoveSettings.Skin;
                    if (gap < 0f) gap = 0f;
                    if (gap < limit) limit = gap;
                }
            }

            ReadOnlySpan<Box> world = scene.Boxes;
            for (int i = 0; i < world.Length; i++)
            {
                ref readonly Box box = ref world[i];
                if (!OverlapsOnOtherAxes(min, max, box, axis)) continue;

                float gap;
                if (delta > 0f)
                {
                    float near = Component(box.Min, axis);
                    float front = Component(max, axis);
                    if (near < front - MoveSettings.Skin) continue;   // behind us or already overlapping
                    gap = near - front - MoveSettings.Skin;
                }
                else
                {
                    float near = Component(box.Max, axis);
                    float front = Component(min, axis);
                    if (near > front + MoveSettings.Skin) continue;
                    gap = front - near - MoveSettings.Skin;
                }

                if (gap < 0f) gap = 0f;
                if (gap < limit)
                {
                    limit = gap;
                    hit = i;
                }
            }
            return delta > 0f ? limit : -limit;
        }

        private static bool OverlapsOnOtherAxes(Vector3 min, Vector3 max, in Box box, int axis)
        {
            bool x = axis == AxisX || (min.X < box.Max.X && max.X > box.Min.X);
            bool y = axis == AxisY || (min.Y < box.Max.Y && max.Y > box.Min.Y);
            bool z = axis == AxisZ || (min.Z < box.Max.Z && max.Z > box.Min.Z);
            return x && y && z;
        }

        private static void GetBounds(Vector3 feet, float height, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(feet.X - MoveSettings.HalfWidth, feet.Y, feet.Z - MoveSettings.HalfWidth);
            max = new Vector3(feet.X + MoveSettings.HalfWidth, feet.Y + height, feet.Z + MoveSettings.HalfWidth);
        }

        // System.Numerics.Vector3 has no indexer in netstandard2.1.
        private static float Component(Vector3 v, int axis) => axis == AxisX ? v.X : axis == AxisY ? v.Y : v.Z;

        // The input's camera heading, when it is a number (Step and DropTransport.Ride).
        internal static void ApplyYaw(ref MoveState state, float yaw)
        {
            if (IsFinite(yaw)) state.Yaw = NormalizeYaw(yaw);
        }

        private static float Finite(float value) => IsFinite(value) ? value : 0f;

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static float NormalizeYaw(float yaw)
        {
            yaw %= 360f;
            if (yaw < 0f) yaw += 360f;
            return yaw;
        }
    }
}
