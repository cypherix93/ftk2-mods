using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassForge.Core
{
    /// <summary>
    /// Builds the payload for the reflection-based call to
    /// <c>FTK2Mods.DevKit.ParityService.Register(string guid, string version, string dataHash, string[] enabledFeatures)</c>
    /// (SPEC.md §3, §6, §9.6). Core has no DevKit reference (mods never reference each other's assemblies);
    /// the Plugin resolves the call by name at runtime and no-ops gracefully if DevKit is absent.
    ///
    /// <para><b>P0.5 — parity coverage is now OPT-OUT.</b> The previous implementation emitted enabled pack
    /// ids plus exactly THREE hand-added <c>feature:Name=bool</c> entries (<c>EnableRecipeEngine</c>,
    /// <c>EnableTraitLoadoutInjection</c>, <c>EnableStatModifiers</c>). Everything else was invisible to
    /// parity, so two peers with different settings compared as <c>Match</c> and desynced anyway. The
    /// most desync-potent knob was not even one of the three, nor in <c>[Trainer]</c>:
    /// <c>[Combat] VenueGridPreset</c> substitutes the combat arena map, which changes tiles per side
    /// (8/12/24); tile count drives <c>list.Count</c> in <c>AIHelper</c>, which drives <c>ShuffleList</c>'s
    /// draw count on the SHARED <c>GameRandom</c> stream (<c>GameRandom.ShuffleList</c> takes exactly
    /// <c>_list.Count</c> draws). A preset mismatch is a guaranteed desync on the first AI turn.</para>
    ///
    /// <para>Hand-naming three knobs guaranteed the 20th would be forgotten the same way, so the mechanism
    /// is inverted: <b>every</b> <c>Config.Bind</c> in ClassForge routes through <c>CFConfig.Bind</c>, whose
    /// <see cref="ParityClass"/> argument is REQUIRED (a new knob cannot compile without an author
    /// choosing), and this builder walks <see cref="ParityKnobRegistry"/> and emits every
    /// <see cref="ParityClass.Gameplay"/> entry. <see cref="ParityClass.Presentation"/> entries are omitted
    /// so a camera or log-verbosity preference cannot refuse a join.</para>
    ///
    /// <para><b>The wire format is unchanged</b> — still <c>feature:&lt;Name&gt;=&lt;value&gt;</c> inside the
    /// same ordinal-sorted <c>string[]</c> — so <c>DevKit.ParityComparer</c> needs no work. Only the
    /// <c>&lt;Name&gt;</c> part is now fully qualified as <c>Section.Key</c> (e.g.
    /// <c>feature:Skills.EnableRecipeEngine=true</c>), which is what lets the mismatch banner name the exact
    /// knob a player has to change.</para>
    /// </summary>
    public static class ParityRegistrationBuilder
    {
        public const string FeaturePrefix = "feature:";

        /// <summary>
        /// The production entry point: pack ids + every <see cref="ParityClass.Gameplay"/> knob currently in
        /// <see cref="ParityKnobRegistry"/>, ordinal-sorted and culture-invariant.
        /// </summary>
        public static ParityRegistration Build(PackLoadResult result, string guid, string version)
        {
            return Build(result, guid, version, ParityKnobRegistry.Gameplay());
        }

        /// <summary>
        /// Explicit-knob overload — the test seam, and the shape that makes the emitted payload a pure
        /// function of its inputs. <paramref name="knobs"/> is filtered to Gameplay here as well, so a
        /// caller handing over <see cref="ParityKnobRegistry.All"/> still gets the right payload.
        /// </summary>
        public static ParityRegistration Build(
            PackLoadResult result, string guid, string version, IEnumerable<ParityKnob> knobs)
        {
            var features = new List<string>();
            features.AddRange(result.EnabledOrderedPacks.Select(p => p.Id).Distinct(StringComparer.Ordinal));

            if (knobs != null)
            {
                foreach (var knob in knobs)
                {
                    if (knob == null || knob.ParityClass != ParityClass.Gameplay) continue;
                    features.Add(FeaturePrefix + knob.Name + "=" + ReadValue(knob));
                }
            }

            return new ParityRegistration
            {
                Guid = guid,
                Version = version,
                DataHash = result.DataHash,
                EnabledFeatures = features
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToArray()
            };
        }

        /// <summary>
        /// COMPATIBILITY SHIM for the pre-P0.5 call shape in <c>ConfigMergePatches</c>, which is owned by
        /// another workstream and cannot be edited in this change. The three bools it passes are IGNORED
        /// on purpose: they are exactly the three knobs whose values the registry now reads directly from
        /// their live <c>ConfigEntry&lt;bool&gt;</c>, so the shim's output is identical to what the caller
        /// intended, minus the maintenance hazard of a hand-kept list. Delete this overload — and the three
        /// arguments at the call site — the next time <c>ConfigMergePatches</c> is in scope.
        /// </summary>
        /// <remarks>Deliberately NOT marked <c>[Obsolete]</c>: the only caller is a file this change may not
        /// touch, so the attribute would produce a warning nobody in this scope is able to fix, in a repo
        /// that builds at zero warnings.</remarks>
        public static ParityRegistration Build(
            PackLoadResult result,
            string guid,
            string version,
            bool enableRecipeEngine,
            bool enableTraitLoadoutInjection,
            bool enableStatModifiers = true)
        {
            return Build(result, guid, version);
        }

        /// <summary>A knob whose value-reader throws must not take the whole registration down, but it must
        /// not silently vanish from the payload either (that would restore the exact "invisible knob"
        /// failure P0.5 exists to remove). It is emitted with a poison value instead, which both peers can
        /// see and which can never equal a real value.</summary>
        private static string ReadValue(ParityKnob knob)
        {
            try
            {
                var text = knob.ValueText != null ? knob.ValueText() : null;
                return text ?? string.Empty;
            }
            catch
            {
                return "<unreadable>";
            }
        }
    }
}
