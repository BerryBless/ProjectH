using UnityEngine;

namespace ProjectH.Client.Game
{
    // Fire presentation: tracers and impact marks, no damage (D12). Own shots are drawn at once when the
    // local WeaponState says the server will fire them; other players' shots come from ShotFired.
    // Everything is created once in the constructor and reused through fixed ring pools, so firing
    // allocates nothing and memory stays constant. Dispose destroys it all.
    public sealed class LocalFireEffects : System.IDisposable
    {
        public const int TracerPoolSize = 16;
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

        // 기능: 탄흔 Pool과 탄도선 Pool을 미리 만들고 공유 Material 하나를 입힌다.
        // 입력: 없음.
        // 출력: 모든 탄흔·탄도선이 꺼진 상태로 생성된 LocalFireEffects.
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

        // 기능: 이번 프레임의 내 사격을 총구에서 조준 지점으로 그린다. 한 프레임 최대 MaxShotsPerFrame발까지만 그린다.
        // 입력: shots - 이번 프레임 발사 수, aimPoint - 조준선이 가리키는 지점, feet - 렌더링 발 위치, yaw - 캐릭터 yaw(도), now - 현재 로컬 시간(초).
        // 출력: 반환값 없음. 탄도선과 탄흔이 Pool에서 표시된다.
        // Own shots this frame (from WeaponState), drawn after the camera moved (LateUpdate). aimPoint is what
        // the crosshair is on; the first thing between the muzzle and it is where the shot lands (Phase 1 D13).
        public void FireLocal(int shots, Vector3 aimPoint, Vector3 feet, float yaw, float now)
        {
            if (_root == null || shots <= 0) return;   // pool destroyed externally (e.g. scene unload)
            if (shots > MaxShotsPerFrame) shots = MaxShotsPerFrame;
            Vector3 muzzle = MuzzlePosition(feet, yaw);
            for (int i = 0; i < shots; i++) FireOne(aimPoint, muzzle, now);
        }

        // 기능: 다른 플레이어의 사격(ShotFired)을 서버가 판정한 시작점부터 끝점까지 탄도선으로 그린다.
        // 입력: start - 서버 기준 사격 시작점(눈), end - 탄이 멈춘 지점, now - 현재 로컬 시간(초).
        // 출력: 반환값 없음. Pool의 탄도선 하나가 표시된다.
        // D11: another player's shot as the server resolved it, from its eye to where it stopped.
        public void ShowRemoteShot(Vector3 start, Vector3 end, float now)
        {
            if (_root == null) return;
            ShowTracer(start, end, now);
        }

        // 기능: 매 프레임 표시 시간이 지난 탄도선을 끈다.
        // 입력: now - 현재 로컬 시간(초).
        // 출력: 반환값 없음. 만료된 탄도선이 비활성화된다.
        // Call once per frame: hides tracers whose time is up.
        public void Tick(float now)
        {
            if (_root == null) return;
            for (int i = 0; i < TracerPoolSize; i++)
            {
                if (_tracers[i].enabled && now >= _tracerHideTime[i]) _tracers[i].enabled = false;
            }
        }

        // 기능: 모든 탄도선과 탄흔을 숨긴다.
        // 입력: 없음.
        // 출력: 반환값 없음. Pool의 모든 효과가 꺼진다.
        public void HideAll()
        {
            // Unity null: on scene or play-mode teardown the root (and the pooled children with it) can
            // be destroyed before the owner's OnDestroy calls this; throwing would skip its later Dispose calls.
            if (_root == null) return;
            for (int i = 0; i < TracerPoolSize; i++) _tracers[i].enabled = false;
            for (int i = 0; i < ImpactPoolSize; i++) _impacts[i].SetActive(false);
        }

        // 기능: 효과 Root와 동적으로 만든 공유 Material을 파괴한다.
        // 입력: 없음.
        // 출력: 반환값 없음. Pool 오브젝트와 Material이 파괴된다.
        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
            if (_material != null) Object.Destroy(_material);
        }

        // 기능: 발 위치와 yaw로 총구 위치(오른쪽·앞쪽 오프셋, 높이 MuzzleHeight)를 계산한다.
        // 입력: feet - 발 위치, yawDegrees - 캐릭터 yaw(도).
        // 출력: 월드 기준 총구 위치.
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

        // 기능: 총구에서 조준 지점으로 Raycast해 맞은 곳까지 탄도선과 탄흔을 그리고, 맞은 것이 없으면 조준 지점까지 탄도선만 그린다.
        // 입력: aimPoint - 조준 지점, muzzle - 총구 위치, now - 현재 로컬 시간(초).
        // 출력: 반환값 없음. 탄도선(과 탄흔)이 표시된다. 거리가 너무 짧으면 아무것도 하지 않는다.
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

        // 기능: Ring Pool의 다음 탄도선을 두 점 사이에 켜고 숨길 시각을 기록한다.
        // 입력: from - 시작점, to - 끝점, now - 현재 로컬 시간(초).
        // 출력: 반환값 없음. 탄도선 하나가 표시된다.
        private void ShowTracer(Vector3 from, Vector3 to, float now)
        {
            int slot = _nextTracer.Next();
            LineRenderer line = _tracers[slot];
            line.SetPosition(0, from);
            line.SetPosition(1, to);
            line.enabled = true;
            _tracerHideTime[slot] = now + TracerSeconds;
        }

        // 기능: Ring Pool의 다음 탄흔을 맞은 표면 바로 앞에 법선 방향으로 놓고 켠다.
        // 입력: point - 맞은 지점, normal - 표면 법선.
        // 출력: 반환값 없음. 탄흔 하나가 배치·표시된다.
        private void ShowImpact(Vector3 point, Vector3 normal)
        {
            GameObject impact = _impacts[_nextImpact.Next()];
            impact.transform.SetPositionAndRotation(point + normal * ImpactLift, Quaternion.LookRotation(normal));
            if (!impact.activeSelf) impact.SetActive(true);
        }
    }
}
