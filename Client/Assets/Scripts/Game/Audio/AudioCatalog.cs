using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Game.Audio
{
    // Phase 18 D1-D3: every sound the client can make. The value is an index into AudioCatalog's table, the clip bank and the
    // QA play counts, so new kinds go before Count. Grouped by the request's priority (§79): gunfire 1, footsteps 2,
    // building and harvesting 3, our own body 4, loot and world interaction 5, the zone 6, UI 7.
    public enum SoundKind : byte
    {
        GunAR = 0,
        GunSMG,
        GunShotgun,
        GunSniper,
        GunPistol,
        RocketLaunch,
        GrenadeThrow,
        Explosion,

        StepGroundWalk,
        StepGroundSprint,
        StepGroundCrouch,
        StepStoneWalk,
        StepStoneSprint,
        StepStoneCrouch,
        StepWoodWalk,
        StepWoodSprint,
        StepWoodCrouch,
        StepMetalWalk,
        StepMetalSprint,
        StepMetalCrouch,
        Slide,
        DownedDrag,

        BuildPlace,
        BuildEdit,
        BuildDamage,
        BuildDestroy,
        BuildCollapse,
        BuildRefused,
        HarvestSwing,
        HarvestHit,
        HarvestWeakPoint,
        HarvestDestroyed,

        HealthHit,
        ShieldHit,
        ShieldBreak,
        HitMarker,
        KillConfirm,
        SelfDowned,
        SelfDied,
        Reload,

        Pickup,
        ContainerOpen,
        SupplyDropLand,
        Reboot,
        DoorOpen,
        DoorClose,

        ZoneWarning,
        ZoneDamage,

        UiClick,
        MapOpen,
        MapClose,

        // Phase 19 D11: added after the Phase 18 kinds so the earlier values (clip seeds, QA indices) stay the same. Getting in
        // and out reuse the door sounds' recipes; a wreck plays Explosion.
        VehicleEnter,
        VehicleExit,
        VehicleImpact,

        Count,
    }

    // What the footsteps can sound like (D5: four surfaces only, request §80).
    public enum SurfaceMaterial : byte
    {
        Ground = 0,
        Stone = 1,
        Wood = 2,
        Metal = 3,
    }

    // A footstep's gait (D5). Slide and Drag do not depend on the surface.
    public enum FootstepGait : byte
    {
        None = 0,
        Walk = 1,
        Sprint = 2,
        Crouch = 3,
        Slide = 4,
        Drag = 5,
    }

    // One row of the catalog: how a kind is mixed. Priority 1 is the most important (request §79). MaxDistance is where a 3D
    // sound fades to silence (Unity Linear rolloff) and beyond which the mixer drops it (§82). MinInterval: a second play of
    // the same kind from the same source sooner than this is a duplicate (D2). Variants: how many clips the bank makes.
    public readonly struct SoundInfo
    {
        public readonly byte Priority;
        public readonly float MaxDistance;
        public readonly float MinInterval;
        public readonly float Volume;
        public readonly byte Variants;

        // 기능: 표의 한 줄을 만든다.
        // 입력: priority - 1(가장 중요)–7, maxDistance - 3D 최대 거리(m), minInterval - 같은 소스·종류의 최소 간격(초),
        //   volume - 0–1 음량, variants - 클립 변형 수(1–3).
        // 출력: 표의 한 줄.
        public SoundInfo(byte priority, float maxDistance, float minInterval, float volume, byte variants)
        {
            Priority = priority;
            MaxDistance = maxDistance;
            MinInterval = minInterval;
            Volume = volume;
            Variants = variants;
        }
    }

    // Phase 18 D1, D3: the one table of sound kinds (priority, audible distance, duplicate interval, volume, variants) and the
    // pure mappings from game facts to kinds. Replacing the synthesized clips with real assets later changes only the bank;
    // tuning a number changes only this table. Pure (no UnityEngine): the EditMode tests read it.
    public static class AudioCatalog
    {
        // D10: one master volume, no menu slider yet.
        public const float MasterVolume = 0.8f;
        // D3 distances (m).
        public const float GunshotDistance = 120f;
        public const float LongGunshotDistance = 150f;   // sniper, rocket, explosion, supply drop landing
        public const float WalkDistance = 25f;
        public const float SprintDistance = 30f;
        public const float CrouchDistance = 12f;
        public const float BuildDistance = 40f;
        public const float HarvestDistance = 30f;
        public const float LootDistance = 15f;
        public const float DoorDistance = 20f;
        // Phase 19 D11: a vehicle hitting something is heard as far as building.
        public const float VehicleImpactDistance = 40f;
        // D5: footsteps are computed only for players within this distance of the listener (the farthest footstep, a sprint).
        public const float FootstepComputeDistance = SprintDistance;
        // Own footsteps are 2D and quieter than another player's at the same distance (they would mask the others otherwise).
        public const float OwnFootstepVolumeScale = 0.5f;
        // The longest MinInterval in the table: the mixer forgets a (kind, source) pair after this.
        public const float LongestInterval = 1f;

        private static readonly SoundInfo[] s_table = BuildTable();

        // 기능: 종류의 표 한 줄을 돌려준다.
        // 입력: kind - 소리 종류(Count 미만).
        // 출력: 우선순위·거리·간격·음량·변형 수. 범위 밖이면 가장 낮은 우선순위의 무음 줄.
        public static SoundInfo Info(SoundKind kind) =>
            (int)kind < s_table.Length ? s_table[(int)kind] : new SoundInfo(7, 0f, LongestInterval, 0f, 1);

        // 기능: 무기 정보로 총성 종류를 고른다(D4: 무기 id 대신 카탈로그 성질로 고른다. 무기 표가 바뀌어도 그대로 맞는다).
        // 입력: weapon - 카탈로그의 무기.
        // 출력: 로켓 = RocketLaunch, 산탄(Shells 또는 2발 이상) = GunShotgun, Heavy = GunSniper, Medium = GunAR,
        //   Light 자동 = GunSMG, Light 반자동 = GunPistol, 그 밖 = GunAR.
        public static SoundKind GunshotFor(in WeaponInfo weapon)
        {
            if (weapon.Projectile == ProjectileKind.Rocket) return SoundKind.RocketLaunch;
            if (weapon.AmmoType == AmmoType.Shells || weapon.Pellets > 1) return SoundKind.GunShotgun;
            switch (weapon.AmmoType)
            {
                case AmmoType.Heavy: return SoundKind.GunSniper;
                case AmmoType.Light: return weapon.Automatic ? SoundKind.GunSMG : SoundKind.GunPistol;
                default: return SoundKind.GunAR;
            }
        }

        // 기능: ShotFired의 무기 id로 총성 종류를 고른다(D4).
        // 입력: catalog - 무기 카탈로그(null 가능), weaponId - ShotFired.WeaponId.
        // 출력: 카탈로그에 있으면 GunshotFor 결과, 없거나 카탈로그가 없으면 기본 GunAR. 할당 없음.
        public static SoundKind GunshotForId(WeaponInfo[] catalog, byte weaponId)
        {
            if (catalog != null)
            {
                for (int i = 0; i < catalog.Length; i++)
                {
                    if (catalog[i].WeaponId == weaponId) return GunshotFor(catalog[i]);
                }
            }
            return SoundKind.GunAR;
        }

        // 기능: 걸음 종류와 발밑 재질로 발소리 종류를 고른다(D5).
        // 입력: gait - 걸음(None이 아니어야 의미가 있다), surface - 발밑 재질.
        // 출력: 소리 종류. Slide·Drag는 재질과 관계없다. None이면 StepGroundWalk(호출자가 걸러야 한다).
        public static SoundKind FootstepFor(FootstepGait gait, SurfaceMaterial surface)
        {
            if (gait == FootstepGait.Slide) return SoundKind.Slide;
            if (gait == FootstepGait.Drag) return SoundKind.DownedDrag;
            int g = gait == FootstepGait.Sprint ? 1 : gait == FootstepGait.Crouch ? 2 : 0;
            return (SoundKind)((int)SoundKind.StepGroundWalk + (int)surface * 3 + g);
        }

        // 기능: 건설 재질을 발밑 재질로 바꾼다(D5: 조각 = 그 기록의 재질).
        // 입력: material - 조각 재질.
        // 출력: Wood·Stone·Metal.
        public static SurfaceMaterial SurfaceOf(BuildMaterialType material)
        {
            switch (material)
            {
                case BuildMaterialType.Stone: return SurfaceMaterial.Stone;
                case BuildMaterialType.Metal: return SurfaceMaterial.Metal;
                default: return SurfaceMaterial.Wood;
            }
        }

        // 기능: 표를 만든다(D3의 우선순위·거리, D2의 중복 간격, Phase 19 차량 타기·내리기·충돌). 시작 때 한 번.
        // 입력: 없음.
        // 출력: SoundKind.Count칸 배열.
        private static SoundInfo[] BuildTable()
        {
            var t = new SoundInfo[(int)SoundKind.Count];
            // Gunfire (1). The SMG fires every 66.7 ms, so the gun interval stays below that: only a repeat inside one
            // frame is a duplicate.
            t[(int)SoundKind.GunAR] = new SoundInfo(1, GunshotDistance, 0.04f, 0.9f, 3);
            t[(int)SoundKind.GunSMG] = new SoundInfo(1, GunshotDistance, 0.04f, 0.8f, 3);
            t[(int)SoundKind.GunShotgun] = new SoundInfo(1, GunshotDistance, 0.04f, 1f, 3);
            t[(int)SoundKind.GunSniper] = new SoundInfo(1, LongGunshotDistance, 0.04f, 1f, 2);
            t[(int)SoundKind.GunPistol] = new SoundInfo(1, GunshotDistance, 0.04f, 0.8f, 3);
            t[(int)SoundKind.RocketLaunch] = new SoundInfo(1, LongGunshotDistance, 0.2f, 1f, 2);
            t[(int)SoundKind.GrenadeThrow] = new SoundInfo(1, BuildDistance, 0.2f, 0.7f, 2);
            t[(int)SoundKind.Explosion] = new SoundInfo(1, LongGunshotDistance, 0.05f, 1f, 3);
            // Footsteps (2): walk 25 m, sprint 30 m, crouch 12 m and quiet; slide and the downed drag.
            for (int s = 0; s < 4; s++)
            {
                int first = (int)SoundKind.StepGroundWalk + s * 3;
                t[first] = new SoundInfo(2, WalkDistance, 0.15f, 0.55f, 3);
                t[first + 1] = new SoundInfo(2, SprintDistance, 0.15f, 0.7f, 3);
                t[first + 2] = new SoundInfo(2, CrouchDistance, 0.15f, 0.3f, 3);
            }
            t[(int)SoundKind.Slide] = new SoundInfo(2, WalkDistance, 0.5f, 0.6f, 2);
            t[(int)SoundKind.DownedDrag] = new SoundInfo(2, CrouchDistance, 0.5f, 0.45f, 2);
            // Building and harvesting (3), 40 m; a piece's health drop at most every 0.15 s (D6).
            t[(int)SoundKind.BuildPlace] = new SoundInfo(3, BuildDistance, 0.05f, 0.7f, 3);
            t[(int)SoundKind.BuildEdit] = new SoundInfo(3, BuildDistance, 0.05f, 0.6f, 2);
            t[(int)SoundKind.BuildDamage] = new SoundInfo(3, BuildDistance, 0.15f, 0.6f, 3);
            t[(int)SoundKind.BuildDestroy] = new SoundInfo(3, BuildDistance, 0.05f, 0.85f, 2);
            t[(int)SoundKind.BuildCollapse] = new SoundInfo(3, BuildDistance, 0.1f, 0.9f, 2);
            t[(int)SoundKind.BuildRefused] = new SoundInfo(3, 0f, 0.25f, 0.5f, 1);
            t[(int)SoundKind.HarvestSwing] = new SoundInfo(3, HarvestDistance, 0.1f, 0.45f, 2);
            t[(int)SoundKind.HarvestHit] = new SoundInfo(3, HarvestDistance, 0.05f, 0.7f, 3);
            t[(int)SoundKind.HarvestWeakPoint] = new SoundInfo(3, HarvestDistance, 0.05f, 0.8f, 2);
            t[(int)SoundKind.HarvestDestroyed] = new SoundInfo(3, HarvestDistance, 0.1f, 0.85f, 2);
            // Our own body (4), 2D.
            t[(int)SoundKind.HealthHit] = new SoundInfo(4, 0f, 0.04f, 0.8f, 2);
            t[(int)SoundKind.ShieldHit] = new SoundInfo(4, 0f, 0.04f, 0.75f, 2);
            t[(int)SoundKind.ShieldBreak] = new SoundInfo(4, 0f, 0.2f, 0.95f, 1);
            t[(int)SoundKind.HitMarker] = new SoundInfo(4, 0f, 0.04f, 0.6f, 1);
            t[(int)SoundKind.KillConfirm] = new SoundInfo(4, 0f, 0.1f, 0.8f, 1);
            t[(int)SoundKind.SelfDowned] = new SoundInfo(4, 0f, 1f, 0.9f, 1);
            t[(int)SoundKind.SelfDied] = new SoundInfo(4, 0f, 1f, 0.9f, 1);
            // A reconcile can flicker WeaponState.Reloading: a long interval keeps it to one sound.
            t[(int)SoundKind.Reload] = new SoundInfo(4, 0f, 0.5f, 0.6f, 1);
            // Loot and world interaction (5): loot 15 m, a supply drop landing 150 m (D9), doors 20 m.
            t[(int)SoundKind.Pickup] = new SoundInfo(5, LootDistance, 0.05f, 0.6f, 2);
            t[(int)SoundKind.ContainerOpen] = new SoundInfo(5, LootDistance, 0.2f, 0.7f, 2);
            t[(int)SoundKind.SupplyDropLand] = new SoundInfo(5, LongGunshotDistance, 0.5f, 1f, 1);
            t[(int)SoundKind.Reboot] = new SoundInfo(5, LootDistance, 0.5f, 0.8f, 1);
            t[(int)SoundKind.DoorOpen] = new SoundInfo(5, DoorDistance, 0.15f, 0.7f, 2);
            t[(int)SoundKind.DoorClose] = new SoundInfo(5, DoorDistance, 0.15f, 0.7f, 2);
            // The zone (6), 2D.
            t[(int)SoundKind.ZoneWarning] = new SoundInfo(6, 0f, 1f, 0.8f, 1);
            t[(int)SoundKind.ZoneDamage] = new SoundInfo(6, 0f, 0.5f, 0.55f, 1);
            // UI (7), 2D.
            t[(int)SoundKind.UiClick] = new SoundInfo(7, 0f, 0.05f, 0.5f, 1);
            t[(int)SoundKind.MapOpen] = new SoundInfo(7, 0f, 0.1f, 0.5f, 1);
            t[(int)SoundKind.MapClose] = new SoundInfo(7, 0f, 0.1f, 0.5f, 1);
            // Phase 19 D11: getting in and out like a door (5, 20 m); an impact like building damage (3, 40 m), at most one per
            // vehicle every 0.3 s.
            t[(int)SoundKind.VehicleEnter] = new SoundInfo(5, DoorDistance, 0.15f, 0.7f, 1);
            t[(int)SoundKind.VehicleExit] = new SoundInfo(5, DoorDistance, 0.15f, 0.7f, 1);
            t[(int)SoundKind.VehicleImpact] = new SoundInfo(3, VehicleImpactDistance, 0.3f, 0.9f, 2);
            return t;
        }
    }
}
