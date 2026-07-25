using System.Collections.Generic;
using ClassForge.Core.Json;

namespace ClassForge.Core
{
    /// <summary>Severity for a <see cref="Finding"/> — mirrors CONVENTIONS.md fail-safe posture (log loudly, never throw out of the loader).</summary>
    public enum FindingSeverity
    {
        Info,
        Warning,
        Error
    }

    /// <summary>
    /// One human-readable diagnostic produced anywhere in the loader/merge pipeline. This is ClassForge.Core's
    /// log abstraction: Core never writes to a console/BepInEx log directly (it has no I/O dependency for that),
    /// it only accumulates Findings; the Plugin layer is responsible for emitting each Finding through
    /// BepInEx's ManualLogSource at the matching level.
    /// </summary>
    public sealed class Finding
    {
        public FindingSeverity Severity { get; }
        public string Code { get; }
        public string Message { get; }
        public string PackId { get; }

        public Finding(FindingSeverity severity, string code, string message, string packId)
        {
            Severity = severity;
            Code = code;
            Message = message;
            PackId = packId;
        }

        public static Finding Info(string code, string message, string packId) => new Finding(FindingSeverity.Info, code, message, packId);
        public static Finding Warning(string code, string message, string packId) => new Finding(FindingSeverity.Warning, code, message, packId);
        public static Finding Error(string code, string message, string packId) => new Finding(FindingSeverity.Error, code, message, packId);

        public override string ToString() =>
            PackId != null
                ? $"[{Severity}] {Code}: {Message} (pack={PackId})"
                : $"[{Severity}] {Code}: {Message}";
    }

    /// <summary>Parsed <c>pack.json</c> (SPEC.md §4.1).</summary>
    public sealed class PackManifest
    {
        public string Id;
        public string Name;
        public string Version;
        public string Author;
        public string Description;
        public int LoadOrder;
        public List<string> Dependencies = new List<string>();
        public bool Enabled;
        /// <summary>Absolute (or IFileSource-relative) root directory this manifest was discovered under. Not part of pack.json itself.</summary>
        public string RootDir;
    }

    /// <summary>A pack.json found on disk, before its content files (classes/traits/etc.) have been parsed.</summary>
    public sealed class DiscoveredPack
    {
        public PackManifest Manifest { get; }
        public string RootDir { get; }

        public DiscoveredPack(PackManifest manifest, string rootDir)
        {
            Manifest = manifest;
            RootDir = rootDir;
        }
    }

    /// <summary>The fully-parsed content of one pack (SPEC.md §4.2-§4.5, §4.7-§4.8). Deliberately opaque —
    /// values are raw <see cref="JsonValue"/> objects, never game types (Core has zero game references and
    /// the merge hands these opaque parsed objects to the game layer, per the task brief).</summary>
    public sealed class ParsedPack
    {
        public string PackId;
        public Dictionary<string, JsonValue> Classes = new Dictionary<string, JsonValue>(System.StringComparer.Ordinal);
        public Dictionary<string, JsonValue> Traits = new Dictionary<string, JsonValue>(System.StringComparer.Ordinal);
        public Dictionary<string, JsonValue> Abilities = new Dictionary<string, JsonValue>(System.StringComparer.Ordinal);
        public Dictionary<string, JsonValue> Items = new Dictionary<string, JsonValue>(System.StringComparer.Ordinal);
        public Dictionary<string, string> Localization = new Dictionary<string, string>(System.StringComparer.Ordinal);
        /// <summary>content id (filename without extension) -> file path.</summary>
        public Dictionary<string, string> Icons = new Dictionary<string, string>(System.StringComparer.Ordinal);
        /// <summary>content id (filename without extension) -> file path.</summary>
        public Dictionary<string, string> Portraits = new Dictionary<string, string>(System.StringComparer.Ordinal);
    }

    /// <summary>One resolved merge instruction: "put this id's raw JSON value into the target Configs.* dictionary, sourced from this pack."</summary>
    public sealed class MergeOp
    {
        public string Id { get; }
        public JsonValue Value { get; }
        public string SourcePackId { get; }

        public MergeOp(string id, JsonValue value, string sourcePackId)
        {
            Id = id;
            Value = value;
            SourcePackId = sourcePackId;
        }
    }

