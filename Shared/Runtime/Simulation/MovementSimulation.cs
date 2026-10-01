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
        // Index into the world span of the box that stopped a horizontal move this tick (the X sweep's first), -1 = none.
        public int BlockedBy;
        // The box that stopped the Z sweep, -1 = none (equal to BlockedBy when only Z was stopped). A door counts when
        // either sweep met it (D9: a doorway entered a little off-centre meets the jamb on one axis).
        public int BlockedByZ;
        // Sprinting this tick (snapshot flag, D11).
        public bool Sprinting;
        // Sprinting or sliding: a closed door in BlockedBy is shouldered open (D9).
        public bool Charging;
    }

    // The one piece of game logic allowed in Shared (see game-core-rules §4): client prediction and
    // the authoritative server run exactly this code. Pure math, no allocation, no engine types.
    // The character is an axis-aligned box (MoveSettings.HalfWidth, and a height that depends on the mode) with its feet
    // at MoveState.Position; the world is the terrain (Phase 6 D2-D4) plus the boxes passed in. Every terrain slope is
    // walkable (GameMapTests), so the terrain never blocks a horizontal move: it only sets the floor height under the feet.
    // Phase 12 D1: Step branches on MoveState.Mode into a few plain functions; there is no state object or class
    // hierarchy. Every number is in MovementTuning (new) or MoveSettings (Phase 1-6).
    public static class MovementSimulation
    {
        private const float DegToRad = 0.017453292f;
        private const int AxisX = 0;
        private const int AxisY = 1;
        private const int AxisZ = 2;

        public static void Step(ref MoveState state, in InputCommand input, float deltaTime, ReadOnlySpan<Box> world, HeightField terrain)
        {
            Step(ref state, input, deltaTime, world, terrain, out _);
        }

        public static void Step(ref MoveState state, in InputCommand input, float deltaTime, ReadOnlySpan<Box> world, HeightField terrain,
            out StepResult result)
        {
            result = new StepResult { BlockedBy = -1, BlockedByZ = -1 };

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
                    StepAir(ref state, input.Buttons, moveX, moveY, sin, cos, deltaTime, world, terrain);
                    return;
            }
            // D8: a vault may start on a jump press while moving forward; it faces where the character looks.
            var forward = new Vector2(sin, cos);
            StepGround(ref state, input.Buttons, move, moveY > 0f ? forward : Vector2.Zero, deltaTime, world, terrain, ref result);
        }

        // Ground, Crouch and Slide (D7), walking, sprinting, jumping and falling (D3 air momentum).
        // move: the input direction in world X/Z, length 0..1. vaultDirection: the facing direction when the input moves
        // forward (a vault may start), else zero.
        private static void StepGround(ref MoveState state, InputButtons buttons, Vector2 move, Vector2 vaultDirection, float deltaTime,
            ReadOnlySpan<Box> world, HeightField terrain, ref StepResult result)
        {
            bool jump = (buttons & InputButtons.Jump) != 0;
            bool crouchHeld = (buttons & InputButtons.Crouch) != 0;
            bool sprintHeld = (buttons & InputButtons.Sprint) != 0;
            bool moving = move.X != 0f || move.Y != 0f;
            Vector3 position = state.Position;

            // 1) Leave any box we start inside (reconcile snap, rounding): sweeps assume a free start (D4).
            Depenetrate(ref position, CollisionHeight(state.Mode), world, terrain);

            // 2) Stateless ground check (D3). Snapping Y onto the surface keeps a standing player at an
            //    exact height, so grounded/airborne never alternates from rounding. A fall that ends in the snap is a
            //    landing too (D10).
            bool grounded = false;
            bool onTerrain = false;
            if (state.VelocityY <= 0f && TryFindGround(position, world, terrain, out float groundY, out onTerrain))
            {
                grounded = true;
                if (state.VelocityY < 0f) result.LandingSpeed = -state.VelocityY;
                position.Y = groundY;
                state.VelocityY = 0f;
            }

            // 3) Posture (D7), on the ground only: crouch pressed while sprinting starts a slide, otherwise a crouch;
            //    released, the character stands up where the standing box fits.
            bool slideStarted = grounded && UpdatePosture(ref state, position, crouchHeld, sprintHeld, world, deltaTime);

            // 4) Sprint and energy (D3, D7): Shift while moving in Ground mode on the ground, with energy. In the air Sprint
            //    is ignored (no cost, no charge): a sprint jump's speed was decided at the takeoff.
            bool sprinting = sprintHeld && grounded && state.Mode == MovementMode.Ground && !state.Exhausted && moving;
            if (!slideStarted) UpdateEnergy(ref state, sprinting, deltaTime);   // a slide start already paid and reset the delay
            result.Sprinting = sprinting;

            // 5) Horizontal velocity: the input on the ground, the slide's own speed, momentum plus air control in the air.
            if (grounded && state.Mode == MovementMode.Slide)
            {
                SlideVelocity(ref state, position, onTerrain, terrain, deltaTime);
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
                TryStartVault(ref state, position, vaultDirection, world, terrain, deltaTime))
            {
                state.Position = position;
                StepVault(ref state, deltaTime);
                return;
            }
            bool walking = grounded;
            if (grounded && jump && TryStartJump(ref state, position, world))
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
            bool blocked = false;
            float wantX = state.HorizontalVelocity.X * deltaTime;
            float movedX = Sweep(position, height, AxisX, wantX, world, terrain, out int hitX);
            position.X += movedX;
            if (movedX != wantX)
            {
                state.HorizontalVelocity.X = 0f;
                result.BlockedBy = hitX;
                blocked = true;
            }
            float wantZ = state.HorizontalVelocity.Y * deltaTime;
            float movedZ = Sweep(position, height, AxisZ, wantZ, world, terrain, out int hitZ);
            position.Z += movedZ;
            if (movedZ != wantZ)
            {
                state.HorizontalVelocity.Y = 0f;
                if (result.BlockedBy < 0) result.BlockedBy = hitZ;
                result.BlockedByZ = hitZ;
                blocked = true;
            }
            // D7: a slide that runs into something ends in a crouch.
            if (blocked && state.Mode == MovementMode.Slide) state.Mode = MovementMode.Crouch;

            // 8) Phase 6 D4: uphill the terrain lifts the feet; downhill a walking character follows the slope instead
            //    of leaving the ground for a tick. MaxSlope bounds the drop over the distance moved, and the Y sweep
            //    stops on a box top on the way down.
            float floor = terrain.Height(position.X, position.Z);
            if (position.Y < floor)
            {
                position.Y = floor;
            }
            else if (walking)
            {
                float dx = position.X - startX;
                float dz = position.Z - startZ;
                float reach = MathF.Sqrt(dx * dx + dz * dz) * MoveSettings.MaxSlope + MoveSettings.GroundProbe;
                float drop = position.Y - floor;
                if (drop > 0f && drop <= reach) position.Y += Sweep(position, height, AxisY, -drop, world, terrain, out _);
            }

            // 9) Y sweep, the floor being the terrain. Stopped on the way down is a landing (D10).
            float wantY = state.VelocityY * deltaTime;
            float movedY = Sweep(position, height, AxisY, wantY, world, terrain, out _);
            if (movedY != wantY)
            {
                if (wantY < 0f) result.LandingSpeed = -state.VelocityY;
                state.VelocityY = 0f;   // landed or hit a ceiling
            }
            position.Y += movedY;

            state.Position = position;
        }

        // D7: crouch held on the ground: a slide while sprinting (Sprint held, not exhausted, at least SlideMinStartSpeed),
        // otherwise a crouch. So a crouch held through a landing, or pressed at walking speed, never slides. Released: stand
        // up where the standing box fits (a slide that cannot stand becomes a crouch).
        // Starting a slide costs SlideStartEnergyCost and restarts the recovery delay, like a sprint tick; reaching 0 sets
        // Exhausted (and an exhausted character cannot start a slide). Without this cost a Sprint+Crouch hop chain is free:
        // each landing starts a slide before any sprint tick is counted, and the air costs nothing.
        // Returns true when a slide started this tick.
        private static bool UpdatePosture(ref MoveState state, Vector3 position, bool crouchHeld, bool sprintHeld, ReadOnlySpan<Box> world,
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
                    if (!crouchHeld && CanStand(position, world)) state.Mode = MovementMode.Ground;
                    return false;

                case MovementMode.Slide:
                    if (!crouchHeld) state.Mode = CanStand(position, world) ? MovementMode.Ground : MovementMode.Crouch;
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
            ReadOnlySpan<Box> world, HeightField terrain)
        {
            Vector3 position = state.Position;
            Depenetrate(ref position, MoveSettings.Height, world, terrain);

            bool freefall = state.Mode == MovementMode.Freefall;
            if (freefall && ((buttons & InputButtons.Jump) != 0 || GroundDistance(position, world, terrain) <= MovementTuning.GlideAutoDeployHeight))
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

            float wantX = state.HorizontalVelocity.X * deltaTime;
            float movedX = Sweep(position, MoveSettings.Height, AxisX, wantX, world, terrain, out _);
            position.X += movedX;
            if (movedX != wantX) state.HorizontalVelocity.X = 0f;
            float wantZ = state.HorizontalVelocity.Y * deltaTime;
            float movedZ = Sweep(position, MoveSettings.Height, AxisZ, wantZ, world, terrain, out _);
            position.Z += movedZ;
            if (movedZ != wantZ) state.HorizontalVelocity.Y = 0f;

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
            float floor = terrain.Height(position.X, position.Z);
            if (position.Y < floor)
            {
                position.Y = floor;   // came over rising terrain
                landed = true;
            }
            float wantY = state.VelocityY * deltaTime;
            float movedY = Sweep(position, MoveSettings.Height, AxisY, wantY, world, terrain, out _);
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
        public static float GroundDistance(Vector3 feet, ReadOnlySpan<Box> world, HeightField terrain)
        {
            float ground = terrain.Height(feet.X, feet.Z);
            float minX = feet.X - MoveSettings.HalfWidth;
            float maxX = feet.X + MoveSettings.HalfWidth;
            float minZ = feet.Z - MoveSettings.HalfWidth;
            float maxZ = feet.Z + MoveSettings.HalfWidth;
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
        // and a hurdle lands on a surface within VaultBaseTolerance of the feet's level.
        private static bool TryStartVault(ref MoveState state, Vector3 feet, Vector2 direction, ReadOnlySpan<Box> world, HeightField terrain,
            float deltaTime)
        {
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
                float surface = SurfaceUnder(x, z, feet.Y, world, terrain);
                destination = new Vector3(x, surface, z);
                found = MathF.Abs(surface - feet.Y) <= MovementTuning.VaultBaseTolerance && IsFreeStand(destination, world, terrain);
            }
            if (!found)
            {
                float reach = MathF.Min(face + MovementTuning.MantleInset, (face + back) * 0.5f);
                destination = new Vector3(feet.X + direction.X * reach, target.Max.Y, feet.Z + direction.Y * reach);
                found = IsFreeStand(destination, world, terrain);
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
        // terrain or a box top.
        private static float SurfaceUnder(float x, float z, float feetY, ReadOnlySpan<Box> world, HeightField terrain)
        {
            float surface = terrain.Height(x, z);
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

        // The standing box fits at these feet: no box overlaps it and the terrain is not above the feet.
        private static bool IsFreeStand(Vector3 feet, ReadOnlySpan<Box> world, HeightField terrain) =>
            feet.Y >= terrain.Height(feet.X, feet.Z) - MoveSettings.GroundProbe && !OverlapsAny(feet, MoveSettings.Height, world);

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
        private static bool TryStartJump(ref MoveState state, Vector3 position, ReadOnlySpan<Box> world)
        {
            if (state.Mode == MovementMode.Crouch || state.Mode == MovementMode.Slide)
            {
                if (!CanStand(position, world)) return false;
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

        public static bool IsGrounded(in MoveState state, ReadOnlySpan<Box> world, HeightField terrain)
        {
            return state.VelocityY <= 0f && TryFindGround(state.Position, world, terrain, out _, out _);
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

        private static void Depenetrate(ref Vector3 feet, float height, ReadOnlySpan<Box> world, HeightField terrain)
        {
            float floor = terrain.Height(feet.X, feet.Z);
            if (feet.Y < floor) feet.Y = floor;

            for (int i = 0; i < world.Length; i++)
            {
                ref readonly Box box = ref world[i];
                GetBounds(feet, height, out Vector3 min, out Vector3 max);

                float overlapX = MathF.Min(max.X, box.Max.X) - MathF.Max(min.X, box.Min.X);
                float overlapY = MathF.Min(max.Y, box.Max.Y) - MathF.Max(min.Y, box.Min.Y);
                float overlapZ = MathF.Min(max.Z, box.Max.Z) - MathF.Max(min.Z, box.Min.Z);
                if (overlapX <= MoveSettings.Skin || overlapY <= MoveSettings.Skin || overlapZ <= MoveSettings.Skin) continue;

                // Distance to clear each face. The smallest wins; ties keep the first in this fixed
                // order (-X, +X, -Z, +Z, +Y, -Y), so the result is deterministic.
                float best = max.X - box.Min.X;
                int direction = 0;
                float push = box.Max.X - min.X;
                if (push < best) { best = push; direction = 1; }
                push = max.Z - box.Min.Z;
                if (push < best) { best = push; direction = 2; }
                push = box.Max.Z - min.Z;
                if (push < best) { best = push; direction = 3; }
                push = box.Max.Y - min.Y;
                if (push < best) { best = push; direction = 4; }
                push = max.Y - box.Min.Y;
                // Pushing down is only allowed while the feet stay above the floor.
                if (push < best && feet.Y - push - MoveSettings.Skin >= floor) { best = push; direction = 5; }

                float distance = best + MoveSettings.Skin;
                switch (direction)
                {
                    case 0: feet.X -= distance; break;
                    case 1: feet.X += distance; break;
                    case 2: feet.Z -= distance; break;
                    case 3: feet.Z += distance; break;
                    case 4: feet.Y += distance; break;
                    default: feet.Y -= distance; break;
                }
            }
        }

        // Highest floor or box top within GroundProbe of the feet, under the character's footprint. The terrain floor
        // is its height under the feet (Phase 6 D4). onTerrain: the ground found is the terrain, not a box top.
        private static bool TryFindGround(Vector3 feet, ReadOnlySpan<Box> world, HeightField terrain, out float groundY, out bool onTerrain)
        {
            float floor = terrain.Height(feet.X, feet.Z);
            bool found = feet.Y <= floor + MoveSettings.GroundProbe;
            groundY = floor;
            onTerrain = found;

            float minX = feet.X - MoveSettings.HalfWidth;
            float maxX = feet.X + MoveSettings.HalfWidth;
            float minZ = feet.Z - MoveSettings.HalfWidth;
            float maxZ = feet.Z + MoveSettings.HalfWidth;
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
            return found;
        }

        // How far the character may move along one axis (same sign as delta, |result| <= |delta|).
        // Every box that overlaps on the other two axes and lies ahead limits the move to its near
        // face minus Skin, whatever the distance, so a fast fall cannot pass through a thin box.
        // hit: the index of the box that limited the move, -1 = none (the terrain floor or nothing).
        private static float Sweep(Vector3 feet, float height, int axis, float delta, ReadOnlySpan<Box> world, HeightField terrain, out int hit)
        {
            hit = -1;
            if (delta == 0f) return 0f;

            GetBounds(feet, height, out Vector3 min, out Vector3 max);
            float limit = MathF.Abs(delta);
            if (axis == AxisY && delta < 0f)
            {
                // The terrain under the feet is the floor, no Skin.
                float above = min.Y - terrain.Height(feet.X, feet.Z);
                if (above < 0f) above = 0f;
                if (above < limit) limit = above;
            }

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
