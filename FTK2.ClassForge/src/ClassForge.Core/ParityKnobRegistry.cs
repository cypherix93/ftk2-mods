using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClassForge.Core
{
    /// <summary>
    /// What a config knob does to a multiplayer session if two peers disagree about it.
    /// <para>There is deliberately no "unknown"/default member: <c>CFConfig.Bind</c> takes this as a
    /// REQUIRED argument precisely so a new knob cannot be added without an author choosing, which is the
    /// whole point of P0.5. The three hand-maintained knob names that used to be passed to
    /// <see cref="ParityRegistrationBuilder"/> by hand were opt-IN, so the 4th..20th knob was simply
    /// forgotten -- <c>[Combat] VenueGridPreset</c> among them, which substitutes the combat arena map and
    /// therefore the tile count that drives <c>AIHelper</c>'s <c>ShuffleList</c> draw count: a guaranteed
    /// desync on the first AI turn, reported as "Match".</para>
    /// </summary>
    public enum ParityClass
    {
        /// <summary>Disagreement changes what executes against replicated state (RNG draw counts, tile
        /// counts, stat values, loadout pools, which content is loaded). MUST be part of the parity
        /// payload. Over-inclusion costs a false refusal; under-inclusion costs a desync.</summary>
        Gameplay = 0,

        /// <summary>Disagreement is visible only to the local player (camera, opacity, log verbosity, which
        /// rows a UI list renders). Omitted from the parity payload so a cosmetic preference cannot refuse
        /// a join.</summary>
        Presentation = 1,
    }

    /// <summary>One registered knob. <see cref="ValueText"/> is a late-bound read so the payload always
    /// reflects the CURRENT value, not the value at bind time (knobs are re-read on every merge).</summary>
    public sealed class ParityKnob
    {
        public ParityKnob(string section, string key, ParityClass parityClass, Func<string> valueText)
        {
            Section = section ?? string.Empty;
            Key = key ?? string.Empty;
            ParityClass = parityClass;
            ValueText = valueText;
        }

        public string Section { get; private set; }
        public string Key { get; private set; }
        public ParityClass ParityClass { get; private set; }
        public Func<string> ValueText { get; private set; }

        /// <summary>The wire name: <c>Section.Key</c>. Ordinal-sorted by the builder.</summary>
        public string Name { get { return Section + "." + Key; } }
    }

    /// <summary>
    /// The opt-OUT knob registry. Every ClassForge config knob lands here (via <c>CFConfig.Bind</c>, or via
    /// <c>CFConfig.Reconcile</c>'s sweep of the live BepInEx <c>ConfigFile</c> for the few bind sites owned
    /// by other workstreams), and <see cref="ParityRegistrationBuilder"/> emits every
    /// <see cref="ParityClass.Gameplay"/> entry into the parity payload.
    ///
    /// <para>Host-agnostic on purpose: Core has no BepInEx reference, so a knob is modelled as
    /// (section, key, class, value-reader) and the Plugin supplies the reader over its
    /// <c>ConfigEntry&lt;T&gt;</c>. That also lets the Core tests exercise the registry with no game
    /// present.</para>
    /// </summary>
    public static class ParityKnobRegistry
    {
        private static readonly object Sync = new object();

        // Ordinal-keyed by "Section.Key" so enumeration order is already canonical.
        private static readonly SortedDictionary<string, ParityKnob> Knobs =
            new SortedDictionary<string, ParityKnob>(StringComparer.Ordinal);

        /// <summary>
        /// Registers (or re-registers) a knob. Idempotent per (section, key) -- <c>EnsurePackKnobsBound</c>
        /// runs from every <c>LoadConfigs</c>/<c>ReloadConfigs</c> postfix, so re-declaration must be a
        /// no-op-with-refresh rather than a duplicate or a throw. A later declaration REPLACES the earlier
        /// one, which is what a hot-reload re-bind wants.
        /// </summary>
        public static void Declare(string section, string key, ParityClass parityClass, Func<string> valueText)
        {
            if (string.IsNullOrEmpty(section)) throw new ArgumentException("section is required", "section");
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("key is required", "key");
            if (valueText == null) throw new ArgumentNullException("valueText");

            var knob = new ParityKnob(section, key, parityClass, valueText);
            lock (Sync) { Knobs[knob.Name] = knob; }
        }

        public static bool IsDeclared(string section, string key)
        {
            lock (Sync) { return Knobs.ContainsKey((section ?? "") + "." + (key ?? "")); }
        }

        public static bool TryGet(string section, string key, out ParityKnob knob)
        {
            lock (Sync) { return Knobs.TryGetValue((section ?? "") + "." + (key ?? ""), out knob); }
        }

        /// <summary>Every declared knob, ordinal-sorted by <see cref="ParityKnob.Name"/>. Snapshot: safe to
        /// enumerate while a late pack-knob bind adds more.</summary>
        public static IList<ParityKnob> All()
        {
            lock (Sync) { return new List<ParityKnob>(Knobs.Values); }
        }

        /// <summary>Every <see cref="ParityClass.Gameplay"/> knob, ordinal-sorted by name.</summary>
        public static IList<ParityKnob> Gameplay()
        {
            var list = new List<ParityKnob>();
            lock (Sync)
            {
                foreach (var knob in Knobs.Values)
                    if (knob.ParityClass == ParityClass.Gameplay) list.Add(knob);
            }
            return list;
        }

        /// <summary>Test seam only -- the plugin never clears the registry.</summary>
        public static void Reset()
        {
            lock (Sync) { Knobs.Clear(); }
        }
    }

    /// <summary>
    /// Culture-invariant canonical text for a knob value. Every peer must format the SAME value to the
    /// SAME string or parity compares two spellings of one number: a German locale renders
    /// <c>VenueTileBorderOpacity=0.5</c> as <c>"0,5"</c> under the ambient culture, which would make two
    /// identically-configured peers diverge. Bools are lower-cased so the emitted form matches the
    /// pre-P0.5 wire text (<c>feature:...=true</c>), not .NET's <c>"True"</c>.
    /// </summary>
    public static class ParityValue
    {
        public static string Format(object value)
        {
            if (value == null) return string.Empty;

            if (value is bool) return ((bool)value) ? "true" : "false";
            if (value is string) return Sanitize((string)value);

            // "R" (round-trip) so 0.1f never renders as 0.100000001 on one peer and 0.1 on another.
            if (value is float) return ((float)value).ToString("R", CultureInfo.InvariantCulture);
            if (value is double) return ((double)value).ToString("R", CultureInfo.InvariantCulture);

            var formattable = value as IFormattable;
            if (formattable != null) return Sanitize(formattable.ToString(null, CultureInfo.InvariantCulture));

            return Sanitize(Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        /// <summary>The payload is a <c>string[]</c>, but DevKit's comparer and every log line join it with
        /// commas and render it on one line. A knob whose value legitimately contains a comma or a newline
        /// (e.g. <c>[Packs] AdditionalRoots</c>) would otherwise be unreadable in the mismatch banner and
        /// ambiguous when split. Commas become <c>;</c> and any newline/CR/tab becomes a space -- a
        /// lossy-but-deterministic mapping, applied identically on both peers, so it can never manufacture
        /// or hide a divergence.</summary>
        private static string Sanitize(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var chars = text.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (c == ',') chars[i] = ';';
                else if (c == '\r' || c == '\n' || c == '\t') chars[i] = ' ';
            }
            return new string(chars);
        }
    }
}
