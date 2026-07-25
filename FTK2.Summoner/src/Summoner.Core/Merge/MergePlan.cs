using System.Collections.Generic;
using Summoner.Core.Diagnostics;

namespace Summoner.Core.Merge
{
    /// <summary>
    /// One entry to add to Configs.Followers or Configs.Characters. <see cref="Entry"/> holds a
    /// Summoner.Core.Model.FollowerEntry when this add came from FollowerAdds, or a
    /// Summoner.Core.Model.CharacterEntry when it came from CharacterAdds -- Summoner.Core has no
    /// single common base for the two game-mirrored POCOs, and MergePlan's shape is fixed by
    /// design §A4.1 to one List&lt;MergeAdd&gt; per destination dictionary, so the payload is typed
    /// loosely here and cast by the consumer (Summoner.Plugin's ConfigsSink), which already knows
    /// which list it is reading from.
    /// </summary>
    public sealed class MergeAdd
    {
        public string PackId { get; set; }
        public string Id { get; set; }
        public object Entry { get; set; }
    }

    /// <summary>An id that was not added because it is already present in the live Configs (design §A2.3).</summary>
    public sealed class MergeSkip
    {
        public string PackId { get; set; }
        public string Id { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>Output of MergePlanner.Plan (design §A4.1).</summary>
    public sealed class MergePlan
    {
        public List<MergeAdd> FollowerAdds { get; set; } = new List<MergeAdd>();
        public List<MergeAdd> CharacterAdds { get; set; } = new List<MergeAdd>();
        public List<MergeSkip> Skips { get; set; } = new List<MergeSkip>();
        public List<Finding> Rejects { get; set; } = new List<Finding>();
    }
}
