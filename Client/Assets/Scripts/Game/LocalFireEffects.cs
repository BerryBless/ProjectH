using UnityEngine;

namespace ProjectH.Client.Game
{
    // Fire presentation: tracers and impact marks, no damage (D12). Own shots are drawn at once when the
    // local WeaponState says the server will fire them; other players' shots come from ShotFired.
    // Everything is created once in the constructor and reused through fixed ring pools, so firing
    // allocates nothing and memory stays constant. Dispose destroys it all.
    public sealed class LocalFireEffects : System.IDisposable
    {
        // Phase 17: 24 so one shotgun blast (8 pellets) plus the shots around it do not recycle each other's tracers.
        public const int TracerPoolSize = 24;
        public const int ImpactPoolSize = 32;
        private const int MaxShotsPerFrame = 3;   // a hitch frame never bursts a pile of effects
        private const float TracerSeconds = 0.05f;
        private const float TracerWidth = 0.02f;
        private const float ImpactSize = 0.1f;
        private const float ImpactLift = 0.01f;   // keeps the mark in front of the surface
        private const float MuzzleHeight = 1.4f;
        // Right and forward offsets of the muzzle from the feet. Their horizontal length
        // (0.24 * sqrt 2 = 0.34 m) is inside the 0.35 m collision half-width at every yaw, and the
        // simulation keeps that box out of every wall, so the muzzle ray normally starts outside a collider
        // (a ray ignores the collider its origin is in). The origin uses RenderPosition, which carries the
        // decaying reconcile offset, so for about 0.1-0.3 s after a misprediction near a wall it can be inside one.
        private const float MuzzleRight = 0.24f;
        private const float MuzzleForward = 0.24f;

        private readonly GameObject _root;
        private readonly Material _material;
        private readonly LineRenderer[] _tracers = new LineRenderer[TracerPoolSize];
        private readonly float[] _tracerHideTime = new float[TracerPoolSize];
        private readonly GameObject[] _impacts = new GameObject[ImpactPoolSize];
        private readonly RingCursor _nextTracer = new RingCursor(TracerPoolSize);
        private readonly RingCursor _nextImpact = new RingCursor(ImpactPoolSize);
        // Phase 17 D3: our own tracers' spread (presentation only; the server's spread uses its own seed). Made once.
        private readonly System.Random _random = new System.Random();

        // 기능: 착탄 표시 풀(충돌체 없는 큐브)과 예광탄 풀(LineRenderer)을 만들고 공유 Material 하나를 붙인다(모두 꺼진 채).
        // 입력: 없음.
        // 출력: 고정 크기 풀이 준비된 효과 객체(Dispose가 해제한다).
        public LocalFireEffects()
        {
            _root = new GameObject("LocalFireEffects");

            for (int i = 0; i < ImpactPoolSize; i++)
            {
                var impact = GameObject.CreatePrimitive(PrimitiveType.Cube);
                impact.name = "Impact";
                // Marks must not block later shots or the camera.
                Object.Destroy(impact.GetComponent<Collider>());
                var renderer = impact.GetComponent<Renderer>();
                // One material for all effects, copied from URP's Lit material (LitMaterial: a primitive's default
                // material is magenta in a build). Never renderer.material (clones per object).
                if (_material == null) _material = new Material(LitMaterial.Source(renderer.sharedMaterial)) { color = new Color(1f, 0.85f, 0.2f) };
                renderer.sharedMaterial = _material;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                impact.transform.SetParent(_root.transform, false);
                impact.transform.localScale = new Vector3(ImpactSize, ImpactSize, ImpactSize);
                impact.SetActive(false);
                _impacts[i] = impact;
            }

            for (int i = 0; i < TracerPoolSize; i++)
            {
                var tracerObject = new GameObject("Tracer");
                tracerObject.transform.SetParent(_root.transform, false);
                var line = tracerObject.AddComponent<LineRenderer>();
                line.positionCount = 2;
                line.useWorldSpace = true;
                line.startWidth = TracerWidth;
                line.endWidth = TracerWidth;
                line.sharedMaterial = _material;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                line.receiveShadows = false;
                line.enabled = false;
                _tracers[i] = line;
            }
        }

