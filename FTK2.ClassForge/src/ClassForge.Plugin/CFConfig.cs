using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using ClassForge.Core;

namespace ClassForge.Plugin
{
    /// <summary>
    /// The single door every ClassForge config knob goes through (P0.5).
    ///
    /// <para><b>Why this exists.</b> Parity coverage used to be opt-IN: three knob names were typed by hand
    /// into <c>ParityRegistrationBuilder</c>, and every other knob was invisible to the handshake, so two
    /// peers with different settings reported <c>Match</c> and desynced an hour in. The worst offender was
    /// never on that list — <c>[Combat] VenueGridPreset</c> substitutes the combat arena map, changing tiles
    /// per side (8/12/24); tile count drives <c>list.Count</c> in <c>AIHelper</c>, which drives
    /// <c>ShuffleList</c>'s draw count on the SHARED <c>GameRandom</c> stream. A preset mismatch desyncs on
    /// the first AI turn, with a green verdict. Hand-naming knobs guaranteed the 20th would be forgotten the
    /// same way the 4th was, so the mechanism is inverted to opt-OUT: <see cref="Bind{T}"/> takes
    /// <see cref="ParityClass"/> as a REQUIRED argument, so a new knob cannot compile without an author
    /// deciding, and the decision is recorded in <see cref="ParityKnobRegistry"/> automatically.</para>
    ///
    /// <para><b>And a second belt.</b> A wrapper only covers bind sites that route through it. Four
    /// <c>TrainerPartner*</c> files bind ten <c>[Trainer]</c> knobs and are owned by another workstream, so
    /// they are classified declaratively in <see cref="ExternalClassifications"/> and adopted by
    /// <see cref="Reconcile"/>, which sweeps the LIVE <c>ConfigFile</c> after every bind has run. Anything
    /// found there in neither the registry nor the table is a knob nobody classified: it is logged as an
    /// ERROR and adopted as <see cref="ParityClass.Gameplay"/> — fail closed, because over-inclusion costs
    /// a false refusal at join and under-inclusion costs a desync mid-run.</para>
    /// </summary>
    internal static class CFConfig
    {
        /// <summary>
        /// Binds a BepInEx config entry AND registers its parity classification in one call.
        /// <paramref name="parity"/> is positional and non-optional by design — see the type doc.
        ///
        /// <para><b>How to choose <paramref name="parity"/>.</b> Ask ONE question: does this knob gate a
        /// write to <c>Thing.CustomData</c>/<c>CharacterComponent</c> custom data, a party/summon roster
        /// mutation, anything that changes <c>CombatState.Entities.Count</c>, or anything that changes the
        /// combat tile count? If yes it is <see cref="ParityClass.Gameplay"/> — <i>regardless of how
        /// cosmetic it sounds</i>. Two of the three tests are non-obvious:</para>
        /// <list type="bullet">
        /// <item><c>CustomData</c> is NOT on <c>NetworkDebuggingHelper.cs:40-43</c>'s ignore list, so it is
        /// inside the vendor's own desync MD5 (<c>GameAction.DesyncDetectionData.Hash</c> over near-full
        /// <c>GameRunData</c>). A knob gating a <c>CF_POKE_HP</c> or <c>CF_POKE_NICKNAME</c> write puts two
        /// disagreeing peers on different HASHED state — the game itself calls that a desync.</item>
        /// <item>Entity and tile counts feed <c>list.Count</c> in <c>AIHelper</c>, which is exactly how many
        /// draws <c>GameRandom.ShuffleList</c> takes from the SHARED stream. Any knob that moves either
        /// count desyncs the stream on the first AI turn.</item>
        /// </list>
        /// <para>Only when all four answers are no — the knob changes what THIS player sees and nothing
        /// else — is <see cref="ParityClass.Presentation"/> correct. When genuinely unsure, choose Gameplay:
        /// over-inclusion costs a false refusal at join, under-inclusion costs a desync an hour in.</para>
        /// </summary>
        internal static ConfigEntry<T> Bind<T>(
            ConfigFile config, string section, string key, T defaultValue, string description, ParityClass parity)
        {
            var entry = config.Bind(section, key, defaultValue, description);
            // Late-bound read: the payload must reflect the value at MERGE time (and after a runtime
            // config-file reload), not the value at plugin-Awake time.
            ParityKnobRegistry.Declare(section, key, parity, () => ParityValue.Format((object)entry.Value));
            return entry;
        }

