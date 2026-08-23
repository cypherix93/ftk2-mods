using System;
using System.Collections.Generic;
using System.Text;

namespace FTK2Mods.Crucible
{
    /// <summary>Outcome of looking up a single dotted-prefix candidate as a type name.</summary>
    public enum TypeLookupStatus
    {
        NotFound,
        Found,
        Ambiguous
    }

    /// <summary>
    /// Result of a single <see cref="TypePrefixResolver"/> lookup callback invocation. Pure data —
    /// no <c>System.Type</c> reference here, so this (and <see cref="TypePrefixResolver"/> itself)
    /// stays unit-testable with no game, no Unity, no live assemblies.
    /// </summary>
    public struct TypeLookupResult
    {
        public TypeLookupStatus Status;

        /// <summary>Set when Status == Found. The type's canonical name, for reporting.</summary>
        public string ResolvedName;

        /// <summary>Set when Status == Ambiguous. The full names of every type that matched.</summary>
        public string[] Candidates;

        public static TypeLookupResult NotFoundResult()
        {
            return new TypeLookupResult { Status = TypeLookupStatus.NotFound };
        }

        public static TypeLookupResult FoundResult(string resolvedName)
        {
            return new TypeLookupResult { Status = TypeLookupStatus.Found, ResolvedName = resolvedName };
        }

        public static TypeLookupResult AmbiguousResult(string[] candidates)
        {
            return new TypeLookupResult { Status = TypeLookupStatus.Ambiguous, Candidates = candidates };
        }
    }

    /// <summary>
    /// Resolves a dotted path's leading type name by trying progressively longer dotted prefixes,
    /// longest-match-wins, before treating the remainder as members — fixing
    /// <c>crucible_get</c>/<c>crucible_set</c>'s old behavior of only ever trying
    /// <c>segments[0]</c> as a type (which fails outright on a namespaced path like
    /// <c>UnityEngine.Application.runInBackground</c>, and can silently bind the wrong type for an
    /// ambiguous bare name like <c>Application</c>).
    ///
    /// Pure logic — the actual "does this string name a type" question is answered by the caller's
    /// <paramref name="lookup"/> delegate, so this class has no dependency on System.Type/Reflection
    /// and unit-tests with a fake lookup standing in for a real type table.
    /// </summary>
    public static class TypePrefixResolver
    {
        /// <summary>
        /// Tries <c>segments[0..len)</c> joined with '.' as a type name, for len from
        /// <c>segments.Length</c> down to 1 (longest first). The first prefix <paramref name="lookup"/>
        /// reports Found wins; an Ambiguous result stops the search immediately (rather than falling
        /// back to a shorter prefix) and is reported as an error, never silently resolved. If no
        /// prefix is Found or Ambiguous, the error names every prefix tried, longest first.
        /// </summary>
        public static bool TryResolve(string[] segments, Func<string, TypeLookupResult> lookup,
            out int typeSegmentCount, out string resolvedTypeName, out string error)
        {
            typeSegmentCount = 0;
            resolvedTypeName = null;
            error = null;

            if (segments == null || segments.Length == 0)
            {
                error = "no segments to resolve";
                return false;
            }
            if (lookup == null)
            {
                error = "no type lookup provided";
                return false;
            }

            List<string> tried = new List<string>();
            for (int len = segments.Length; len >= 1; len--)
            {
                string candidate = string.Join(".", segments, 0, len);
                tried.Add(candidate);

                TypeLookupResult result = lookup(candidate);
                if (result.Status == TypeLookupStatus.Ambiguous)
                {
                    StringBuilder sb = new StringBuilder();
                    sb.Append("ambiguous type name '").Append(candidate).Append("': matches ");
                    string[] candidates = result.Candidates ?? new string[0];
                    for (int i = 0; i < candidates.Length; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(candidates[i]);
                    }
                    sb.Append(" -- use a fully-qualified path to disambiguate");
                    error = sb.ToString();
                    return false;
                }

                if (result.Status == TypeLookupStatus.Found)
                {
                    typeSegmentCount = len;
                    resolvedTypeName = string.IsNullOrEmpty(result.ResolvedName) ? candidate : result.ResolvedName;
                    return true;
                }
            }

            error = "no type found for any prefix of '" + string.Join(".", segments)
                + "' (tried longest-to-shortest: " + string.Join(", ", tried.ToArray()) + ")";
            return false;
        }
    }
}
