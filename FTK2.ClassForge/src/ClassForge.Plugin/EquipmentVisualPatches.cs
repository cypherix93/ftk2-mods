using System;
using System.Collections.Generic;

namespace ClassForge.Plugin
{
    /// <summary>
    /// Equipment 3D-visual fallback for pack items (task #8 crash fix).
    ///
    /// <para><b>The crash.</b> <c>CharacterVisualHelper.VisualReEquip</c> resolves each equipped Thing's
    /// visual via <c>EquipmentVisualHelper.GetITMEquipmentPrefab(item.ConfigName, …)</c> and then
    /// dereferences the result unguarded (<c>visualEquipment.GetEquipmentType()</c>, decompile L1049).
    /// A pack item has no <c>dEquipmentPrefab</c> record, so the lookup returns null and character
    /// creation NREs — bare model, input-locked party screen (Player.log 2026-08-15).</para>
    ///
    /// <para><b>The fix, mirroring the reference implementation</b> (EOR62
    /// <c>EquipmentVisualHelper_GetITMEquipmentPrefab_Prefix</c>, L18473): prefix the STRING overload of
    /// <c>GetITMEquipmentPrefab</c> — the terminal one; the <c>VisualEquipment</c> overload funnels into
    /// it — and rewrite <c>ref pItemConfigName</c> to the donor id from the merged pack
    /// <c>visualfallbacks.json</c> (<c>MergePlan.VisualFallbacks</c>). The donor's record then resolves
    /// normally, so the pack weapon renders as its visual base (e.g. the Cattle Whip renders the militia
    /// whip model).</para>
    ///
    /// <para><b>The safety net.</b> A finalizer on <c>VisualReEquip</c> logs and SUPPRESSES any exception.
    /// This deliberately deviates from the reference implementation (which rethrows): an invisible weapon
    /// beats an input-locked freeze, and until task #9 ships fallbacks for the ~500 equippable Armory
    /// items, any of them being equipped would otherwise reproduce this exact crash. The finalizer is
    /// registered UNCONDITIONALLY (not gated on <c>FeaturesActive</c> or any knob) — a crash guard must
    /// not be toggleable off into a freeze.</para>
    ///
    /// <para><b>MP posture:</b> presentation-only — which 3D prefab is attached never touches simulation
    /// state, so there is no parity feature line. The fallback DATA rides the pack directory and is
    /// therefore already covered by the parity dataHash.</para>
    /// </summary>
    internal static class EquipmentVisualPatches
    {
        /// <summary>One log per distinct suppressed-exception message, so a rebind loop cannot spam.</summary>
        private static readonly HashSet<string> LoggedSuppressed = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Prefix for <c>GetITMEquipmentPrefab(string, eActorBodies, eActorBodies)</c>.</summary>
        public static void GetITMEquipmentPrefab_Prefix(ref string pItemConfigName)
        {
            try
            {
                if (!ClassForgePlugin.FeaturesActive) return;
                if (string.IsNullOrEmpty(pItemConfigName)) return;

                var plan = ClassForgePlugin.CurrentMergePlan;
                if (plan == null || plan.VisualFallbacks.Count == 0) return;

                string donor;
                if (plan.VisualFallbacks.TryGetValue(pItemConfigName, out donor) && !string.IsNullOrEmpty(donor))
                    pItemConfigName = donor;
            }
            catch (Exception)
            {
                // Fail-safe: silent pass-through — the finalizer below still guards the caller.
            }
        }

        /// <summary>Finalizer for <c>CharacterVisualHelper.VisualReEquip</c> — log once per distinct
        /// message and suppress, degrading a missing equipment visual to an invisible item instead of an
        /// input-locked party screen.</summary>
        public static Exception VisualReEquip_Finalizer(Exception __exception)
        {
            if (__exception == null) return null;
            try
            {
                var key = __exception.GetType().Name + "|" + (__exception.Message ?? "");
                if (LoggedSuppressed.Add(key))
                    ClassForgePlugin.Log.LogError(
                        "[ClassForge] equipment visual attach failed — item rendered without a 3D model " +
                        "(fail-safe, exception suppressed to keep the UI responsive; ship a visualfallbacks.json " +
                        "entry for the offending item id): " + __exception);
            }
            catch (Exception)
            {
                // Even the logging path must never throw out of a finalizer.
            }
            return null;
        }
    }
}
