using BepInEx.Logging;

namespace ClassForge.Plugin
{
    /// <summary>
    /// LATER-UNIT WIRING POINT — not installed in M1.
    ///
    /// Target (game-patch-surface-notes.md §8): <c>AssetLoader.GetImage(object pIconID, eTextureAtlas pAtlas,
    /// out Color pColor)</c> — the overload <c>CharacterCustomizationViewHelper.RenderClassList</c> actually
    /// calls for class icons (L1575) — plus <c>AssetLoader.GetRender(...)</c> for portraits. SPEC.md §10
    /// places the real icon/portrait fallback patch in M2, alongside the class-select UI polish; M1 only
    /// binds the <c>[UI] EnableIconFallback</c> knob (see ClassForgePlugin) so its config key is stable when
    /// the real patch lands.
    ///
    /// When implemented, this should be a Prefix returning false (skip vanilla) when
    /// <c>ClassForgePlugin.CurrentMergePlan.Icons</c> / <c>.Portraits</c> contains the requested id, loading
    /// the pack's PNG from disk into a Texture2D — mirroring EOR's proven hook shape (SPEC.md §6).
    /// </summary>
    public static class AssetPatches
    {
        public static void LogWiringPointOnly(ManualLogSource log)
        {
            log.LogInfo("[ClassForge] AssetLoader.GetImage/GetRender icon+portrait fallback: wiring point only " +
                        "in M1 (knob [UI] EnableIconFallback bound; the Harmony patch itself ships in a later unit).");
        }

        // Later unit, sketch (uncomment/implement once this project has verified overload resolution against
        // the real FTK2.dll):
        //
        // public static bool GetImage_Prefix(object pIconID, UnityEngine.eTextureAtlas pAtlas,
        //     out UnityEngine.Color pColor, ref UnityEngine.Texture2D __result)
        // {
        //     pColor = default;
        //     if (!ClassForgePlugin.Enabled.Value || !ClassForgePlugin.EnableIconFallback.Value) return true;
        //     var plan = ClassForgePlugin.CurrentMergePlan;
        //     if (plan == null) return true;
        //     var id = pIconID as string;
        //     if (id == null || !plan.Icons.TryGetValue(id, out var path)) return true;
        //     __result = LoadTextureFromDisk(path);
        //     return false; // skip vanilla lookup
        // }
    }
}
