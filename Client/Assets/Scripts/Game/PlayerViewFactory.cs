using ProjectH.Shared.Simulation;
using UnityEngine;

namespace ProjectH.Client.Game
{
    // Capsule views are created on spawn and destroyed on despawn (rare events), so no pooling.
    // Materials are created once per color and shared through sharedMaterial: never use
    // renderer.material, which silently clones a material per object.
    public static class PlayerViewFactory
    {
        // Built-in "Ignore Raycast" layer. Remote views keep a collider there so the crosshair ray can land on a
        // player (the aim point must be on the target, D2). The camera SphereCast uses DefaultRaycastLayers, which
        // excludes this layer, so other players never push the camera; aim and fire rays add it via AimRaycastMask.
        public const int RemoteHitLayer = 2;
        public const int AimRaycastMask = Physics.DefaultRaycastLayers | (1 << RemoteHitLayer);

        private static Material _localMaterial;
        private static Material _remoteMaterial;
        private static Material _deadMaterial;

        public static Transform Create(string name, bool isLocal)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = name;

            if (isLocal)
            {
                // The aim ray starts at the shoulder next to our own capsule; it must never hit it. DestroyImmediate,
                // not Destroy: a deferred destroy would leave the collider for the spawn frame's camera and aim rays.
                Object.DestroyImmediate(go.GetComponent<Collider>());
            }
            else
            {
                // D7: the server's hit box is the movement AABB (0.7 x 1.8 x 0.7), so the aim ray uses the same box
                // instead of the primitive's capsule, which would miss the corners. Feet at the bottom; the view's
                // origin is 1 m above the feet (Pose). DestroyImmediate, not Destroy: a deferred destroy would leave
                // the capsule collider hittable for a frame and returned by GetComponent<Collider>().
                Object.DestroyImmediate(go.GetComponent<CapsuleCollider>());
                var box = go.AddComponent<BoxCollider>();
                box.size = new Vector3(2f * MoveSettings.HalfWidth, MoveSettings.Height, 2f * MoveSettings.HalfWidth);
                box.center = new Vector3(0f, MoveSettings.Height * 0.5f - 1f, 0f);
                go.layer = RemoteHitLayer;
            }

            var renderer = go.GetComponent<Renderer>();
            EnsureMaterials(LitMaterial.Source(renderer.sharedMaterial));
            renderer.sharedMaterial = isLocal ? _localMaterial : _remoteMaterial;
            return go.transform;
        }

        // D13: dead players are grey and lying down. A dead remote player's collider is off, so shots and the
        // crosshair go through the body. collider may be null (the local view has none).
        public static void SetAlive(Renderer renderer, Collider collider, bool isLocal, bool alive)
        {
            if (renderer != null) renderer.sharedMaterial = alive ? (isLocal ? _localMaterial : _remoteMaterial) : _deadMaterial;
            if (collider != null) collider.enabled = alive;
        }

        // Upright: view origin (capsule mesh centre) 1 m above the feet. Dead: on its side along the facing direction.
        public static void Pose(Vector3 feet, float yaw, bool alive, out Vector3 position, out Quaternion rotation)
        {
            if (alive)
            {
                position = feet + Vector3.up;
                rotation = Quaternion.Euler(0f, yaw, 0f);
            }
            else
            {
                position = feet + new Vector3(0f, 0.5f, 0f);
                rotation = Quaternion.Euler(90f, yaw, 0f);
            }
        }

        // Called by GameClient.OnDestroy: the cached materials live exactly as long as the client.
        public static void ReleaseMaterials()
        {
            if (_localMaterial != null) Object.Destroy(_localMaterial);
            if (_remoteMaterial != null) Object.Destroy(_remoteMaterial);
            if (_deadMaterial != null) Object.Destroy(_deadMaterial);
            _localMaterial = null;
            _remoteMaterial = null;
            _deadMaterial = null;
        }

        private static void EnsureMaterials(Material template)
        {
            // Explicit == null (not ??=): Unity's null check also catches destroyed materials.
            if (_localMaterial == null) _localMaterial = Tinted(template, new Color(0.2f, 0.6f, 1f));
            if (_remoteMaterial == null) _remoteMaterial = Tinted(template, new Color(1f, 0.45f, 0.2f));
            if (_deadMaterial == null) _deadMaterial = Tinted(template, new Color(0.45f, 0.45f, 0.45f));
        }

        // Copies the primitive's default material, so the shader is guaranteed to be in the build.
        private static Material Tinted(Material template, Color color)
        {
            return new Material(template) { color = color };
        }
    }
}