        /// <summary>
        /// <see cref="Bind{T}"/>, but the knob reports something OTHER than its literal value to parity.
        ///
        /// <para><b>Why this exists (W1-H).</b> A knob's parity entry has exactly one job: be equal on two
        /// peers that are going to behave the same, and unequal on two that are not. For nearly every knob
        /// its own value is the right answer. <c>[Packs] AdditionalRoots</c> is the counter-example: it is
        /// an ABSOLUTE FILESYSTEM PATH, so two friends with byte-identical packs on <c>C:</c> and <c>D:</c>
        /// were refused at join over a drive letter — and since <c>FeaturesMismatch</c> defaults to
        /// <c>Block</c>, that refusal reads to a player as "the mod is broken". The path is not what
        /// matters; WHAT WAS FOUND at the path is, and that is already covered twice over (the enabled pack
        /// ids are emitted by <see cref="ParityRegistrationBuilder"/>, their content by
        /// <c>DataHasher</c>'s <c>DataHash</c>).</para>
        ///
        /// <para>Deliberately still a REQUIRED, positional <see cref="ParityClass"/> and still routed
        /// through this file, so the opt-OUT registry property the type doc describes is untouched: this
        /// changes what a knob reports, never whether it reports.</para>
        /// </summary>
        internal static ConfigEntry<T> BindWithParityValue<T>(
            ConfigFile config, string section, string key, T defaultValue, string description,
            ParityClass parity, Func<T, string> parityValue)
        {
            var entry = config.Bind(section, key, defaultValue, description);
            ParityKnobRegistry.Declare(section, key, parity, () =>
            {
                try { return ParityValue.Format(parityValue(entry.Value)); }
                catch { return "<unreadable>"; }
            });
            return entry;
        }

        /// <summary>
        /// Bind sites that cannot route through <see cref="Bind{T}"/> because their file is owned by another
        /// workstream. Keyed <c>"Section|Key"</c>. This table is the ONLY sanctioned bypass, and the
        /// source-scan test (<c>ClassForge.Core.Tests</c>, "no raw Config.Bind outside CFConfig") fails the
        /// build's test run if a raw <c>Config.Bind</c> appears anywhere in the Plugin whose (section, key)
        /// is not listed here — so the next knob still cannot bypass the registry, it can only be classified
        /// in a different place.
        ///
        /// <para>All ten are <see cref="ParityClass.Gameplay"/>, and not merely by the over-include rule:
        /// the persistence, autonomy and capture knobs change what the partner AI does with replicated
        /// combat state, and the two nickname knobs write <c>CF_POKE_NICKNAME</c> into
        /// <c>Thing.CustomData</c> on the ball item — which lands inside the serialized <c>GameRunData</c>
        /// graph that the VENDOR's own desync detector MD5s at every save/init/end-turn checkpoint
        /// (<c>GameAction.DesyncDetectionData.Hash</c>, docs/MULTIPLAYER.md open-question #2). A nickname
        /// difference is a detected desync in vanilla's own terms.</para>
        /// </summary>
        private static readonly Dictionary<string, ParityClass> ExternalClassifications =
            new Dictionary<string, ParityClass>(StringComparer.Ordinal)
            {
                // TrainerPartnerPersistence.cs — all four gate CF_POKE_HP / roster writes.
                // RevivePartnersInTown in particular: it now gates the town-revive CF_POKE_HP write on the
                // networked AdventureDirector._onUseService path, so two peers disagreeing on it disagree
                // on both the desync-hashed CustomData AND the partner roster (hence Entities.Count, hence
                // the shared-stream draw count). Presentation would be actively wrong here.
                { "Trainer|EnablePartnerPersistence", ParityClass.Gameplay },
                { "Trainer|CapPartnerMaxHp",          ParityClass.Gameplay },
                { "Trainer|PartnerMaxHpByStage",      ParityClass.Gameplay },
                { "Trainer|RevivePartnersInTown",     ParityClass.Gameplay },

                // TrainerPartnerNicknames.cs — writes Thing.CustomData; see the remark above.
                { "Trainer|EnablePartnerNicknames",   ParityClass.Gameplay },
                { "Trainer|DefaultPartnerNicknames",  ParityClass.Gameplay },

                // TrainerPartnerPanel.cs — the one [Trainer] knob that is arguably presentation (it appends
                // text to the class info tab and mutates nothing). Kept Gameplay anyway: it lives in a file
                // this change may not edit, so its behaviour cannot be re-verified here, and the cost
                // asymmetry (false refusal vs. desync) says over-include when unsure.
                { "Trainer|EnablePartnerPanel",       ParityClass.Gameplay },

                // TrainerPartnerAutonomy.cs
                { "Trainer|EnablePartnerAutonomy",        ParityClass.Gameplay },
                { "Trainer|ShapePartnerTendency",         ParityClass.Gameplay },
                { "Trainer|EnablePartnerCommandTendency", ParityClass.Gameplay },
            };