    /// <summary>
    /// The fully-resolved, ordered plan the Plugin executes against Env.Configs (SPEC.md §3 runtime flow).
    /// Collisions are already resolved (last-pack-wins) by the time this is built; Findings records what happened.
    /// </summary>
    public sealed class MergePlan
    {
        /// <summary>-&gt; Configs.Characters, from classes.json.</summary>
        public List<MergeOp> Characters = new List<MergeOp>();
        /// <summary>-&gt; Configs.Things, from traits.json + items.json (same target dictionary in the game).</summary>
        public List<MergeOp> Things = new List<MergeOp>();
        /// <summary>-&gt; Configs.Abilities, from abilities.json.</summary>
        public List<MergeOp> Abilities = new List<MergeOp>();
        /// <summary>-&gt; Lang backing dictionary, from localization/en.json. Last pack wins per key.</summary>
        public Dictionary<string, string> Localization = new Dictionary<string, string>(System.StringComparer.Ordinal);
        /// <summary>content id -&gt; file path, from icons/*.png. Last pack wins per id.</summary>
        public Dictionary<string, string> Icons = new Dictionary<string, string>(System.StringComparer.Ordinal);
        /// <summary>content id -&gt; file path, from portraits/*.png. Last pack wins per id.</summary>
        public Dictionary<string, string> Portraits = new Dictionary<string, string>(System.StringComparer.Ordinal);
        /// <summary>Ids within <see cref="Things"/> whose Class == "TRAIT" — candidates for the trait registry
        /// (SPEC-DELTA-v1.1 §1 OQ#1: grant/remove is native via the TRAIT_ ConfigName prefix, no bridge needed).</summary>
        public List<string> TraitIds = new List<string>();
        public List<Finding> Findings = new List<Finding>();
    }

    /// <summary>Everything one <c>PackLoader.Load()</c> call produced (SPEC.md §3 runtime flow, end to end).</summary>
    public sealed class PackLoadResult
    {
        /// <summary>Every pack.json found under the scanned roots, alphabetically sorted by id (deterministic discovery), before any enabled-filter or ordering.</summary>
        public List<PackManifest> DiscoveredPacks = new List<PackManifest>();
        /// <summary>Packs that were enabled, resolved (no cycle/missing-dep), parsed successfully, and merged — in final resolved load order.</summary>
        public List<PackManifest> EnabledOrderedPacks = new List<PackManifest>();
        /// <summary>Packs discovered but not merged (disabled, cyclic, missing dependency, or parse failure) — see Findings for why.</summary>
        public List<PackManifest> SkippedPacks = new List<PackManifest>();
        public MergePlan MergePlan = new MergePlan();
        /// <summary>SHA-256 hex digest over every enabled pack's files, excluding localization/** (SPEC.md §3, §9.6).</summary>
        public string DataHash = "";
        public List<Finding> Findings = new List<Finding>();
    }

    /// <summary>The payload shape ClassForgePlugin.Load() reflection-calls FTK2Mods.DevKit.ParityService.Register(...) with (SPEC.md §3, §6, §9.6).</summary>
    public sealed class ParityRegistration
    {
        public string Guid;
        public string Version;
        public string DataHash;
        /// <summary>Sorted (ordinal) list of active pack ids — the parity-relevant unit is a pack, not an individual content id.</summary>
        public string[] EnabledFeatures = System.Array.Empty<string>();
    }

    /// <summary>Input to <see cref="Hashing.DataHasher"/>: one enabled pack's id + the root directory to hash.</summary>
    public struct PackForHash
    {
        public readonly string PackId;
        public readonly string RootDir;

        public PackForHash(string packId, string rootDir)
        {
            PackId = packId;
            RootDir = rootDir;
        }
    }

    /// <summary>
    /// Snapshot of ids already present in the LIVE (pre-merge) game <c>Configs</c> dictionaries, captured by
    /// the Plugin immediately before a merge run and handed to <see cref="MergePlanner"/> (MP review M0).
    /// ClassForge packs are adds-only: a pack entry whose id collides with something already in the live
    /// target dictionary (vanilla content, or content the game itself pre-populated) is refused with a loud
    /// <see cref="Finding"/> rather than silently overwriting it. This is a DIFFERENT check from the existing
    /// pack-vs-pack collision warning in <see cref="MergePlanner"/>, which still resolves last-pack-wins
    /// unchanged — only a collision against a LIVE id is a hard refusal.
    /// <para>A null/absent set (the Plugin's own construction, or a caller such as
    /// <c>ClassForge.PackCheck</c> that has no live <c>Configs</c> to snapshot) is treated as empty: nothing is
    /// refused. That keeps every existing caller's behavior unchanged unless it opts in by supplying real ids.</para>
    /// </summary>
    public sealed class LiveIdSets
    {
        public static readonly LiveIdSets Empty = new LiveIdSets(null, null, null);

        /// <summary>Live keys of <c>Configs.Characters</c> (classes.json target).</summary>
        public ISet<string> Characters { get; }
        /// <summary>Live keys of <c>Configs.Things</c> (traits.json + items.json target).</summary>
        public ISet<string> Things { get; }
        /// <summary>Live keys of <c>Configs.Abilities</c> (abilities.json target).</summary>
        public ISet<string> Abilities { get; }

        public LiveIdSets(IEnumerable<string> characters, IEnumerable<string> things, IEnumerable<string> abilities)
        {
            Characters = ToSet(characters);
            Things = ToSet(things);
            Abilities = ToSet(abilities);
        }

        private static ISet<string> ToSet(IEnumerable<string> source)
            => source != null
                ? new HashSet<string>(source, System.StringComparer.Ordinal)
                : new HashSet<string>(System.StringComparer.Ordinal);
    }
}
