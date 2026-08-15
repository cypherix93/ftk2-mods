using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ClassForge.Plugin
{
    /// <summary>
    /// Icon + portrait fallback for pack content (SPEC.md §6, §10).
    ///
    /// <para><b>Verified targets</b> (AssetLoader.cs):</para>
    /// <code>
    /// // L333 — the overload RenderClassList actually calls for class icons (L1575:
    /// //        AssetLoader.GetImage(_pClassConfig, eTextureAtlas.Class, out var pColor))
    /// public static Texture2D GetImage(object pIconID, eTextureAtlas pAtlas, out Color pColor)
    /// // L404
    /// public static Texture2D GetRender(string pKey, bool pAllowPrerender = true, bool pIsDialogue = false)
    /// </code>
    ///
    /// <para><b>Why this overload of <c>GetImage</c>.</b> Its whole body is
    /// <c>return GetImage($"{pAtlas}_{pIconID}".ToUpper(), out pColor);</c> — patching here sits <i>above</i>
    /// the key mangling, so the prefix sees the raw content id (<c>CF_BATTLEMASTER</c>) rather than
    /// <c>CLASS_CF_BATTLEMASTER</c>, and can branch on <c>pAtlas</c> as a clean enum. The
    /// <c>GetImage(object, eTextureAtlas)</c> overload delegates to this one, so it is covered for free.</para>
    ///
    /// <para><b>Not a crash fix, a polish feature.</b> The vanilla miss path returns <c>null</c> silently
    /// (no throw, no log) and a null <c>backgroundImage</c> just renders nothing. So this patch fails open in
    /// every direction: unknown id, missing file, unreadable PNG and any exception all
    /// <c>return true</c> and let the game resolve normally.</para>
    ///
    /// <para><b>Prefix returns <c>false</c> only on a successful override</b>, which is presentation-only —
    /// it substitutes a texture, it does not touch game state, so it is not the "per-client suppression"
    /// SPEC-DELTA-v1.1 §5.2 invariant 6 forbids (that rule is about authoritative combat state).</para>
    ///
    /// <para><b>Two mandatory details, both learned from the reference implementation:</b> the
    /// <c>out Color</c> parameter must be declared <c>typeof(Color).MakeByRefType()</c> in the
    /// <c>AccessTools.Method</c> type array or the target silently resolves to null and the patch never
    /// applies; and <c>pColor</c> <b>must</b> be assigned when returning <c>false</c>, because the caller
    /// writes it straight into <c>style.unityBackgroundImageTintColor</c>.</para>
    /// </summary>
    public static class AssetPatches
    {
        // Positive cache (id -> texture) and negative cache, both essential: these methods are called once
        // per row per rebind, so an uncached miss would re-probe the disk continuously.
        private static readonly Dictionary<string, Texture2D> Cache = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        private static readonly HashSet<string> Missing = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Diagnostic (2026-08-15, trait-icon report): pack-shaped ids that missed
        /// <c>plan.Icons</c>, logged once each so the log shows exactly which ids fall through to the
        /// vanilla atlas. Vanilla TRAIT_* ids landing here once is expected and informative, not a bug.</summary>
        private static readonly HashSet<string> LoggedPlanMisses = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Dropped whenever the merge plan is replaced, so a hot-reload picks up edited PNGs.</summary>
        internal static void InvalidateCache()
        {
            Cache.Clear();
            Missing.Clear();
            LoggedPlanMisses.Clear();
        }

        /// <summary>
        /// Prefix for <c>GetImage(object pIconID, eTextureAtlas pAtlas, out Color pColor)</c>.
        /// Serves <c>MergePlan.Icons</c> entries by content id.
        /// </summary>
        public static bool GetImage_Prefix(object pIconID, eTextureAtlas pAtlas, ref Color pColor, ref Texture2D __result)
        {
            try
            {
                if (!ClassForgePlugin.FeaturesActive) return true;
                if (!ClassForgePlugin.EnableIconFallback.Value) return true;
                if (pIconID == null) return true;

                var plan = ClassForgePlugin.CurrentMergePlan;
                if (plan == null || plan.Icons.Count == 0) return true;

                string id = pIconID as string;
                if (string.IsNullOrEmpty(id)) return true;   // enum ids (eSkills etc.) are never pack content

                string path;
                if (!plan.Icons.TryGetValue(id, out path))
                {
                    if ((id.StartsWith("TRAIT_", StringComparison.Ordinal) || id.StartsWith("CF_", StringComparison.Ordinal))
                        && LoggedPlanMisses.Add(id))
                        ClassForgePlugin.Log.LogWarning(
                            "[ClassForge] Icon lookup for '" + id + "' (atlas " + pAtlas + ") not in pack icons — " +
                            "vanilla path used. Logged once per id.");
                    return true;
                }

                var texture = LoadTexture("icon:" + id, path);
                if (texture == null) return true;

                __result = texture;
                pColor = Color.white;   // MUST be set — the caller tints with it
                return false;
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogError("[ClassForge] Icon fallback failed (fail-safe, vanilla lookup runs): " + ex);
                return true;
            }
        }

        /// <summary>
        /// Prefix for <c>GetRender(string pKey, bool pAllowPrerender, bool pIsDialogue)</c>.
        /// Serves <c>MergePlan.Portraits</c> entries by content id.
        /// <para>Returning <c>false</c> deliberately skips the vanilla
        /// <c>MemoryManagementHelper.IncrementRefCount</c> bookkeeping — correct, because the returned
        /// <c>Texture2D</c> is owned by ClassForge's cache, not by the game's prerender pool.</para>
        /// </summary>
        public static bool GetRender_Prefix(string pKey, bool pAllowPrerender, bool pIsDialogue, ref Texture2D __result)
        {
            try
            {
                if (!ClassForgePlugin.FeaturesActive) return true;
                if (!ClassForgePlugin.EnableIconFallback.Value) return true;
                if (string.IsNullOrEmpty(pKey)) return true;

                var plan = ClassForgePlugin.CurrentMergePlan;
                if (plan == null || plan.Portraits.Count == 0) return true;

                string path;
                if (!plan.Portraits.TryGetValue(pKey, out path)) return true;

                var texture = LoadTexture("portrait:" + pKey, path);
                if (texture == null) return true;

                __result = texture;
                return false;
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogError("[ClassForge] Portrait fallback failed (fail-safe, vanilla lookup runs): " + ex);
                return true;
            }
        }

        /// <summary>
        /// Loads a PNG off disk into a <c>Texture2D</c>. <c>2, 2</c> are placeholder dimensions —
        /// <c>ImageConversion.LoadImage</c> resizes to the file's real size.
        /// <c>HideFlags.HideAndDontSave</c> keeps the texture alive across scene unloads (the reference
        /// implementation omits this and leaks a dangling reference on scene change).
        /// </summary>
        private static Texture2D LoadTexture(string cacheKey, string path)
        {
            Texture2D cached;
            if (Cache.TryGetValue(cacheKey, out cached)) return cached;
            if (Missing.Contains(cacheKey)) return null;

            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    Missing.Add(cacheKey);
                    ClassForgePlugin.Log.LogWarning(
                        "[ClassForge] Asset '" + cacheKey + "' maps to '" + (path ?? "(null)") +
                        "' which does not exist — falling back to the vanilla lookup. Logged once per id.");
                    return null;
                }

                var bytes = File.ReadAllBytes(path);
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!texture.LoadImage(bytes, false))
                {
                    UnityEngine.Object.Destroy(texture);
                    Missing.Add(cacheKey);
                    ClassForgePlugin.Log.LogWarning(
                        "[ClassForge] '" + path + "' is not a decodable PNG/JPG — falling back to the vanilla lookup. Logged once per id.");
                    return null;
                }

                texture.name = "CF_" + cacheKey;
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Bilinear;
                texture.hideFlags = HideFlags.HideAndDontSave;

                Cache[cacheKey] = texture;
                if (ClassForgePlugin.VerboseLogging.Value)
                    ClassForgePlugin.Log.LogDebug("[ClassForge] Loaded '" + cacheKey + "' from " + path + ".");
                return texture;
            }
            catch (Exception ex)
            {
                Missing.Add(cacheKey);
                ClassForgePlugin.Log.LogWarning("[ClassForge] Could not load '" + path + "': " + ex.Message + " — vanilla lookup used.");
                return null;
            }
        }
    }
}