        private static bool _reconciled;

        /// <summary>
        /// Post-bind sweep of the live <c>ConfigFile</c>: adopts the declared external bind sites and fails
        /// closed on anything nobody classified. Called once at the end of <c>Awake</c>, after every
        /// <c>*.Bind(Config)</c> has run.
        ///
        /// <para>Per-pack <c>[Packs] &lt;id&gt;.Enabled</c> knobs bind LATER (from the
        /// <c>ConfigsHelper.LoadConfigs</c> postfix) and are therefore not visible here — they do not need
        /// to be, because <c>EnsurePackKnobsBound</c> routes through <see cref="Bind{T}"/> and so
        /// self-registers whenever it runs.</para>
        /// </summary>
        internal static void Reconcile(ConfigFile config)
        {
            try
            {
                if (config == null) return;

                int adopted = 0, unclassified = 0;

                foreach (var pair in config)
                {
                    var definition = pair.Key;
                    var entry = pair.Value;
                    if (definition == null || entry == null) continue;

                    string section = definition.Section;
                    string key = definition.Key;
                    if (ParityKnobRegistry.IsDeclared(section, key)) continue;

                    ParityClass parity;
                    if (ExternalClassifications.TryGetValue(section + "|" + key, out parity))
                    {
                        adopted++;
                    }
                    else
                    {
                        unclassified++;
                        parity = ParityClass.Gameplay; // fail closed.
                        ClassForgePlugin.Log.LogError(
                            "[ClassForge] UNCLASSIFIED CONFIG KNOB [" + section + "] " + key +
                            " — it was bound without going through CFConfig.Bind and is not listed in " +
                            "CFConfig.ExternalClassifications. Treating it as Gameplay (it WILL be compared " +
                            "at multiplayer join and can refuse a join). Route it through CFConfig.Bind " +
                            "with an explicit ParityClass, or add it to the table.");
                    }

                    var captured = entry;
                    ParityKnobRegistry.Declare(section, key, parity,
                        () => ParityValue.Format(captured.BoxedValue));
                }

                if (!_reconciled)
                {
                    _reconciled = true;
                    ClassForgePlugin.Log.LogInfo(
                        "[ClassForge] Parity knob registry: " + ParityKnobRegistry.All().Count + " knob(s) declared (" +
                        ParityKnobRegistry.Gameplay().Count + " Gameplay), " + adopted +
                        " adopted from the external classification table, " + unclassified + " unclassified.");
                }
            }
            catch (Exception ex)
            {
                // Never fatal — but this one is genuinely bad news, because it means the payload may be
                // short some knobs. Logged at Error so it is not mistaken for a routine warning.
                ClassForgePlugin.Log.LogError(
                    "[ClassForge] Parity knob reconciliation failed; the parity payload may be INCOMPLETE " +
                    "for this session: " + ex);
            }
        }
    }
}
