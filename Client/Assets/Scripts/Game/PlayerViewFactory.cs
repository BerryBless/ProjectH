using ProjectH.Shared.Simulation;
using UnityEngine;

namespace ProjectH.Client.Game
{
    // One character on screen (Phase 12 D14). The root sits at the feet, never rotates and carries the remote hit box,
    // so the box stays axis-aligned like the server's AABB (D7) at the mode's height (D13). The capsule child shows the
    // pose (PlayerPose: height, lean, lying down), a flat wing child the glider. Transforms change only when the pose
    // does, besides the per-frame position and facing. Created on spawn and destroyed on despawn.
    public sealed class PlayerView
    {
        private const float WingLift = 0.3f;

        private readonly bool _isLocal;
        private readonly Transform _body;
        private readonly Renderer _bodyRenderer;
        private readonly GameObject _wings;
        private readonly Transform _wingsTransform;
        private readonly BoxCollider _collider;   // null for the local player
        private bool _alive = true;
        private bool _hidden;
        private PlayerPose _pose;
        private bool _hasPose;

        internal PlayerView(Transform root, Transform body, GameObject wings, BoxCollider collider, bool isLocal)
        {
            Root = root;
            _body = body;
            _bodyRenderer = body.GetComponent<Renderer>();
            _wings = wings;
            _wingsTransform = wings.transform;
            _collider = collider;
            _isLocal = isLocal;
        }

        public Transform Root { get; }

        // D13 (Phase 3): dead players are grey and lying down, and a dead remote player's hit box is off.
        public void SetAlive(bool alive)
        {
            if (alive == _alive) return;
            _alive = alive;
            _bodyRenderer.sharedMaterial = PlayerViewFactory.BodyMaterial(_isLocal, alive);
            _hasPose = false;   // the pose depends on it
        }

        // Every frame: where the feet are drawn, the facing, and the mode's pose.
        public void Place(Vector3 feet, float yaw, MovementMode mode, bool sprinting)
        {
            Root.position = feet;
            PlayerPose pose = PlayerPose.For(mode, sprinting, _alive);
            if (!_hasPose || pose.BodyHeight != _pose.BodyHeight || pose.Prone != _pose.Prone || pose.Wings != _pose.Wings ||
                pose.Hidden != _pose.Hidden || pose.HitHeight != _pose.HitHeight)
            {
                ApplyShape(pose);
            }
            _pose = pose;
            _hasPose = true;
            float pitch = pose.Prone ? 90f : pose.Lean;
            _body.localRotation = Quaternion.Euler(pitch, yaw, 0f);
            if (pose.Wings) _wingsTransform.localRotation = Quaternion.Euler(0f, yaw, 0f);
        }

        private void ApplyShape(in PlayerPose pose)
        {
            float half = pose.BodyHeight * 0.5f;
            // Lying down, the capsule's middle is half its radius (0.5 m) above the feet.
            _body.localPosition = new Vector3(0f, pose.Prone ? 0.5f : half, 0f);
            _body.localScale = new Vector3(1f, pose.Prone ? 1f : half, 1f);
            if (_hidden != pose.Hidden)
            {
                _hidden = pose.Hidden;
                _bodyRenderer.enabled = !pose.Hidden;
            }
            if (_wings.activeSelf != pose.Wings) _wings.SetActive(pose.Wings);
            _wingsTransform.localPosition = new Vector3(0f, pose.BodyHeight + WingLift, 0f);
            if (_collider != null)
            {
                _collider.size = new Vector3(2f * MoveSettings.HalfWidth, pose.HitHeight, 2f * MoveSettings.HalfWidth);
                _collider.center = new Vector3(0f, pose.HitHeight * 0.5f, 0f);
                _collider.enabled = _alive && !pose.Hidden;
            }
        }

        public void Destroy()
        {
            if (Root != null) Object.Destroy(Root.gameObject);
        }
    }

