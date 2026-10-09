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

        // 기능: 런타임 Material의 복사 원본(Resources/ProjectHLit)을 돌려준다(한 번 읽어 보관; 없으면 경고를 한 번만 내고 fallback을 쓴다).
        // 입력: fallback - Primitive 자체의 Material(리소스가 없을 때만 쓰며 Editor에서는 그대로 그려진다).
        // 출력: 에셋 Material(복사해서 쓰고 파괴·변경하지 않는다), 리소스가 없으면 fallback.
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