        // Own shots this frame (from WeaponState), drawn after the camera moved (LateUpdate). aimPoint is what
        // the crosshair is on; the first thing between the muzzle and it is where the shot lands (Phase 1 D13).
        // 기능: 이번 프레임 내 사격의 예광탄과 착탄 표시를 그린다. Phase 17 D3: 퍼짐이 있으면 발·산탄마다 Client 난수로 원뿔 안 방향을
        //   골라 사거리까지 Raycast한다(표시용, 서버 퍼짐과 다르다). 퍼짐 0·산탄 1이면 지금처럼 조준점으로 쏜다.
        // 입력: shots - 이번 프레임 발 수, pellets - 한 발의 산탄 수(1 이상), spreadDegrees - 원뿔 반각, range - 무기 사거리,
        //   aimPoint - 조준점, feet - 그린 발 위치, yaw - 카메라 Yaw, now - 현재 시각.
        // 출력: 반환값 없음. 예광탄·착탄 풀이 쓰인다(프레임마다 예광탄 풀 크기까지). 할당 없음.
        public void FireLocal(int shots, int pellets, float spreadDegrees, float range, Vector3 aimPoint, Vector3 feet, float yaw, float now)
        {
            if (_root == null || shots <= 0) return;   // pool destroyed externally (e.g. scene unload)
            if (shots > MaxShotsPerFrame) shots = MaxShotsPerFrame;
            if (pellets < 1) pellets = 1;
            Vector3 muzzle = MuzzlePosition(feet, yaw);
            if (pellets == 1 && !(spreadDegrees > 0f))
            {
                for (int i = 0; i < shots; i++) FireOne(aimPoint, muzzle, now);
                return;
            }
            Vector3 toAim = aimPoint - muzzle;
            float distance = toAim.magnitude;
            if (distance < 0.01f) return;
            Vector3 center = toAim / distance;
            // One frame never recycles its own tracers.
            int tracers = Mathf.Min(shots * pellets, TracerPoolSize);
            for (int i = 0; i < tracers; i++)
            {
                Vector3 direction = SpreadCone.Sample(center, spreadDegrees, (float)_random.NextDouble(), (float)_random.NextDouble());
                FireRay(muzzle, direction, Mathf.Max(range, distance), now);
            }
        }

        // 기능: 다른 플레이어의 사격(ShotFired)을 서버가 판정한 대로 예광탄으로 그린다(D11, 착탄 표시 없음).
        // 입력: start - 사수의 눈 위치, end - 탄이 멈춘 지점, now - 현재 시각.
        // 출력: 반환값 없음. 예광탄 풀 하나가 쓰인다.
        public void ShowRemoteShot(Vector3 start, Vector3 end, float now)
        {
            if (_root == null) return;
            ShowTracer(start, end, now);
        }

        // 기능: 프레임마다 표시 시간이 끝난 예광탄을 끈다.
        // 입력: now - 현재 시각.
        // 출력: 반환값 없음. 만료된 LineRenderer가 비활성화된다.
        public void Tick(float now)
        {
            if (_root == null) return;
            for (int i = 0; i < TracerPoolSize; i++)
            {
                if (_tracers[i].enabled && now >= _tracerHideTime[i]) _tracers[i].enabled = false;
            }
        }

        // 기능: 모든 예광탄과 착탄 표시를 숨긴다(풀은 그대로 둔다).
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void HideAll()
        {
            // Unity null: on scene or play-mode teardown the root (and the pooled children with it) can
            // be destroyed before the owner's OnDestroy calls this; throwing would skip its later Dispose calls.
            if (_root == null) return;
            for (int i = 0; i < TracerPoolSize; i++) _tracers[i].enabled = false;
            for (int i = 0; i < ImpactPoolSize; i++) _impacts[i].SetActive(false);
        }

        // 기능: 풀 객체(Root)와 공유 Material을 파괴한다.
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
            if (_material != null) Object.Destroy(_material);
        }

