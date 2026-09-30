using UnityEngine;

namespace ProjectH.Client.Game
{
    // Client-only fire presentation (D11, D13, D14): tracers and impact marks, no packet, no damage.
    // Everything is created once in the constructor and reused through fixed ring pools, so firing
    // 10 times a second allocates nothing and memory stays constant. Dispose destroys it all.
    public sealed class LocalFireEffects : System.IDisposable
    {
        public const int TracerPoolSize = 16;
        public const int ImpactPoolSize = 32;
        private const float ShotsPerSecond = 10f;
        private const int MaxShotsPerFrame = 3;
        private const float Range = 200f;
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

        private readonly FireRateAccumulator _rate = new FireRateAccumulator(ShotsPerSecond, MaxShotsPerFrame);
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
                // One material for all effects, copied from the primitive's default so its shader is
                // guaranteed to be in the build. Never renderer.material (clones per object).
                if (_material == null) _material = new Material(renderer.sharedMaterial) { color = new Color(1f, 0.85f, 0.2f) };
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

        // Call once per frame after the camera has moved (LateUpdate).
        public void Tick(float deltaTime, bool triggerHeld, Ray aimRay, Vector3 feet, float yaw, float now)
        {
            if (_root == null) return;   // pool destroyed externally (e.g. scene unload): nothing to draw
            int shots = _rate.Consume(deltaTime, triggerHeld);
            if (shots > 0)
            {
                Vector3 muzzle = MuzzlePosition(feet, yaw);
                for (int i = 0; i < shots; i++) FireOne(aimRay, muzzle, now);
            }

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

        private void FireOne(Ray aimRay, Vector3 muzzle, float now)
        {
            // 1) Screen-centre ray: what the crosshair is on.
            Vector3 aimPoint = Physics.Raycast(aimRay, out RaycastHit aimHit, Range, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                ? aimHit.point
                : aimRay.GetPoint(Range);

            // 2) Muzzle -> aim point: the first thing in between is where the shot lands (D13).
            Vector3 toAim = aimPoint - muzzle;
            float distance = toAim.magnitude;
            if (distance < 0.01f) return;
            Vector3 direction = toAim / distance;

            // A little past the aim point so a shot aimed at a surface registers the hit on it.
            if (Physics.Raycast(muzzle, direction, out RaycastHit hit, distance + 0.05f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
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
