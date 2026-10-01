using UnityEngine;

namespace ProjectH.Client.Game
{
    // The base that every material made at runtime is copied from. A primitive's default material cannot be that base:
    // URP's default material is an Editor-only resource, so in a player build CreatePrimitive gets the built-in
    // Default-Material (Standard shader), which URP draws magenta. Resources/ProjectHLit.mat is a copy of URP's Lit
    // material; Resources content is always in the build, and with it the Lit shader. The loaded material is an asset:
    // callers copy it (new Material) and never destroy or change it.
    public static class LitMaterial
    {
        private const string ResourcePath = "ProjectHLit";
        private static Material s_source;
        private static bool s_warned;

        // fallback: the primitive's own material, used only when the resource is missing (it still renders in the Editor).
        public static Material Source(Material fallback)
        {
            if (s_source == null) s_source = Resources.Load<Material>(ResourcePath);
            if (s_source != null) return s_source;
            if (!s_warned)
            {
                s_warned = true;
                Debug.LogWarning("LitMaterial: Resources/" + ResourcePath + " is missing; using the primitive's material (magenta in a URP build).");
            }
            return fallback;
        }
    }
}
