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

    public sealed class CombatantView
    {
        public string Id;
        public string Name;
        public string ClassId;
        public bool IsPlayer;
        public int? Hp;
        public int? MaxHp;
        public bool? Alive;

        public bool StatusesAvailable;
        public readonly List<StatusView> Statuses = new List<StatusView>();

        public bool StatsAvailable;
        /// <summary>String-keyed: <c>eStats</c> does not exist anywhere in the assembly.</summary>
        public readonly Dictionary<string, int> Stats = new Dictionary<string, int>();
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
