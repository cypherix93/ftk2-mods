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
            d["tiles"] = c.TilesAvailable ? (object)BuildTiles(c.Tiles) : null;
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
                d["tile"] = BuildPosition(c.TileX, c.TileY);
                d["groupIndex"] = Box(c.GroupIndex);
                d["ordinal"] = c.RosterOrdinal == CombatantView.NoOrdinal ? null : (object)c.RosterOrdinal;
                d["isSummon"] = Box(c.IsSummon);
                d["isTile"] = c.IsTile;
                d["customData"] = c.CustomDataAvailable ? (object)BuildCustomData(c.CustomData) : null;
                d["things"] = c.ThingsAvailable ? (object)BuildThings(c.Things) : null;
                result.Add(d);
            }
            return result;
        }

        /// <summary>
        /// Named axes, never a bare tuple. <c>x</c> is the ROW/DEPTH axis and <c>y</c> is lateral
        /// (<c>VenueHelper.cs:988/998</c>); emitting a two-element array would leave that to be
        /// re-derived by every reader, and it has already been got backwards once.
        /// Null (not a half-filled object) when neither axis could be read.
        /// </summary>
        private static object BuildPosition(int? x, int? y)
        {
            if (!x.HasValue && !y.HasValue) return null;
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["x"] = Box(x);
            d["y"] = Box(y);
            return d;
        }

        /// <summary>
        /// Emit-all, not allow-listed. MiniJson writes object keys ordinal-sorted, so a dictionary's
        /// emission order is already deterministic and a newly added <c>CF_*</c> key shows up with no
        /// harness change.
        /// </summary>
        private static Dictionary<string, object> BuildCustomData(Dictionary<string, string> data)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            foreach (KeyValuePair<string, string> kv in data)
            {
                if (kv.Key == null) continue;
                d[kv.Key] = kv.Value;
            }
            return d;
        }

        /// <summary>
        /// Sorted by (configName, custom-data content, id). Id is LAST and only a tiebreak: it is a
        /// locally generated GUID (<c>InventoryHelper.cs:83</c>), so sorting on it first could give
        /// two peers different array orders for identical state.
        /// </summary>
        private static List<object> BuildThings(List<ThingView> things)
        {
            List<ThingView> sorted = new List<ThingView>(things);
            sorted.Sort(CompareThing);

            List<object> result = new List<object>();
            for (int i = 0; i < sorted.Count; i++)
            {
                ThingView t = sorted[i];
                if (t == null) continue;
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["id"] = t.Id;
                d["configName"] = t.ConfigName;
                d["customData"] = BuildCustomData(t.CustomData);
                result.Add(d);
            }
            return result;
        }

        private static int CompareThing(ThingView a, ThingView b)
        {
            if (a == null || b == null) return (a == null ? 0 : 1) - (b == null ? 0 : 1);
            int byConfig = string.CompareOrdinal(Text(a.ConfigName), Text(b.ConfigName));
            if (byConfig != 0) return byConfig;
            int byData = string.CompareOrdinal(CustomDataKey(a.CustomData), CustomDataKey(b.CustomData));
            if (byData != 0) return byData;
            return string.CompareOrdinal(Text(a.Id), Text(b.Id));
        }

        /// <summary>Content-derived, id-free sort key: sorted "k=v" pairs joined by a unit separator.</summary>
        private static string CustomDataKey(Dictionary<string, string> data)
        {
            if (data == null || data.Count == 0) return string.Empty;
            List<string> keys = new List<string>(data.Keys);
            keys.Sort(StringComparer.Ordinal);
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < keys.Count; i++)
            {
                if (i > 0) sb.Append('\u001f');
                sb.Append(keys[i]).Append('=').Append(Text(data[keys[i]]));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Sorted by (x, y, groupIndex): the roster's tile block is seeded from <c>Dictionary</c> key
        /// enumeration order (<c>CombatPhase.cs:314</c>) - stable in fact, not by contract. Sorting on
        /// the coordinates makes the array order contractual.
        /// </summary>
        private static List<object> BuildTiles(List<TileView> tiles)
        {
            List<TileView> sorted = new List<TileView>(tiles);
            sorted.Sort(CompareTile);

            List<object> result = new List<object>();
            for (int i = 0; i < sorted.Count; i++)
            {
                TileView t = sorted[i];
                if (t == null) continue;
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["x"] = Box(t.X);
                d["y"] = Box(t.Y);
                d["groupIndex"] = Box(t.GroupIndex);
                d["rowPositionsType"] = t.RowPositionsType;
                d["auraStatuses"] = t.AuraStatusesAvailable ? (object)BuildAuraStatuses(t.AuraStatuses) : null;
                d["ordinal"] = t.RosterOrdinal == CombatantView.NoOrdinal ? null : (object)t.RosterOrdinal;
                d["occupantId"] = t.OccupantId;
                d["occupantOrdinal"] = Box(t.OccupantOrdinal);
                result.Add(d);
            }
            return result;
        }

        private static int CompareTile(TileView a, TileView b)
        {
            if (a == null || b == null) return (a == null ? 0 : 1) - (b == null ? 0 : 1);
            int byX = Rank(a.X).CompareTo(Rank(b.X));
            if (byX != 0) return byX;
            int byY = Rank(a.Y).CompareTo(Rank(b.Y));
            if (byY != 0) return byY;
            return Rank(a.GroupIndex).CompareTo(Rank(b.GroupIndex));
        }

        /// <summary>Unread axes sort first and together, instead of comparing as 0 against a real 0.</summary>
        private static long Rank(int? v) { return v.HasValue ? v.Value : long.MinValue; }

        private static string Text(string v) { return v == null ? string.Empty : v; }

        /// <summary>
        /// Sorted ordinally. <c>VenueTileComponent.AuraStatuses</c> is a bare <c>List&lt;string&gt;</c>
        /// whose order nothing in the game guarantees.
        /// </summary>
        private static List<object> BuildAuraStatuses(List<string> statuses)
        {
            List<string> sorted = new List<string>(statuses);
            sorted.Sort(StringComparer.Ordinal);
            List<object> result = new List<object>();
            for (int i = 0; i < sorted.Count; i++) result.Add(sorted[i]);
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