    // Views are created on spawn and destroyed on despawn (rare events), so no pooling.
    // Materials are created once per color and shared through sharedMaterial: never use
    // renderer.material, which silently clones a material per object.
    public static class PlayerViewFactory
    {
        // Built-in "Ignore Raycast" layer. Remote views keep a collider there so the crosshair ray can land on a
        // player (the aim point must be on the target, D2). The camera SphereCast uses DefaultRaycastLayers, which
        // excludes this layer, so other players never push the camera; aim and fire rays add it via AimRaycastMask.
        public const int RemoteHitLayer = 2;
        public const int AimRaycastMask = Physics.DefaultRaycastLayers | (1 << RemoteHitLayer);

        private static readonly Vector3 WingSize = new Vector3(3f, 0.08f, 1f);

        private static Material _localMaterial;
        private static Material _remoteMaterial;
        private static Material _deadMaterial;
        private static Material _wingMaterial;

        public static PlayerView Create(string name, bool isLocal)
        {
            var root = new GameObject(name);
            BoxCollider collider = null;
            if (!isLocal)
            {
                // D7: the server's hit box is the movement AABB, so the aim ray uses the same box (on the root, which never
                // rotates) instead of the primitive's capsule, which would miss the corners. The local view has none: the
                // aim ray starts next to it and must never hit it.
                collider = root.AddComponent<BoxCollider>();
                root.layer = RemoteHitLayer;
            }

            // DestroyImmediate, not Destroy: a deferred destroy would leave the primitives' colliders for this frame's
            // camera and aim rays.
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            Object.DestroyImmediate(body.GetComponent<Collider>());
            body.transform.SetParent(root.transform, false);
            var bodyRenderer = body.GetComponent<Renderer>();
            EnsureMaterials(LitMaterial.Source(bodyRenderer.sharedMaterial));
            bodyRenderer.sharedMaterial = isLocal ? _localMaterial : _remoteMaterial;

            var wings = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wings.name = "Glider";
            Object.DestroyImmediate(wings.GetComponent<Collider>());
            wings.transform.SetParent(root.transform, false);
            wings.transform.localScale = WingSize;
            wings.GetComponent<Renderer>().sharedMaterial = _wingMaterial;
            wings.SetActive(false);

            var view = new PlayerView(root.transform, body.transform, wings, collider, isLocal);
            view.Place(root.transform.position, 0f, MovementMode.Ground, false);
            return view;
        }

        internal static Material BodyMaterial(bool isLocal, bool alive) =>
            alive ? (isLocal ? _localMaterial : _remoteMaterial) : _deadMaterial;

        // Called by GameClient.OnDestroy: the cached materials live exactly as long as the client.
        public static void ReleaseMaterials()
        {
            if (_localMaterial != null) Object.Destroy(_localMaterial);
            if (_remoteMaterial != null) Object.Destroy(_remoteMaterial);
            if (_deadMaterial != null) Object.Destroy(_deadMaterial);
            if (_wingMaterial != null) Object.Destroy(_wingMaterial);
            _localMaterial = null;
            _remoteMaterial = null;
            _deadMaterial = null;
            _wingMaterial = null;
        }

        private static void EnsureMaterials(Material template)
        {
            // Explicit == null (not ??=): Unity's null check also catches destroyed materials.
            if (_localMaterial == null) _localMaterial = Tinted(template, new Color(0.2f, 0.6f, 1f));
            if (_remoteMaterial == null) _remoteMaterial = Tinted(template, new Color(1f, 0.45f, 0.2f));
            if (_deadMaterial == null) _deadMaterial = Tinted(template, new Color(0.45f, 0.45f, 0.45f));
            if (_wingMaterial == null) _wingMaterial = Tinted(template, new Color(0.95f, 0.85f, 0.25f));
        }

        // Copies the Lit material (LitMaterial), so the shader is guaranteed to be in the build.
        private static Material Tinted(Material template, Color color)
        {
            return new Material(template) { color = color };
        }
    }
}
