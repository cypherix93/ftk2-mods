using System.Collections.Generic;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Plain transport between the plugin's reflection reader and <see cref="SnapshotShape"/>.
    ///
    /// Deliberately dumb: no game types, no reflection, no logic. The reader fills these in; the
    /// shape builder decides how they serialize. That split is what lets every shape/sort/null rule
    /// be tested with no game running.
    ///
    /// Note the *Available flags. "Could not read it" and "there is none" must serialize
    /// differently — null versus an empty collection — or a reflection failure would read as
    /// "the ability applied nothing", and a scenario would pass because the oracle went blind.
    /// </summary>
    public sealed class StatusView
    {
        public string Id;
        public int? Duration;
        public int? InitialDuration;
        public int? TickDuration;
        public string OriginEntityId;
        // No Stacks: StatusEffectInfo has no stack count, and the suffixed-id convention
        // (STATUS_ATTACKUP_00/_01) is unverified. See the plan's "stacks decision".
    }

    /// <summary>
    /// One <c>Thing</c> hanging off a combatant that carries custom data.
    ///
    /// Only things with a non-empty <c>Thing.CustomData</c> are emitted. Every combatant owns a full
    /// inventory of ordinary items and dumping all of them would bury the two entries that matter
    /// (the Trainer ball's <c>CF_POKE_*</c> record) under a hundred that never change.
    /// </summary>
    public sealed class ThingView
    {
        /// <summary>Runtime id (<c>Guid.NewGuid()</c> at <c>InventoryHelper.cs:83</c>) — redacted from the digest.</summary>
        public string Id;
        public string ConfigName;
        public readonly Dictionary<string, string> CustomData = new Dictionary<string, string>();
    }

    public sealed class CombatantView
    {
        public string Id;
        public string Name;
        public string ClassId;
        public bool IsPlayer;
        public int? Hp;
        public int? MaxHp;
        public bool? Alive;

        /// <summary>
        /// <c>VenueComponent.TilePosition.x</c> — the ROW/DEPTH axis, not the horizontal one
        /// (<c>VenueHelper.cs:988/998</c> takes Min/Max of <c>.x</c> to find the front and back rows).
        /// Emitted under an explicit <c>x</c> key rather than as a tuple because this axis has already
        /// been documented backwards once.
        /// </summary>
        public int? TileX;
        /// <summary><c>VenueComponent.TilePosition.y</c> — the LATERAL axis.</summary>
        public int? TileY;

        /// <summary><c>CharacterComponent.GroupIndex</c>. 0 = player side, 1 = enemy side.</summary>
        public int? GroupIndex;

        /// <summary>
        /// Index into <c>CombatState.Entities</c> — the peer-stable identity the whole determinism
        /// layer is built on (<c>ClassForge.Core/Rng/EntityKey.cs</c>). <see cref="NoOrdinal"/> when
        /// the entity was not located in the roster.
        /// </summary>
        public int RosterOrdinal = NoOrdinal;

        /// <summary>Not-found sentinel for <see cref="RosterOrdinal"/>; serializes as null.</summary>
        public const int NoOrdinal = int.MaxValue;

        /// <summary>
        /// <c>eActorProperties.SUMMON</c> in <c>CharacterComponent.Properties</c> — which is exactly
        /// what <c>CharacterHelper.ActorHasProperty</c> does (<c>CharacterHelper.cs:2010-2017</c>:
        /// <c>Properties != null &amp;&amp; Properties.Contains(p)</c>). Read as a list membership
        /// rather than through the two-arg helper so no overload has to be resolved reflectively.
        /// </summary>
        public bool? IsSummon;

        /// <summary>True when it is a venue TILE entity, not an actor. Tiles live in
        /// <c>CombatState.Entities</c> alongside characters, so they occupy roster ordinals.</summary>
        public bool IsTile;

        public bool StatusesAvailable;
        public readonly List<StatusView> Statuses = new List<StatusView>();

        public bool StatsAvailable;
        /// <summary>String-keyed: <c>eStats</c> does not exist anywhere in the assembly.</summary>
        public readonly Dictionary<string, int> Stats = new Dictionary<string, int>();

        /// <summary>
        /// <c>CharacterComponent.CustomData</c>, verbatim and unfiltered. NOT allow-listed: a new
        /// class feature's state has to show up without another harness change.
        /// </summary>
        public bool CustomDataAvailable;
        public readonly Dictionary<string, string> CustomData = new Dictionary<string, string>();

        /// <summary><c>CharacterComponent.Things</c>, restricted to entries carrying custom data.</summary>
        public bool ThingsAvailable;
        public readonly List<ThingView> Things = new List<ThingView>();
    }

    /// <summary>
    /// One venue tile. Tile entities carry a <c>VenueComponent</c> (position) and a
    /// <c>VenueTileComponent</c> (side, row band, aura statuses).
    ///
    /// <see cref="AuraStatuses"/> is the point of this type: it is where TILE effects live — the
    /// Chaos Mage hazard tile among them — and before this view nothing in the harness read it, so a
    /// tile effect was only ever evidenced by a decal in a screenshot.
    /// </summary>
    public sealed class TileView
    {
        /// <summary>Row/depth axis. See <see cref="CombatantView.TileX"/>.</summary>
        public int? X;
        /// <summary>Lateral axis.</summary>
        public int? Y;
        /// <summary><c>VenueTileComponent.GroupIndex</c>: which side of the board this tile is on.</summary>
        public int? GroupIndex;
        /// <summary><c>VenueTileComponent.RowPositionsType</c> (<c>eTileRowPositions</c>) as a string.</summary>
        public string RowPositionsType;

        public bool AuraStatusesAvailable;
        /// <summary><c>VenueTileComponent.AuraStatuses</c>. Sorted: a list whose order is not contractual.</summary>
        public readonly List<string> AuraStatuses = new List<string>();

        /// <summary>Roster index of the tile entity itself.</summary>
        public int RosterOrdinal = CombatantView.NoOrdinal;

        /// <summary>The actor standing on this tile, if any.</summary>
        public string OccupantId;
        public int? OccupantOrdinal;
    }

    public sealed class CombatView
    {
        public bool Active;
        /// <summary>CombatState.TotalRounds. NOT monotonic — resets to -1 per wave.</summary>
        public int? Round;
        /// <summary>CombatState.WaveIndex, so a consumer can segment a non-monotonic round.</summary>
        public int? Wave;
        /// <summary>Synthesized ordinal. Null when the hooks did not install.</summary>
        public int? Turn;
        /// <summary>Synthesized. PLAYER / ENEMY / NONE / UNKNOWN.</summary>
        public string Phase = TurnTracker.PhaseNone;
        public string ActiveEntityId;
        public string Episode;

        public bool CombatantsAvailable;
        /// <summary>CombatState.Entities order, preserved: it is the game's own replicated order.</summary>
        public readonly List<CombatantView> Combatants = new List<CombatantView>();

        public bool TilesAvailable;
        /// <summary>Sorted by (x, y, groupIndex) at shape time — see <c>SnapshotShape</c>.</summary>
        public readonly List<TileView> Tiles = new List<TileView>();
    }

    public sealed class RunView
    {
        public bool Present;
        public object Seed;
        public object Day;
        public object Gold;
        public object Chapter;
    }

    public sealed class NetworkView
    {
        public bool Online;
        public bool? IsHost;
        public int? PlayerCount;
    }
}
