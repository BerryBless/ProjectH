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

            // 기능: 한 걸음이 충돌할 대상을 한데 묶는다(복사 없음, Span 참조만).
            // 입력: boxes - 상자, boxIds - 상자 이름(비어 있으면 Static i), slopes - 경사면, slopeIds - 경사면 이름, terrain - 지형.
            // 출력: 그 Span들을 참조하는 Scene.
            public Scene(ReadOnlySpan<Box> boxes, ReadOnlySpan<ColliderId> boxIds, ReadOnlySpan<Slope> slopes, ReadOnlySpan<ColliderId> slopeIds,
                HeightField terrain)
            {
                Boxes = boxes;
                BoxIds = boxIds;
                Slopes = slopes;
                SlopeIds = slopeIds;
                Terrain = terrain;
            }

            // 기능: 상자 번호를 충돌체 이름으로 바꾼다.
            // 입력: i - Boxes의 번호(-1 = 없음).
            // 출력: BoxIds가 있으면 그 이름, 없으면 Static i. 음수면 None.
            public ColliderId BoxId(int i) =>
                i < 0 ? ColliderId.None : i < BoxIds.Length ? BoxIds[i] : new ColliderId(ColliderKind.Static, (uint)i);

            // 기능: 경사면 번호를 충돌체 이름으로 바꾼다.
            // 입력: i - Slopes의 번호(-1 = 없음).
            // 출력: SlopeIds의 이름. 범위 밖이거나 이름이 없으면 None.
            public ColliderId SlopeId(int i) => i < 0 || i >= SlopeIds.Length ? ColliderId.None : SlopeIds[i];
        }

        // 기능: 상자 목록과 지형으로 한 Tick의 이동을 계산한다(결과 보고 없음. 테스트와 봇이 쓴다).
        // 입력: state - 이어서 계산할 상태, input - 이 Tick의 입력(검증 전), deltaTime - Tick 길이(초), world - 상자(이름은 Static i), terrain - 지형.
        // 출력: 반환값 없음. state가 다음 Tick 상태로 바뀐다.
        public static void Step(ref MoveState state, in InputCommand input, float deltaTime, ReadOnlySpan<Box> world, HeightField terrain)
        {
            Step(ref state, input, deltaTime, world, terrain, out _);
        }

        // 기능: 상자 목록과 지형으로 한 Tick의 이동을 계산하고 결과를 보고한다(경사면 없음).
        // 입력: state - 상태, input - 입력, deltaTime - Tick 길이, world - 상자(이름은 Static i), terrain - 지형, result - 결과.
        // 출력: 반환값 없음. state가 바뀌고 result에 착지 속도·막힘·질주가 담긴다.
        public static void Step(ref MoveState state, in InputCommand input, float deltaTime, ReadOnlySpan<Box> world, HeightField terrain,
            out StepResult result)
        {
            Run(ref state, input, deltaTime, new Scene(world, default, default, default, terrain), out result);
        }

        // 기능: 캐릭터 주변에서 모은 충돌 세계(상자·문·채집 대상·조각·경사면)로 한 Tick의 이동을 계산한다(Phase 13 D3).
        // 입력: state - 상태, input - 입력, deltaTime - Tick 길이, world - 이 걸음 전 위치에서 CollisionWorld.Gather한 세계, terrain - 지형,
        //   result - 결과.
        // 출력: 반환값 없음. state가 바뀌고 result에 착지 속도·막은 충돌체 이름·질주가 담긴다.
        // Phase 13 D3: the world gathered around the character (CollisionWorld.Gather at its position before this step).
        public static void Step(ref MoveState state, in InputCommand input, float deltaTime, CollisionWorld world, HeightField terrain,
            out StepResult result)
        {
            Run(ref state, input, deltaTime, new Scene(world.Boxes, world.BoxIds, world.Slopes, world.SlopeIds, terrain), out result);
        }

        // 기능: 한 Tick의 이동을 모드별 함수로 나눠 계산한다(Phase 12 D1). Phase 14 D4: Downed는 지면 이동을 기어가기로 한다.
        // 입력: state - 이어서 계산할 상태, input - 이 Tick의 입력(검증 전), deltaTime - Tick 길이(초), scene - 충돌 대상.
        // 출력: 반환값 없음. state가 다음 Tick 상태로 바뀌고 result에 착지 속도·막힘·질주가 담긴다.
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
                case MovementMode.Downed:
                    // Phase 14 D4: the ground step at crawl speed. Jump, sprint and crouch do nothing and no vault starts, so
                    // the mode never changes here (only the server leaves it).
                    StepGround(ref state, input.Buttons & ~(InputButtons.Jump | InputButtons.Sprint | InputButtons.Crouch), move, Vector2.Zero,
                        deltaTime, scene, ref result);
                    return;
            }
            // D8: a vault may start on a jump press while moving forward; it faces where the character looks.
            var forward = new Vector2(sin, cos);
            StepGround(ref state, input.Buttons, move, moveY > 0f ? forward : Vector2.Zero, deltaTime, scene, ref result);
        }

        // 기능: Ground·Crouch·Slide(D7)와 Phase 14 Downed의 걷기·질주·점프·낙하(D3 공중 관성)를 계산한다. 리뷰 수정 D1: 수평 이동 직후와
        //   끝에서 위치를 바깥벽 안쪽으로 자른다(바닥 따라가기는 자른 위치에서 한다).
        // 입력: state - 상태, buttons - 이 Tick의 버튼(Downed는 점프·질주·웅크리기를 뺀 값), move - 월드 X/Z 입력 방향(길이 0..1),
        //   vaultDirection - 앞으로 움직일 때 바라보는 방향(Vault 시작 가능), 아니면 0, deltaTime - Tick 길이, scene - 충돌 대상,
        //   result - 결과.
        // 출력: 반환값 없음. state의 위치·속도·모드와 result가 갱신된다.
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
                    : state.Mode == MovementMode.Downed ? MovementTuning.CrawlSpeed
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
            // Review fix D1, review D round 1: back inside the outer walls before the floor is followed (step 8), as StepAir does,
            // so the floor and the drop are taken at the clamped X/Z and not from a lower surface outside.
            ClampToMap(ref position, ref state);

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

            // Review fix D1: a safety net (TryOneAxis in step 8 can still pick a position from before the first clamp).
            ClampToMap(ref position, ref state);
            state.Position = position;
        }

        // 기능: 수평 한 축의 이동을 계산한다: 상자 Sweep(지상에서는 StepUpHeight 안의 상자 위로 올라서며, 몸이 맞는 곳만), 그다음
        //   경사면(D2): 표면으로 오르는 것이 아닌데 경사면 판 안으로 들어가는 이동은 아예 하지 않는다.
        // 입력: position - 발 위치(이동 결과로 갱신), height - 충돌 상자 높이, axis - AxisX 또는 AxisZ, want - 원하는 이동량,
        //   stepUp - 지상이라 올라서기를 허용하는지, scene - 충돌 대상, hit - 막은 충돌체 이름.
        // 출력: 축이 막혔으면 true와 막은 충돌체(상자 또는 경사면), 다 움직였으면 false와 None.
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

        // 기능: 이동 뒤 위치에서 몸이 새로 들어가는 경사면 판 중 표면으로 오르는 것(이동 거리 x MaxSlope 안)이 아닌 첫 경사면을 찾는다.
        //   from에서 이미 들어 있던 판은 막지 않는다(걸어 나갈 수 있다).
        // 입력: from - 이동 전 발 위치, to - 이동 후 발 위치, height - 충돌 상자 높이, moved - 이동 거리(절대값), scene - 충돌 대상.
        // 출력: 막는 경사면의 번호, 없으면 -1.
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

        // 기능: 몸이 경사면의 판 안(발자국 아래 고체 바닥과 표면 사이)에 Skin보다 깊이 들어 있는지 본다.
        // 입력: feet - 발 위치, height - 충돌 상자 높이, slope - 경사면, high - 발자국 아래 표면의 가장 높은 점.
        // 출력: 들어 있으면 true. 발자국이 칸과 겹치지 않으면 false와 high 0.
        // The body at these feet is inside the slope's slab (between its bottom and its surface, under the footprint),
        // by more than Skin. high: the surface's highest point under the footprint.
        private static bool InSlab(Vector3 feet, float height, in Slope slope, out float high)
        {
            if (!slope.Range(feet.X - MoveSettings.HalfWidth, feet.Z - MoveSettings.HalfWidth, feet.X + MoveSettings.HalfWidth,
                    feet.Z + MoveSettings.HalfWidth, out _, out high, out float bottom)) return false;
            return feet.Y < high - MoveSettings.Skin && feet.Y + height > bottom + MoveSettings.Skin;
        }

        // 기능: 발을 그 아래에서 찾은 바닥(경사면 표면)으로 올린다. 몸이 거기 맞을 때만(Skin 넘는 상자도, 판도 없음).
        // 입력: position - 발 위치(결과로 갱신), floor - 올릴 바닥 높이, height - 충돌 상자 높이, start - 맞지 않을 때 되돌릴 위치,
        //   scene - 충돌 대상, overhead - 가로막은 충돌체.
        // 출력: 올렸으면 true와 None. 아니면 false, position = start(이 Tick의 수평 이동 취소), overhead = 가로막은 상자 또는 경사면.
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

        // 기능: 한 축으로만 움직인 후보 위치를 받아, 거기 바닥이 더 높으면 올리고 몸이 맞으면 그 위치를 채택한다(최종 리뷰 B11).
        // 입력: position - 채택 시 갱신할 발 위치, candidate - 한 축만 움직인 후보, start - 이동 전 위치, height - 충돌 상자 높이,
        //   scene - 충돌 대상.
        // 출력: 채택했으면 true와 바뀐 position. 그 축이 움직이지 않았거나 올린 자리에 몸이 안 맞으면 false(position 그대로).
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

        // 기능: 몸이 어떤 상자에 Skin보다 깊이 들어 있거나 경사면 판 안에 있는지 본다.
        // 입력: feet - 발 위치, height - 충돌 상자 높이, scene - 충돌 대상, blocker - 가로막은 충돌체.
        // 출력: 있으면 true와 첫 상자(상자가 없으면 첫 경사면)의 이름. 없으면 false와 None.
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

        // 기능: 발자국 아래에서 upTo 이하인 가장 높은 바닥(발 아래 지형 또는 경사면 표면)을 찾는다. 상자 윗면은 보지 않는다.
        // 입력: feet - 발 위치, upTo - 이 높이보다 높은 경사면 표면은 무시, scene - 충돌 대상, onTerrain - 바닥이 지형인지.
        // 출력: 바닥 높이. 경사면이 지형보다 높고 upTo 이하면 그 표면이고 onTerrain = false.
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
        // 기능: 지상에서 자세를 바꾼다(D7). Ground에서 웅크리기를 누르면 질주 중(Sprint, 지치지 않음, SlideMinStartSpeed 이상)이면 슬라이드
        //   시작(에너지 소모), 아니면 웅크리기. Crouch·Slide에서 떼면 서 있는 상자가 맞는 곳에서 Ground로(못 서는 슬라이드는 Crouch).
        // 입력: state - 이동 상태, position - 발 위치, crouchHeld - 웅크리기 누름, sprintHeld - 질주 누름, scene - 충돌 대상, deltaTime - Tick 길이.
        // 출력: 이 Tick에 슬라이드가 시작됐으면 true(속도·에너지가 바뀐다). 그 외 false(모드만 바뀔 수 있다).
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

        // 기능: 슬라이드의 수평 속도를 갱신한다(D3): 방향 유지, 초당 SlideFriction 감속, 지형 내리막이면 가속, SlideMaxSpeed로 제한.
        // 입력: state - 이동 상태, position - 발 위치(기울기 계산), onTerrain - 바닥이 지형인지(경사로·지붕에서는 가속 없음), terrain - 지형,
        //   deltaTime - Tick 길이.
        // 출력: 반환값 없음. HorizontalVelocity가 바뀌고, SlideMinSpeed 아래면 Mode가 Crouch가 된다.
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

        // 기능: 공중에서 입력으로 수평 속도를 바꾼다(D3). 속도는 원래 속도와 모드의 걷기 속도(웅크리기·기절은 각자의 속도) 중 큰 값을
        //   넘지 않는다(제자리 점프도 조금 움직일 수 있다).
        // 입력: state - 상태, move - 월드 X/Z 입력 방향, deltaTime - Tick 길이.
        // 출력: 반환값 없음. state.HorizontalVelocity가 바뀐다.
        private static void AirControl(ref MoveState state, Vector2 move, float deltaTime)
        {
            if (move.X == 0f && move.Y == 0f) return;
            Vector2 velocity = state.HorizontalVelocity;
            float walk = state.Mode == MovementMode.Crouch ? MovementTuning.CrouchSpeed
                : state.Mode == MovementMode.Downed ? MovementTuning.CrawlSpeed : MoveSettings.WalkSpeed;
            float limit = MathF.Max(velocity.Length(), walk);
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
        // 기능: Freefall·Glide 한 Tick을 계산한다(D6). 점프 입력이나 GlideAutoDeployHeight 안의 지면이면 글라이더를 편다. 자유 낙하는 종단
        //   속도까지 가속, 글라이더는 일정 속도로 하강하며 입력으로 수평 속도를 조향한다. 지면에 닿으면 수평 속도 0으로 Ground(낙하 피해 없음).
        // 입력: state - 이동 상태, buttons - 버튼(Jump만 본다), moveX·moveY - 캐릭터 기준 입력(길이 1 이하), sin·cos - Yaw의 sin·cos,
        //   deltaTime - Tick 길이, scene - 충돌 대상.
        // 출력: 반환값 없음. 위치·속도·Mode가 바뀐다.
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

            ClampToMap(ref position, ref state);

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

        // Review fix D1 (STB-0): the feet never leave the inside of the outer walls (the walls are only 4 m high, so a raised
        // floor, a ramp, a jump or a vault could otherwise carry a player over them).
        private const float MapBound = GameMap.HalfSize - MoveSettings.HalfWidth - MoveSettings.Skin;

        // 기능: 발 위치를 바깥벽 안쪽으로 자르고, 잘린 축의 수평 속도를 0으로 한다(리뷰 수정 D1: 원래 StepAir에만 있던 본문을 지상·Vault도 쓴다).
        //   서버와 Client 예측이 같은 Shared 코드를 써서 같은 결과를 낸다.
        // 입력: position - 고칠 발 위치, state - 수평 속도를 고칠 이동 상태.
        // 출력: 반환값 없음. 바깥이면 position의 X·Z가 경계로, 그 축의 HorizontalVelocity가 0으로 바뀐다.
        private static void ClampToMap(ref Vector3 position, ref MoveState state)
        {
            if (position.X > MapBound || position.X < -MapBound)
            {
                position.X = position.X > 0f ? MapBound : -MapBound;
                state.HorizontalVelocity.X = 0f;
            }
            if (position.Z > MapBound || position.Z < -MapBound)
            {
                position.Z = position.Z > 0f ? MapBound : -MapBound;
                state.HorizontalVelocity.Y = 0f;
            }
        }

        // 기능: 발이 그 아래 지면(지형 또는 발자국 아래 가장 높은 상자 윗면)에서 얼마나 높이 있는지 잰다(D6).
        // 입력: feet - 발 위치, world - 상자, terrain - 지형.
        // 출력: 발 높이 - 지면 높이(m).
        // D6: height of the feet above the ground under them: the terrain or the highest box top below the feet under
        // the character's footprint.
        public static float GroundDistance(Vector3 feet, ReadOnlySpan<Box> world, HeightField terrain) =>
            GroundDistance(feet, new Scene(world, default, default, default, terrain));

        // Phase 13: the same in a gathered world: the terrain, the gathered boxes and slopes. Building pieces are gathered
        // only within CollisionWorld.PieceLevelRadius levels of the feet, so a piece further below is not seen: a freefall
        // opens the glider by the terrain and the map's boxes, and by a piece only once it is that close (glide and
        // freefall landings do no damage, D10).
        // 기능: 모은 충돌 세계(지형, 모은 상자와 경사면)에서 발이 지면에서 얼마나 높이 있는지 잰다(Phase 13). 더 아래의 조각은 모이지 않아 보지 않는다.
        // 입력: feet - 발 위치, world - 발 위치에서 모은 충돌 세계, terrain - 지형.
        // 출력: 발 높이 - 지면 높이(m).
        public static float GroundDistance(Vector3 feet, CollisionWorld world, HeightField terrain) =>
            GroundDistance(feet, new Scene(world.Boxes, world.BoxIds, world.Slopes, world.SlopeIds, terrain));

        // 기능: 발 아래 지면 높이(지형·경사면 표면·발자국 아래의 발 높이 + GroundProbe 이하인 상자 윗면 중 가장 높은 것)와 발의 높이 차를 잰다.
        // 입력: feet - 발 위치, scene - 충돌 대상.
        // 출력: 발 높이 - 지면 높이(m).
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

        // D8: a vault starts only when all five checks pass: (1) an obstacle within VaultReach ahead, standing on the
        // feet's level, (2) its top in hurdle or mantle range, (3) room to stand at the destination, (4) the destination
        // inside no box and above the terrain, (5) review fix D1: the destination inside the outer walls (|x|, |z| <= MapBound). A low obstacle is hurdled only at sprint speed (else this is a normal jump);
        // a hurdle lands HurdleLandingGap past the far side, or on the top when the obstacle is deeper than HurdleMaxDepth
        // or there is no room behind it. A mantle stands MantleInset inside the top's edge. The vault then moves at one
        // constant velocity for its ticks, so the snapshot's velocities and ModeTicks are all a replay needs.
        // Linear passes over the boxes; it runs on every grounded tick that reads the jump level (forward + jump held).
        // The straight path must be clear of every box except the obstacle itself (a wall or door between is a blocker),
        // and a hurdle lands on a surface within VaultBaseTolerance of the feet's level. Phase 13: obstacles are boxes
        // (walls and floors included); a ramp or roof is never one, but a destination on one counts as ground.
        // 기능: 앞의 장애물을 뛰어넘거나(Hurdle) 올라서는(Mantle) Vault를 시작할 수 있는지 다섯 조건으로 검사하고, 되면 일정 속도의 Vault
        //   상태로 바꾼다(D8). 낮은 장애물은 질주 속도에서만 넘는다.
        // 입력: state - 이동 상태, feet - 발 위치, direction - 바라보는 수평 방향(단위 벡터), scene - 충돌 대상, deltaTime - Tick 길이.
        // 출력: 시작했으면 true(Mode Vault, ModeTicks, 수평·수직 속도가 정해진다). 아니면 false(state 그대로).
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
            // Review fix D1: never onto or over the outer wall (a vault does not collide, so it would leave the map).
            if (MathF.Abs(destination.X) > MapBound || MathF.Abs(destination.Z) > MapBound) return false;
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

        // 기능: (x, z)의 발자국 아래에서 발 높이 + VaultBaseTolerance를 넘지 않는 가장 높은 표면(지형·경사면·상자 윗면)을 찾는다.
        // 입력: x·z - 착지 후보의 평면 위치, feetY - 지금 발 높이, scene - 충돌 대상.
        // 출력: 그 표면의 높이.
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

        // 기능: 직선 Vault 경로를 덮는 상자(시작~착지, 서 있는 높이)가 장애물 자신 말고는 어떤 상자와도 겹치지 않는지 본다. 윗면이 발 높이인
        //   상자는 바닥으로 친다.
        // 입력: from - 시작 발 위치, to - 착지 위치, skip - 장애물 상자 번호(무시), world - 상자.
        // 출력: 경로가 비어 있으면 true.
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

        // 기능: 그 자리에 설 수 있는지 본다: 서 있는 상자가 어떤 상자·판과도 겹치지 않고 지형이 발보다 GroundProbe 넘게 높지 않다.
        // 입력: feet - 발 위치, scene - 충돌 대상.
        // 출력: 설 수 있으면 true.
        // The standing box fits at these feet: no box or slab overlaps it and the terrain is not above the feet.
        private static bool IsFreeStand(Vector3 feet, Scene scene) =>
            feet.Y >= scene.Terrain.Height(feet.X, feet.Z) - MoveSettings.GroundProbe && CanStand(feet, scene);

        // 기능: (x, z)에서 단위 방향으로 나가는 2D 광선이 X/Z 사각형에 들어가고 나오는 거리를 구한다.
        // 입력: x·z - 광선 시작, direction - 단위 방향, minX·minZ·maxX·maxZ - 사각형, enter·leave - 들어가는·나오는 거리.
        // 출력: 맞으면 true와 두 거리(시작이 안이면 enter는 음수). 빗나가거나 사각형이 뒤에 있으면 false.
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

        // 기능: 한 축에서 광선이 [min, max] 구간 안에 있는 거리 범위로 [enter, leave]를 좁힌다.
        // 입력: origin - 광선 시작 좌표, direction - 그 축의 방향 성분, min·max - 구간, enter·leave - 좁힐 거리 범위.
        // 출력: 범위가 남으면 true. 방향이 0에 가까우면 enter·leave를 바꾸지 않고 시작이 구간 안일 때만 true.
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

        // 기능: Vault 한 Tick을 진행한다(D8): 시작 때 정한 일정한 속도로 움직이고 충돌은 보지 않는다(경로는 시작 때 확인). 마지막 Tick에 Ground로 선다.
        //   리뷰 수정 D1: 움직인 뒤 바깥벽 안쪽으로 자른다(시작 때 착지점도 막지만, Snapshot에서 받은 Vault 상태도 맵을 벗어나지 않게).
        // 입력: state - 이동 상태, deltaTime - Tick 길이.
        // 출력: 반환값 없음. 위치와 남은 Tick이 바뀌고, 끝나면 Ground가 된다.
        // D8: one tick of a vault: the constant velocity set at its start, no collision (the path was checked then). The
        // last tick ends it standing in Ground mode.
        private static void StepVault(ref MoveState state, float deltaTime)
        {
            if (state.ModeTicks > 0)
            {
                Vector3 position = state.Position + new Vector3(state.HorizontalVelocity.X, state.VelocityY, state.HorizontalVelocity.Y) * deltaTime;
                ClampToMap(ref position, ref state);
                state.Position = position;
                state.ModeTicks--;
            }
            if (state.ModeTicks > 0) return;
            state.Mode = MovementMode.Ground;
            state.VelocityY = 0f;
            state.HorizontalVelocity = Vector2.Zero;
        }

        // 기능: 지상에서 점프를 시작할 수 있는지 본다. Crouch·Slide는 먼저 일어서며 서 있는 상자가 안 맞으면 점프하지 못한다.
        // 입력: state - 이동 상태, position - 발 위치, scene - 충돌 대상.
        // 출력: 점프할 수 있으면 true(Crouch·Slide였으면 Mode가 Ground가 된다. 속도는 호출자가 정한다). 못 서면 false.
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
        // 기능: 이 Tick의 에너지를 갱신한다(D3): 질주면 소모하고 회복 지연을 다시 시작, 아니면 지연을 세고 지연이 끝나면 회복한다.
        //   SprintResumeEnergy까지 회복되면 Exhausted를 푼다.
        // 입력: state - 이동 상태, sprinting - 이 Tick에 질주했는지, deltaTime - Tick 길이.
        // 출력: 반환값 없음. EnergySpent·EnergyDelayTicks·Exhausted가 바뀐다.
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

        // 기능: 에너지를 소모한다(질주 한 Tick, 슬라이드 시작). 바닥나면 Exhausted를 켜고, 회복 지연을 처음부터 다시 센다.
        // 입력: state - 이동 상태, hundredths - 소모량(1/100 단위), deltaTime - Tick 길이(지연 Tick 수 계산).
        // 출력: 반환값 없음. EnergySpent(최대 MaxEnergyHundredths)·Exhausted·EnergyDelayTicks가 바뀐다.
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

        // 기능: 초당 에너지 비율을 Tick당 1/100 단위 정수로 바꾼다.
        // 입력: perSecond - 초당 에너지(0..1 단위), deltaTime - Tick 길이.
        // 출력: Tick당 에너지(1/100 단위, 반올림).
        // Energy hundredths per tick for a per-second rate.
        private static int PerTick(float perSecond, float deltaTime) =>
            (int)MathF.Round(perSecond * MovementTuning.EnergyScale * deltaTime);

        // 기능: 시간을 Tick 수로 바꾼다.
        // 입력: seconds - 시간(초), deltaTime - Tick 길이.
        // 출력: 반올림한 Tick 수(최소 1, 최대 255. NaN이면 1).
        // Whole ticks for a duration (at least 1, at most 255).
        private static byte TicksOf(float seconds, float deltaTime)
        {
            float ticks = MathF.Round(seconds / deltaTime);
            if (!(ticks >= 1f)) return 1;
            return ticks > 255f ? (byte)255 : (byte)ticks;
        }

        // 기능: 모드의 충돌(과 피격) 상자 높이를 돌려준다(D7, D13, Phase 14 D4).
        // 입력: mode - 이동 모드.
        // 출력: 웅크리기·슬라이드는 CrouchHeight, 기절은 DownedHeight, 나머지는 서 있는 높이.
        public static float CollisionHeight(MovementMode mode) =>
            mode == MovementMode.Crouch || mode == MovementMode.Slide ? MovementTuning.CrouchHeight
            : mode == MovementMode.Downed ? MovementTuning.DownedHeight : MoveSettings.Height;

        // 기능: 서 있는 상자가 그 자리에 맞는지 본다(D7: 웅크린 위 0.6 m에 아무것도 없음). 상자만 본다.
        // 입력: feet - 발 위치, world - 상자.
        // 출력: 어떤 상자와도 겹치지 않으면 true.
        // D7: the standing box fits at these feet (nothing in the 0.6 m above a crouch).
        public static bool CanStand(Vector3 feet, ReadOnlySpan<Box> world) => !OverlapsAny(feet, MoveSettings.Height, world);

        // 기능: 서 있는 상자가 그 자리에 맞는지 본다(Phase 13: 상자도, 경사면 판(머리 위 경사로·지붕)도 없음).
        // 입력: feet - 발 위치, scene - 충돌 대상.
        // 출력: 상자·판 모두와 겹치지 않으면 true.
        // Phase 13: neither a box nor a slope's slab (a ramp or roof overhead) is in the standing box.
        private static bool CanStand(Vector3 feet, Scene scene) => Fits(feet, MoveSettings.Height, scene);

        // 기능: 그 높이의 캐릭터 상자가 어떤 상자에도, 어떤 경사면 판에도 들어 있지 않은지 본다.
        // 입력: feet - 발 위치, height - 충돌 상자 높이, scene - 충돌 대상.
        // 출력: 모두 비어 있으면 true.
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

        // 기능: 캐릭터가 지면에 있는지 본다(올라가는 중이 아니고 GroundProbe 안에 지형이나 상자 윗면이 있음).
        // 입력: state - 이동 상태, world - 상자, terrain - 지형.
        // 출력: 지면에 있으면 true.
        public static bool IsGrounded(in MoveState state, ReadOnlySpan<Box> world, HeightField terrain)
        {
            return state.VelocityY <= 0f && TryFindGround(state.Position, new Scene(world, default, default, default, terrain), out _, out _);
        }

        // 기능: 모은 충돌 세계에서 캐릭터가 지면(지형·상자 윗면·경사면 표면)에 있는지 본다.
        // 입력: state - 이동 상태, world - 모은 충돌 세계, terrain - 지형.
        // 출력: 올라가는 중이 아니고 GroundProbe 안에 지면이 있으면 true.
        public static bool IsGrounded(in MoveState state, CollisionWorld world, HeightField terrain)
        {
            return state.VelocityY <= 0f &&
                   TryFindGround(state.Position, new Scene(world.Boxes, world.BoxIds, world.Slopes, world.SlopeIds, terrain), out _, out _);
        }

        // 기능: 서 있는 캐릭터 상자가 어떤 상자와 모든 축에서 0보다 크게 겹치는지 본다(Sweep·지면 스냅 뒤처럼 면이 닿기만 하면 아님).
        // 입력: feet - 발 위치, world - 상자.
        // 출력: 하나라도 겹치면 true.
        // True if the standing character box at these feet overlaps any box by more than zero on every axis.
        // Touching faces (as after a sweep or a ground snap) do not count.
        public static bool OverlapsAny(Vector3 feet, ReadOnlySpan<Box> world) => OverlapsAny(feet, MoveSettings.Height, world);

        // 기능: 그 높이의 캐릭터 상자가 어떤 상자와 모든 축에서 0보다 크게 겹치는지 본다.
        // 입력: feet - 발 위치, height - 충돌 상자 높이, world - 상자.
        // 출력: 하나라도 겹치면 true.
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
        // 기능: 캐릭터 상자가 모은 상자에 Skin보다 깊이 들어 있거나 경사면 판 안에 있는지 본다(테스트와 서버 자체 점검용).
        // 입력: feet - 발 위치, height - 충돌 상자 높이, world - 모은 충돌 세계, piecesOnly - true면 건설 조각 상자만 본다(경사면은 모두 조각).
        // 출력: 끼어 있으면 true.
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
        // 기능: 걸음 시작 때 몸이 들어 있는 지형 아래·경사면 판·상자에서 빠져나온다(Phase 13 D2). 상자는 가장 짧은 밀기 방향으로 나가되
        //   다른 상자나 판으로 들어가는 밀기는 건너뛰고, 아래로는 바닥 위에 머물 때만 민다. 두 번 돈다.
        // 입력: feet - 발 위치(결과로 갱신), height - 충돌 상자 높이, scene - 충돌 대상.
        // 출력: 반환값 없음. feet가 빈 자리로 옮겨진다(이미 비어 있으면 그대로).
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

        // 기능: 캐릭터 상자가 그 방향으로 상자 면을 벗어나는 데 필요한 거리를 다시 계산한다(Depenetrate가 -1로 표시한 값 복구).
        // 입력: direction - 밀기 방향(0 -X, 1 +X, 2 -Z, 3 +Z, 4 +Y, 5 -Y), min·max - 캐릭터 상자, box - 겹친 상자.
        // 출력: 그 면을 벗어나는 거리.
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

        // 기능: 발 위치를 밀기 방향으로 distance만큼 옮긴 위치를 낸다.
        // 입력: feet - 발 위치, direction - 밀기 방향(0 -X, 1 +X, 2 -Z, 3 +Z, 4 +Y, 5 -Y), distance - 거리.
        // 출력: 옮긴 발 위치.
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

        // 기능: to의 캐릭터 상자가 from에서는 들어 있지 않던 다른 상자(Skin 초과)나 경사면 판에 새로 들어가는지 본다.
        // 입력: from - 밀기 전 발 위치, to - 밀기 후 발 위치, height - 충돌 상자 높이, skip - 무시할 상자 번호(-1 = 없음), scene - 충돌 대상.
        // 출력: 새로 들어가는 것이 있으면 true.
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

        // 기능: 캐릭터 상자와 상자의 세 축 겹침 중 가장 작은 값을 낸다.
        // 입력: min·max - 캐릭터 상자, box - 상자.
        // 출력: 가장 작은 겹침 깊이. 0 이하면 떨어져 있거나 닿기만 한다.
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
        // 기능: 발의 GroundProbe 안에 있는 발자국 아래 가장 높은 지면(지형, 상자 윗면, 경사면 표면)을 찾는다.
        // 입력: feet - 발 위치, scene - 충돌 대상, groundY - 찾은 지면 높이, onTerrain - 그 지면이 지형인지.
        // 출력: 지면이 있으면 true와 높이. 없으면 false(groundY는 지형 높이, onTerrain false).
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
        // 기능: 한 축으로 캐릭터가 움직일 수 있는 거리를 잰다. 다른 두 축에서 겹치며 앞에 있는 상자마다 가까운 면 - Skin까지로 제한한다
        //   (거리와 관계없이, 얇은 상자를 뚫지 않게). 아래로는 발 아래 바닥, 위로는 머리 위 경사면 판이 한계.
        // 입력: feet - 발 위치, height - 충돌 상자 높이, axis - AxisX·AxisY·AxisZ, delta - 원하는 이동량, scene - 충돌 대상, hit - 제한한 상자 번호.
        // 출력: 움직일 수 있는 거리(delta와 같은 부호, |결과| <= |delta|). hit는 상자 번호, 바닥·경사면·없음이면 -1.
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

        // 기능: 캐릭터 상자가 이동 축을 뺀 나머지 두 축에서 상자와 겹치는지 본다.
        // 입력: min·max - 캐릭터 상자, box - 상자, axis - 이동 축(검사에서 뺀다).
        // 출력: 나머지 두 축 모두 0보다 크게 겹치면 true.
        private static bool OverlapsOnOtherAxes(Vector3 min, Vector3 max, in Box box, int axis)
        {
            bool x = axis == AxisX || (min.X < box.Max.X && max.X > box.Min.X);
            bool y = axis == AxisY || (min.Y < box.Max.Y && max.Y > box.Min.Y);
            bool z = axis == AxisZ || (min.Z < box.Max.Z && max.Z > box.Min.Z);
            return x && y && z;
        }

        // 기능: 발 위치와 높이로 캐릭터 상자의 최소·최대 모서리를 만든다(HalfWidth 반폭).
        // 입력: feet - 발 위치, height - 충돌 상자 높이, min·max - 결과 모서리.
        // 출력: 반환값 없음. min·max가 채워진다.
        private static void GetBounds(Vector3 feet, float height, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(feet.X - MoveSettings.HalfWidth, feet.Y, feet.Z - MoveSettings.HalfWidth);
            max = new Vector3(feet.X + MoveSettings.HalfWidth, feet.Y + height, feet.Z + MoveSettings.HalfWidth);
        }

        // 기능: 벡터의 한 축 성분을 낸다(netstandard2.1의 Vector3에는 인덱서가 없다).
        // 입력: v - 벡터, axis - AxisX·AxisY·AxisZ.
        // 출력: 그 축의 값.
        // System.Numerics.Vector3 has no indexer in netstandard2.1.
        private static float Component(Vector3 v, int axis) => axis == AxisX ? v.X : axis == AxisY ? v.Y : v.Z;

        // 기능: 입력의 카메라 Yaw를 상태에 적용한다(Step과 DropTransport.Ride가 쓴다).
        // 입력: state - 이동 상태, yaw - 입력 Yaw(도).
        // 출력: 반환값 없음. 유한한 값이면 state.Yaw가 0..360으로 정규화된 값으로 바뀌고, NaN·무한이면 그대로.
        // The input's camera heading, when it is a number (Step and DropTransport.Ride).
        internal static void ApplyYaw(ref MoveState state, float yaw)
        {
            if (IsFinite(yaw)) state.Yaw = NormalizeYaw(yaw);
        }

        // 기능: 유한하지 않은 값을 0으로 바꾼다(검증 전 입력).
        // 입력: value - 값.
        // 출력: 유한하면 그대로, NaN·무한이면 0.
        private static float Finite(float value) => IsFinite(value) ? value : 0f;

        // 기능: 값이 유한한지 본다.
        // 입력: value - 값.
        // 출력: NaN도 무한도 아니면 true.
        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        // 기능: Yaw를 0 이상 360 미만으로 맞춘다.
        // 입력: yaw - 도(유한한 값).
        // 출력: 정규화한 Yaw.
        private static float NormalizeYaw(float yaw)
        {
            yaw %= 360f;
            if (yaw < 0f) yaw += 360f;
            return yaw;
        }
    }
}
