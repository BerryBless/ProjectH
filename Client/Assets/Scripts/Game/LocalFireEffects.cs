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
        public void FireLocal(int shots, Vector3 aimPoint, Vector3 feet, float yaw, float now)
        {
            if (_root == null || shots <= 0) return;   // pool destroyed externally (e.g. scene unload)
            if (shots > MaxShotsPerFrame) shots = MaxShotsPerFrame;
            Vector3 muzzle = MuzzlePosition(feet, yaw);
            for (int i = 0; i < shots; i++) FireOne(aimPoint, muzzle, now);
        }

        // D11: another player's shot as the server resolved it, from its eye to where it stopped.
        public void ShowRemoteShot(Vector3 start, Vector3 end, float now)
        {
            if (_root == null) return;
            ShowTracer(start, end, now);
        }

        // Call once per frame: hides tracers whose time is up.
        public void Tick(float now)
        {
            if (_root == null) return;
            for (int i = 0; i < TracerPoolSize; i++)
            {
                if (_tracers[i].enabled && now >= _tracerHideTime[i]) _tracers[i].enabled = false;
            }
        }

        public void HideAll()
        {
            // Unity null: on scene or play-mode teardown the root (and the pooled children with it) can
            // be destroyed before the owner's OnDestroy calls this; throwing would skip its later Dispose calls.
            if (_root == null) return;
            for (int i = 0; i < TracerPoolSize; i++) _tracers[i].enabled = false;
            for (int i = 0; i < ImpactPoolSize; i++) _impacts[i].SetActive(false);
        }

        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
            if (_material != null) Object.Destroy(_material);
        }

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

        private void ShowTracer(Vector3 from, Vector3 to, float now)
        {
            int slot = _nextTracer.Next();
            LineRenderer line = _tracers[slot];
            line.SetPosition(0, from);
            line.SetPosition(1, to);
            line.enabled = true;
            _tracerHideTime[slot] = now + TracerSeconds;
        }

        private void ShowImpact(Vector3 point, Vector3 normal)
        {
            GameObject impact = _impacts[_nextImpact.Next()];
            impact.transform.SetPositionAndRotation(point + normal * ImpactLift, Quaternion.LookRotation(normal));
            if (!impact.activeSelf) impact.SetActive(true);
        }
    }
}
