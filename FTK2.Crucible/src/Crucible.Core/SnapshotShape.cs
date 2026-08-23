using System;
using System.Collections.Generic;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// The one place the <c>crucible.state.v2</c> JSON shape is defined.
    ///
    /// Emits only <c>Dictionary&lt;string,object&gt;</c> / <c>List&lt;object&gt;</c> / primitives,
    /// which is exactly what <see cref="MiniJson"/> serializes — anything else would silently
    /// stringify. Object keys need no sorting here (MiniJson sorts them ordinally); arrays do,
    /// wherever the source was a dictionary whose enumeration order is not contractual.
    /// </summary>
    public static class SnapshotShape
    {
        public const string SchemaV2 = "crucible.state.v2";

        public static Dictionary<string, object> BuildV2(string instance, string route, RunView run,
            NetworkView network, CombatView combat, bool consoleShowing, WarningSink warnings)
        {
            Dictionary<string, object> root = new Dictionary<string, object>();
            root["schema"] = SchemaV2;
            root["instance"] = instance;
            root["route"] = route;
            root["run"] = BuildRun(run);
            root["network"] = BuildNetwork(network);
            root["combat"] = BuildCombat(combat);
            root["console"] = consoleShowing;
            root["warnings"] = warnings == null ? new List<object>() : warnings.ToJsonList();
            return root;
        }

        private static Dictionary<string, object> BuildRun(RunView run)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["present"] = run != null && run.Present;
            if (run != null && run.Present)
            {
                d["seed"] = Scalar(run.Seed);
                d["day"] = Scalar(run.Day);
                d["gold"] = Scalar(run.Gold);
                d["chapter"] = Scalar(run.Chapter);
            }
            return d;
        }

        private static Dictionary<string, object> BuildNetwork(NetworkView net)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["online"] = net != null && net.Online;
            d["isHost"] = net == null ? null : Box(net.IsHost);
            d["playerCount"] = net == null ? null : Box(net.PlayerCount);
            return d;
        }

        private static Dictionary<string, object> BuildCombat(CombatView c)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["synthesized"] = Synthesized();

            if (c == null)
            {
                d["active"] = false;
                d["round"] = null;
                d["wave"] = null;
                d["turn"] = null;
                d["phase"] = TurnTracker.PhaseNone;
                d["activeId"] = null;
                d["episode"] = null;
                d["combatants"] = null;
                return d;
            }

            d["active"] = c.Active;
            d["round"] = Box(c.Round);
            d["wave"] = Box(c.Wave);
            d["turn"] = Box(c.Turn);
            d["phase"] = c.Phase;
            d["activeId"] = c.ActiveEntityId;
            d["episode"] = c.Episode;
            d["combatants"] = c.CombatantsAvailable ? (object)BuildCombatants(c.Combatants) : null;
            return d;
        }

        private static List<object> BuildCombatants(List<CombatantView> combatants)
        {
            List<object> result = new List<object>();
            for (int i = 0; i < combatants.Count; i++)
            {
                CombatantView c = combatants[i];
                if (c == null) continue;
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["id"] = c.Id;
                d["name"] = c.Name;
                d["classId"] = c.ClassId;
                d["isPlayer"] = c.IsPlayer;
                d["hp"] = Box(c.Hp);
                d["maxHp"] = Box(c.MaxHp);
                d["alive"] = Box(c.Alive);
                d["statuses"] = c.StatusesAvailable ? (object)BuildStatuses(c.Statuses) : null;
                d["stats"] = c.StatsAvailable ? (object)BuildStats(c.Stats) : null;
                result.Add(d);
            }
            return result;
        }

        /// <summary>
        /// Sorted by id: the source is <c>StatusEffectComponent.Statuses</c>, a Dictionary whose
        /// enumeration order is not contractual, and an unsorted array would make two identical
        /// peers produce different digests.
        /// </summary>
        private static List<object> BuildStatuses(List<StatusView> statuses)
        {
            List<StatusView> sorted = new List<StatusView>(statuses);
            sorted.Sort(CompareStatus);

            List<object> result = new List<object>();
            for (int i = 0; i < sorted.Count; i++)
            {
                StatusView s = sorted[i];
                if (s == null) continue;
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["id"] = s.Id;
                d["duration"] = Box(s.Duration);
                d["initialDuration"] = Box(s.InitialDuration);
                d["tickDuration"] = Box(s.TickDuration);
                d["originEntityId"] = s.OriginEntityId;
                result.Add(d);
            }
            return result;
        }

        private static int CompareStatus(StatusView a, StatusView b)
        {
            string x = a == null || a.Id == null ? string.Empty : a.Id;
            string y = b == null || b.Id == null ? string.Empty : b.Id;
            return string.CompareOrdinal(x, y);
        }

        private static Dictionary<string, object> BuildStats(Dictionary<string, int> stats)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            foreach (KeyValuePair<string, int> kv in stats) d[kv.Key] = kv.Value;
            return d;
        }

        /// <summary>
        /// Machine-readable honesty: these two fields are computed by Crucible, not read from the
        /// game, because the engine holds neither as data (field map).
        /// </summary>
        private static List<object> Synthesized()
        {
            List<object> l = new List<object>();
            l.Add("turn");
            l.Add("phase");
            return l;
        }

        private static object Box(int? v) { return v.HasValue ? (object)v.Value : null; }
        private static object Box(bool? v) { return v.HasValue ? (object)v.Value : null; }

        /// <summary>Primitives stay primitive so the digest can quantize; everything else
        /// stringifies rather than being walked as an arbitrary object graph.</summary>
        private static object Scalar(object value)
        {
            if (value == null) return null;
            if (value is bool || value is int || value is long || value is double || value is float) return value;
            return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
