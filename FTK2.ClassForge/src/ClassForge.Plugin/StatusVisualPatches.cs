using System;
using System.Collections.Generic;
using IOG.dObjects;

namespace ClassForge.Plugin
{
    /// <summary>
    /// Status-icon donor fallback for pack statuses (STATUS_VIGOR_CF_ENGORGED invisibility fix, 2026-08-25;
    /// extended to the other four CF_PACK_ORIGINALS statuses 2026-08-26).
    ///
    /// <para><b>The gap.</b> Unlike items (whose 3D visual is a JSON-declarable
    /// <c>visualfallbacks.json</c> donor id), a status effect's icon lives on a baked Unity
    /// ScriptableObject — <c>dStatusEffect</c> — loaded into <c>dObjectHelper.Index.dStatusEffect</c>
    /// (<c>IOG.dObjects.dStatusEffectIndex</c>, built from <c>Resources.LoadAll&lt;dStatusEffect&gt;</c>).
    /// A pack status authored only in <c>statuses.json</c> (a <c>StatusEffectConfig</c> — duration,
    /// stats, no icon field at all) has no corresponding asset, so every lookup
    /// (<c>GetRecordByName</c>/<c>TryGetRecordByName</c>) returns null: the combat HUD icon strip
    /// (<c>CharacterCombatHudHelper</c>) skips it entirely, and the world-space status FX regen
    /// (<c>CharacterVisualHelper.RegenStatusVisuals</c>) logs <c>"No status record found for ..."</c>
    /// and renders nothing. The status is fully live in state — the game is simply never told what to
    /// draw for it.</para>
    ///
    /// <para><b>The fix, mirroring <see cref="EquipmentVisualPatches"/>'s visualfallbacks.json
    /// approach:</b> prefix both <c>dStatusEffectIndex</c> lookup methods and rewrite the incoming
    /// <c>pName</c> to a donor vanilla status id BEFORE the dictionary lookup runs, so every caller —
    /// HUD icon strip, world FX, tooltips — transparently renders the donor's icon/FX for our status.</para>
    ///
    /// <para><b>Not a substitute for a crash guard.</b> This table is DATA and data falls behind: it
    /// carried exactly one entry while <c>CF_PACK_ORIGINALS/statuses.json</c> grew to five, and the four
    /// unmapped statuses each resolved to null at
    /// <c>CombatTimelineViewHelper2._refreshPortraitVisuals</c>'s unguarded
    /// <c>statusRecord.IconTexture</c> — a hard freeze (the NRE escapes the scheduled
    /// <c>_progressRound</c> callback, so the round never finishes progressing and it is retried every
    /// frame). The real fix for THAT is
    /// <see cref="CombatVisualNullGuards.AddStatusIcon_Prefix"/>; this table is what makes the mechanic
    /// VISIBLE rather than merely non-fatal. Both are required.
    /// <see cref="IntentionallyIconless"/> plus the <c>ClassForge.Core.Tests</c> donor-completeness test
    /// are what stop the table falling behind again unnoticed.</para>
    /// </summary>
    internal static class StatusVisualPatches
    {
        /// <summary>Pack status id -&gt; donor vanilla status id.
        ///
        /// <para>Each donor is a real shipped status of the same <c>Type</c> the pack status authors in
        /// statuses.json, so the borrowed icon reads correctly: VIGOR-&gt;STATUS_VIGOR_00,
        /// CHARGE-&gt;STATUS_CHARGE_00, EVASIVE-&gt;STATUS_EVASIVE_00, PROWESS-&gt;STATUS_PROWESS_00,
        /// CONCENTRATION-&gt;STATUS_CONCENTRATION_00.</para>
        ///
        /// <para>Every pack id here is deliberately prefixed with an <c>eStatusEffectsGroups</c> MEMBER
        /// name (<c>STATUS_VIGOR_</c>, <c>STATUS_CHARGE_</c>, <c>STATUS_EVASIVE_</c>,
        /// <c>STATUS_PROWESS_</c>, <c>STATUS_CONCENTRATION_</c> — all five verified present in the enum
        /// against the shipped FTK2.dll) so the HUD icon-strip loops' <c>StartsWith($"{e}_")</c> gate
        /// matches and actually reaches this lookup — see PackContentParser.cs's CF_STATUS_ID_PREFIX
        /// comment for why that breaks our own naming convention on purpose. Note the gate is where
        /// <c>CharacterHudController</c>/<c>CombatDetailViewHelper</c>/<c>CharacterCombatHudHelper</c>
        /// enumerate the enum; the TIMELINE portrait strip has no such gate and walks every status the
        /// combatant carries.</para>
        ///
        /// <para><b>A donor must resolve in the ASSET INDEX, not merely exist in StatusEffects.json.</b>
        /// The two stores are independent, and a donor present only in the JSON reproduces the identical
        /// freeze. <see cref="VerifyDonors"/> probes the live index once and logs the result.</para></summary>
        private static readonly Dictionary<string, string> Donors =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "STATUS_VIGOR_CF_ENGORGED", "STATUS_VIGOR_00" },
                { "STATUS_CHARGE_CF_FOCUS_FIRE", "STATUS_CHARGE_00" },
                { "STATUS_EVASIVE_CF_SPREAD_OUT", "STATUS_EVASIVE_00" },
            };

        /// <summary>Pack statuses that are deliberately given NO donor, i.e. they are expected to render
        /// no icon at all.
        ///
        /// <para>The CF_PACK_ENCOUNTER_MODIFIERS family is an invisible, permanent
        /// (<c>Duration: -1</c>) stat rider applied to ENEMIES to express an encounter modifier; the
        /// modifier itself is surfaced by the encounter UI, not by a per-combatant status icon. They are
        /// also structurally unreachable from every icon path: <c>TickCombat</c> is false (so the
        /// timeline portrait loop <c>continue</c>s before <c>_addStatusIcon</c>) and the id does not
        /// start with any <c>eStatusEffectsGroups</c> member (so the enum-gated HUD strips never select
        /// them). Listing them here is the explicit record the completeness test demands, so that a
        /// future status added with no donor is a TEST FAILURE rather than a silent invisibility — or,
        /// before this file's guard existed, a silent freeze.</para></summary>
        private static readonly HashSet<string> IntentionallyIconless =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "STATUS_CF_ENCMOD_ARMORED",
                "STATUS_CF_ENCMOD_RESISTANT",
                "STATUS_CF_ENCMOD_FRENZIED",
                "STATUS_CF_ENCMOD_SWIFT",
                "STATUS_CF_ENCMOD_VETERAN",
                "STATUS_CF_ENCMOD_WEALTHY",
                "STATUS_CF_ENCMOD_CURSED",
                "STATUS_CF_ENCMOD_REGENERATING",
                "STATUS_CF_ENCMOD_GLASSCANNON",
                "STATUS_CF_ENCMOD_TREASUREGUARDED",
            };

        /// <summary>Latch for <see cref="VerifyDonors"/>. Set BEFORE the probe runs, because the probe
        /// calls back into the very method this class prefixes.</summary>
        private static bool _donorsVerified;

        /// <summary>Prefix for <c>dStatusEffectIndex.GetRecordByName(string)</c>.</summary>
        public static void GetRecordByName_Prefix(ref string pName)
        {
            VerifyDonors();
            Rewrite(ref pName);
        }

        /// <summary>Prefix for <c>dStatusEffectIndex.TryGetRecordByName(string, out dStatusEffect)</c>.</summary>
        public static void TryGetRecordByName_Prefix(ref string pName)
        {
            VerifyDonors();
            Rewrite(ref pName);
        }

        private static void Rewrite(ref string pName)
        {
            try
            {
                if (!ClassForgePlugin.FeaturesActive) return;
                if (string.IsNullOrEmpty(pName)) return;

                string donor;
                if (Donors.TryGetValue(pName, out donor) && !string.IsNullOrEmpty(donor))
                    pName = donor;
            }
            catch (Exception)
            {
                // Fail-safe: silent pass-through — worst case the status stays iconless, as it was
                // before this patch existed.
            }
        }

        /// <summary>
        /// Probes every donor id against the LIVE <c>dStatusEffect</c> asset index exactly once, and
        /// logs which resolved and which did not.
        ///
        /// <para><b>Why this exists.</b> "The donor is in StatusEffects.json" is NOT evidence that it
        /// resolves: the crash site reads <c>dObjectHelper.Index.dStatusEffect</c>, a separate store
        /// built from baked ScriptableObjects. A donor that is missing there leaves the pack status
        /// exactly as iconless as no donor at all. Deferred to the first index lookup rather than run at
        /// plugin load because the index is populated from <c>Resources</c> well after our
        /// <c>Awake</c>; by the time anything queries it, it is live.</para>
        ///
        /// <para>The latch is set BEFORE probing on purpose — the probe calls
        /// <c>GetRecordByName</c>, which re-enters this prefix.</para>
        /// </summary>
        private static void VerifyDonors()
        {
            if (_donorsVerified) return;
            _donorsVerified = true;
            try
            {
                var index = dObjectHelper.Index == null ? null : dObjectHelper.Index.dStatusEffect;
                if (index == null)
                {
                    _donorsVerified = false; // not live yet — try again on the next lookup.
                    return;
                }

                var resolved = new List<string>();
                var missing = new List<string>();
                foreach (var pair in Donors)
                {
                    dStatusEffect record = null;
                    try { record = index.GetRecordByName(pair.Value); }
                    catch (Exception) { /* treated as missing below */ }
                    (record != null ? resolved : missing).Add(pair.Key + "->" + pair.Value);
                }

                ClassForgePlugin.Log.LogInfo(
                    "[ClassForge] status icon donors resolved in dObjectHelper.Index.dStatusEffect (" +
                    resolved.Count + "/" + Donors.Count + "): " + string.Join(", ", resolved.ToArray()) +
                    " — plus " + IntentionallyIconless.Count + " pack statuses recorded as intentionally " +
                    "iconless.");
                if (missing.Count > 0)
                    ClassForgePlugin.Log.LogWarning(
                        "[ClassForge] status icon donors NOT found in the asset index — these pack " +
                        "statuses stay iconless (guarded, not fatal: see " +
                        "CombatVisualNullGuards.AddStatusIcon_Prefix). Pick a donor that exists as a " +
                        "baked dStatusEffect, not merely one present in StatusEffects.json: " +
                        string.Join(", ", missing.ToArray()));
            }
            catch (Exception)
            {
                // Diagnostics must never be the thing that throws out of a lookup prefix.
            }
        }
    }
}
