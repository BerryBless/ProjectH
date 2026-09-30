using UnityEngine;

namespace ProjectH.Client.Game
{
    // Capsule views are created on spawn and destroyed on despawn (rare events), so no pooling.
    // Materials are created once per color and shared through sharedMaterial: never use
    // renderer.material, which silently clones a material per object.
    public static class PlayerViewFactory
    {
        private static Material _localMaterial;
        private static Material _remoteMaterial;

        public static Transform Create(string name, bool isLocal)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = name;
            Object.Destroy(go.GetComponent<Collider>());   // no client physics in this phase

            var renderer = go.GetComponent<Renderer>();
            renderer.sharedMaterial = GetMaterial(renderer.sharedMaterial, isLocal);
            return go.transform;
        }

        // Called by GameClient.OnDestroy: the cached materials live exactly as long as the client.
        public static void ReleaseMaterials()
        {
            if (_localMaterial != null) Object.Destroy(_localMaterial);
            if (_remoteMaterial != null) Object.Destroy(_remoteMaterial);
            _localMaterial = null;
            _remoteMaterial = null;
        }

        private static Material GetMaterial(Material template, bool isLocal)
        {
            // Explicit == null (not ??=): Unity's null check also catches destroyed materials.
            if (isLocal)
            {
                if (_localMaterial == null) _localMaterial = Tinted(template, new Color(0.2f, 0.6f, 1f));
                return _localMaterial;
            }
            if (_remoteMaterial == null) _remoteMaterial = Tinted(template, new Color(1f, 0.45f, 0.2f));
            return _remoteMaterial;
        }

        // Copies the primitive's default material, so the shader is guaranteed to be in the build.
        private static Material Tinted(Material template, Color color)
        {
            var material = new Material(template) { color = color };
            return material;
        }
    }
}
