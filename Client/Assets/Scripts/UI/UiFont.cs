using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ProjectH.Client.UI
{
    // Phase 11 D2: the one font of every UI text, HUDs included. Made once at run time from an OS font that has Hangul
    // (no font asset in the repository: license and size). The installed candidates are passed together, so the first
    // one renders and the rest are fallbacks for glyphs it lacks. With none installed it falls back to Unity's built-in
    // LegacyRuntime.ttf, where Korean shows as boxes; the log line says which font was chosen.
    // Owned by GameClient: it calls Release in OnDestroy, after its HUDs are gone.
    public static class UiFont
    {
        private static readonly string[] Candidates =
        {
            "Malgun Gothic", "맑은 고딕", "Apple SD Gothic Neo", "Noto Sans CJK KR", "Noto Sans KR", "NanumGothic",
        };

        // The dynamic font's base size; every Text sets its own fontSize.
        private const int BaseSize = 16;

        private static Font s_font;
        private static bool s_ownsFont;

        public static Font Get()
        {
            // Unity null: a released font is made again.
            if (s_font != null) return s_font;

            string[] installed = Font.GetOSInstalledFontNames();
            var found = new List<string>(Candidates.Length);
            foreach (string candidate in Candidates)
            {
                foreach (string name in installed)
                {
                    if (!string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase)) continue;
                    found.Add(candidate);
                    break;
                }
            }

            if (found.Count > 0)
            {
                s_font = Font.CreateDynamicFontFromOSFont(found.ToArray(), BaseSize);
                s_ownsFont = true;
                Debug.Log("UI font: " + string.Join(", ", found) + " (OS font)");
            }
            else
            {
                s_font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                s_ownsFont = false;
                Debug.LogWarning("UI font: no Hangul OS font found (" + string.Join(", ", Candidates) +
                                 "); using the built-in LegacyRuntime.ttf, so Korean text may show as boxes.");
            }
            return s_font;
        }

        // Destroys the font Get made from the OS (the built-in one belongs to Unity).
        public static void Release()
        {
            if (s_ownsFont && s_font != null) Object.Destroy(s_font);
            s_font = null;
            s_ownsFont = false;
        }
    }
}