        // 기능: 발 위치와 yaw로 총구 위치를 구한다(오른쪽·앞으로 0.24 m, 높이 1.4 m).
        // 입력: feet - 그린 발 위치, yawDegrees - 바라보는 방향(도, 0 = +Z).
        // 출력: 총구 월드 위치.
        private static Vector3 MuzzlePosition(Vector3 feet, float yawDegrees)
        {
            float yaw = yawDegrees * Mathf.Deg2Rad;
            float sin = Mathf.Sin(yaw);
            float cos = Mathf.Cos(yaw);
            // right = (cos, 0, -sin), forward = (sin, 0, cos), as in MovementSimulation.
            return new Vector3(
                feet.x + cos * MuzzleRight + sin * MuzzleForward,
                feet.y + MuzzleHeight,
                feet.z - sin * MuzzleRight + cos * MuzzleForward);
        }

        // 기능: 퍼짐 없는 한 발을 총구에서 조준점으로 Raycast해 예광탄(맞으면 착탄 표시도)을 그린다(Phase 1 D13).
        // 입력: aimPoint - 조준점, muzzle - 총구 위치, now - 현재 시각.
        // 출력: 반환값 없음. 조준점이 총구에 너무 가까우면 아무것도 그리지 않는다.
        private void FireOne(Vector3 aimPoint, Vector3 muzzle, float now)
        {
            Vector3 toAim = aimPoint - muzzle;
            float distance = toAim.magnitude;
            if (distance < 0.01f) return;
            Vector3 direction = toAim / distance;

            // A little past the aim point so a shot aimed at a surface registers the hit on it. Remote players
            // are on PlayerViewFactory.RemoteHitLayer, so the mask includes that layer.
            if (Physics.Raycast(muzzle, direction, out RaycastHit hit, distance + 0.05f, PlayerViewFactory.AimRaycastMask, QueryTriggerInteraction.Ignore))
            {
                ShowTracer(muzzle, hit.point, now);
                ShowImpact(hit.point, hit.normal);
            }
            else
            {
                ShowTracer(muzzle, aimPoint, now);
            }
        }

        // 기능: 총구에서 한 방향으로 사거리까지 Raycast해 예광탄(과 맞으면 착탄)을 그린다(Phase 17 퍼짐 표시).
        // 입력: muzzle - 총구 위치, direction - 단위 방향, range - 광선 길이, now - 현재 시각.
        // 출력: 반환값 없음.
        private void FireRay(Vector3 muzzle, Vector3 direction, float range, float now)
        {
            if (Physics.Raycast(muzzle, direction, out RaycastHit hit, range, PlayerViewFactory.AimRaycastMask, QueryTriggerInteraction.Ignore))
            {
                ShowTracer(muzzle, hit.point, now);
                ShowImpact(hit.point, hit.normal);
            }
            else
            {
                ShowTracer(muzzle, muzzle + direction * range, now);
            }
        }

        // 기능: 다음 풀 자리의 예광탄을 두 점 사이에 켜고 TracerSeconds 뒤 숨길 시각을 기록한다.
        // 입력: from - 시작점, to - 끝점, now - 현재 시각.
        // 출력: 반환값 없음. 링 커서가 한 칸 돈다.
        private void ShowTracer(Vector3 from, Vector3 to, float now)
        {
            int slot = _nextTracer.Next();
            LineRenderer line = _tracers[slot];
            line.SetPosition(0, from);
            line.SetPosition(1, to);
            line.enabled = true;
            _tracerHideTime[slot] = now + TracerSeconds;
        }

        // 기능: 다음 풀 자리의 착탄 표시를 표면 법선 방향으로 살짝 띄워 놓는다(끌 때까지 남는다).
        // 입력: point - 착탄 지점, normal - 표면 법선.
        // 출력: 반환값 없음. 링 커서가 한 칸 돈다.
        private void ShowImpact(Vector3 point, Vector3 normal)
        {
            GameObject impact = _impacts[_nextImpact.Next()];
            impact.transform.SetPositionAndRotation(point + normal * ImpactLift, Quaternion.LookRotation(normal));
            if (!impact.activeSelf) impact.SetActive(true);
        }
    }
}
