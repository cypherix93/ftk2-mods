using System.Collections.Generic;
using ClassForge.Core.Rng;

namespace ClassForge.Recipes.Abstractions
{
    /// <summary>
    /// The ONE place the engine turns a set of combatants into a deterministic sequence, and the ONE place
    /// it turns a combatant into a string that may be hashed, persisted or compared across peers.
    ///
    /// <para><b>Why this type exists.</b> Until 2026-08-26 the engine ordered and hashed on
    /// <c>ICombatEntity.Guid</c>. <c>Entity.Create()</c> mints that with <c>System.Guid.NewGuid()</c>
    /// (Entity.cs:119-125), so it is different on every peer for the same logical combatant. Every one of
    /// those sites was therefore a silent co-op divergence: ordering picked a different target on each peer,
    /// and hashing produced a different verdict. Neither shows up as a draw-count fork, so the vendor's
    /// own <c>GameRandomNextInt</c> probe would never have caught it — it surfaces only as two players
    /// watching different things happen. Both are now keyed on the roster ordinal instead
    /// (<see cref="ICombatEntity.RosterOrdinal"/>, and see <c>EntityKey</c> for the full argument).</para>
    ///
    /// <para><b>Stability is part of the contract.</b> <c>List&lt;T&gt;.Sort</c> is an UNSTABLE introsort:
    /// two entities whose ordinals compare equal (both unresolved, i.e. <see cref="Unknown"/>) could land in
    /// either order, and .NET does not promise the same order on two machines. <see cref="SortInPlace"/> is
    /// therefore an insertion sort, which is stable by construction — equal-ordinal entities keep the order
    /// they arrived in, and the caller's arrival order is itself replicated. Rosters are a few dozen entries,
    /// so the O(n²) worst case is irrelevant and the guarantee is worth more than the asymptotics.</para>
    /// </summary>
    public static class PeerOrder
    {
        /// <summary>"Roster index could not be resolved." Matches <see cref="ICombatEntity.RosterOrdinal"/>'s
        /// documented sentinel, and sorts last.</summary>
        public const int Unknown = int.MaxValue;

        /// <summary>The ordinal of <paramref name="e"/>, or <see cref="Unknown"/> for null.</summary>
        public static int OrdinalOf(ICombatEntity e)
        {
            return e == null ? Unknown : e.RosterOrdinal;
        }

        /// <summary>
        /// The canonical cross-peer TEXT form of a combatant — <c>"E&lt;ordinal&gt;"</c>, invariant culture,
        /// produced by <c>EntityKey.ToString()</c> so there is exactly one definition of the shape in the
        /// whole mod. This is what goes into a hash input, a persisted key, or anything written to
        /// <c>Thing.CustomData</c>; a raw <see cref="ICombatEntity.Guid"/> never does.
        /// </summary>
        public static string KeyOf(ICombatEntity e)
        {
            return new EntityKey(OrdinalOf(e)).ToString();
        }

        /// <summary>The same text form for an already-known ordinal.</summary>
        public static string KeyOf(int ordinal)
        {
            return new EntityKey(ordinal).ToString();
        }

        /// <summary>Ascending roster ordinal. Nulls and unresolved ordinals sort last.</summary>
        public static int Compare(ICombatEntity a, ICombatEntity b)
        {
            int oa = OrdinalOf(a), ob = OrdinalOf(b);
            return oa < ob ? -1 : (oa > ob ? 1 : 0);
        }

        /// <summary>
        /// Stable ascending-ordinal sort, in place. Equal ordinals keep their input order — see the type
        /// remarks for why an unstable <c>List.Sort</c> is not acceptable here.
        /// </summary>
        public static void SortInPlace(List<ICombatEntity> list)
        {
            if (list == null || list.Count < 2) return;
            for (int i = 1; i < list.Count; i++)
            {
                ICombatEntity key = list[i];
                int ko = OrdinalOf(key);
                int j = i - 1;
                while (j >= 0 && OrdinalOf(list[j]) > ko)
                {
                    list[j + 1] = list[j];
                    j--;
                }
                list[j + 1] = key;
            }
        }
    }
}
