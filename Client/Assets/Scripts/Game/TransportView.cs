using ProjectH.Shared.Simulation;
using UnityEngine;
using UnityEngine.Rendering;

namespace ProjectH.Client.Game
{
    // Phase 12 D14: the drop transport, one long box flying the route (DropRoute.PositionAt at the render tick, the same
    // formula the server places riders with). Shown from the route's start tick to its end tick. Built once; Tick only
    // moves it. No collider: nothing collides with it. Dispose destroys the object and its material.
    public sealed class TransportView : System.IDisposable
    {
        private static readonly Vector3 Size = new Vector3(4f, 2f, 14f);
        private const float Lift = 2.5f;   // drawn above the riders' feet, so a rider's camera sits under its belly

        private readonly GameObject _root;
        private readonly Material _material;
        private bool _hasRoute;
        private DropRoute _route;
        private bool _visible;

        public TransportView()
        {
            _root = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _root.name = "DropTransport";
            Object.DestroyImmediate(_root.GetComponent<Collider>());
            _root.transform.localScale = Size;
            var renderer = _root.GetComponent<Renderer>();
            _material = new Material(LitMaterial.Source(renderer.sharedMaterial)) { color = new Color(0.25f, 0.28f, 0.32f) };
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            _root.SetActive(false);
        }

        public void SetRoute(in DropRoute route)
        {
            _route = route;
            _hasRoute = true;
            var direction = new Vector3(route.EndX - route.StartX, 0f, route.EndZ - route.StartZ);
            if (direction.sqrMagnitude > 1e-6f) _root.transform.rotation = Quaternion.LookRotation(direction);
        }

        public void Clear()
        {
            _hasRoute = false;
            SetVisible(false);
        }

        // renderTick: the server tick the world is drawn at this frame.
        public void Tick(double renderTick)
        {
            if (_root == null) return;
            bool flying = _hasRoute && renderTick >= _route.StartTick && renderTick <= _route.EndTick;
            SetVisible(flying);
            if (!flying) return;
            System.Numerics.Vector3 at = _route.PositionAt(renderTick);
            _root.transform.position = new Vector3(at.X, at.Y + Lift, at.Z);
        }

        private void SetVisible(bool visible)
        {
            if (_root == null || visible == _visible) return;
            _visible = visible;
            _root.SetActive(visible);
        }

        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
            if (_material != null) Object.Destroy(_material);
        }
    }
}
