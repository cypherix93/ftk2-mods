using System;
using System.Collections.Generic;
using System.Linq;

namespace WarBrain.Core
{
    /// <summary>Facts about an entity the resolver matches against (host-supplied).</summary>
    public class AssignmentSubject
    {
        public string CharacterId;
        public IReadOnlyList<string> Tags;
        public string BaseType;
        public string AiBehaviour; // vanilla eAiBehaviours name
    }

    /// <summary>SPEC §4.6 resolution: highest Priority, ties by match-kind specificity.</summary>
    public class AssignmentResolver
    {
        private readonly List<AssignmentRule> _rules;

        public AssignmentResolver(IEnumerable<AssignmentConfig> configs)
        {
            _rules = configs.SelectMany(c => c.Rules).ToList();
        }

        public (string profileId, string doctrineId) Resolve(AssignmentSubject subject, string defaultProfileId)
        {
            AssignmentRule best = null;
            int bestSpecificity = -1;
            foreach (var rule in _rules)
            {
                int spec = Specificity(rule.Match, subject);
                if (spec < 0) continue; // doesn't apply
                if (best == null || rule.Priority > best.Priority ||
                    (rule.Priority == best.Priority && spec > bestSpecificity))
                {
                    best = rule;
                    bestSpecificity = spec;
                }
            }
            return best != null ? (best.ProfileId, best.DoctrineId) : (defaultProfileId, null);
        }

        /// <summary>-1 = no match; otherwise higher = more specific (CharacterId > Tags > BaseType > AiBehaviour > Default).</summary>
        private static int Specificity(AssignmentMatch m, AssignmentSubject s)
        {
            int spec = 0;
            if (m.CharacterId != null)
            {
                if (!string.Equals(m.CharacterId, s.CharacterId, StringComparison.Ordinal)) return -1;
                spec = Math.Max(spec, 5);
            }
            if (m.Tags != null)
            {
                if (!Matches(s.Tags, m.Tags)) return -1;
                spec = Math.Max(spec, 4);
            }
            if (m.BaseType != null)
            {
                if (!Matches(new[] { s.BaseType ?? string.Empty }, m.BaseType)) return -1;
                spec = Math.Max(spec, 3);
            }
            if (m.AiBehaviour != null)
            {
                if (!string.Equals(m.AiBehaviour, s.AiBehaviour, StringComparison.Ordinal)) return -1;
                spec = Math.Max(spec, 2);
            }
            if (m.Default) spec = Math.Max(spec, 1);
            return spec == 0 ? -1 : spec;
        }

        private static bool Matches(IReadOnlyList<string> tags, TagMatch m)
        {
            tags = tags ?? Array.Empty<string>();
            if (m.AnyOf != null && m.AnyOf.Count > 0 && !m.AnyOf.Any(tags.Contains)) return false;
            if (m.NoneOf != null && m.NoneOf.Count > 0 && m.NoneOf.Any(tags.Contains)) return false;
            return true;
        }
    }
}
