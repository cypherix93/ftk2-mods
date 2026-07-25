using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace FTK2Mods.DevKit
{
    /// <summary>
    /// Minimal, culture-invariant glob matcher for <see cref="DataHasher"/>'s exclusion patterns.
    ///
    /// Supported syntax (relative, forward-slash paths only):
    ///  - <c>**</c> — any number of path segments (or none) e.g. <c>**/Localization/**</c>;
    ///  - <c>*</c>  — any run of characters inside a single segment;
    ///  - <c>?</c>  — exactly one character inside a single segment;
    ///  - a pattern with no <c>/</c> (e.g. <c>*.png</c>) also matches against the bare file name,
    ///    so callers do not have to write <c>**/*.png</c> to exclude a file type everywhere.
    ///
    /// Matching is case-insensitive using <see cref="RegexOptions.CultureInvariant"/>. That flag is
    /// load-bearing for R1: without it, a Turkish-locale peer folds <c>I</c> to <c>ı</c> and
    /// <c>**/Localization/**</c> silently stops matching <c>LOCALIZATION/en.json</c>, so that peer
    /// would hash files the other peers excluded and every session would report a false data
    /// mismatch. Covered by a Turkish-culture test in DevKit.Core.Tests.
    /// </summary>
    public static class GlobMatcher
    {
        private static readonly object CacheSync = new object();
        private static readonly Dictionary<string, Regex> Cache = new Dictionary<string, Regex>(StringComparer.Ordinal);

        /// <summary>Normalizes separators and strips any <c>./</c> or leading <c>/</c>.</summary>
        public static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            string p = path.Replace('\\', '/');
            while (p.StartsWith("./", StringComparison.Ordinal)) p = p.Substring(2);
            while (p.StartsWith("/", StringComparison.Ordinal)) p = p.Substring(1);
            return p;
        }

        /// <summary>True if <paramref name="relativePath"/> matches <paramref name="pattern"/>.</summary>
        public static bool IsMatch(string relativePath, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return false;
            string path = NormalizePath(relativePath);
            if (path.Length == 0) return false;
            Regex regex = GetRegex(pattern);
            if (regex.IsMatch(path)) return true;
            if (pattern.IndexOf('/') < 0 && pattern.IndexOf('\\') < 0)
            {
                int slash = path.LastIndexOf('/');
                if (slash >= 0) return regex.IsMatch(path.Substring(slash + 1));
            }
            return false;
        }

        /// <summary>True if any pattern matches. A null/empty pattern set excludes nothing.</summary>
        public static bool IsMatchAny(string relativePath, IEnumerable<string> patterns)
        {
            if (patterns == null) return false;
            foreach (string pattern in patterns)
            {
                if (string.IsNullOrEmpty(pattern)) continue;
                if (IsMatch(relativePath, pattern)) return true;
            }
            return false;
        }

        private static Regex GetRegex(string pattern)
        {
            Regex regex;
            lock (CacheSync)
            {
                if (Cache.TryGetValue(pattern, out regex)) return regex;
            }
            regex = new Regex(Translate(pattern),
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);
            lock (CacheSync)
            {
                Cache[pattern] = regex;
            }
            return regex;
        }

        private static string Translate(string pattern)
        {
            string p = NormalizePath(pattern);
            StringBuilder sb = new StringBuilder(p.Length * 3 + 4);
            sb.Append('^');
            int i = 0;
            while (i < p.Length)
            {
                char c = p[i];
                if (c == '*')
                {
                    bool doubleStar = (i + 1 < p.Length) && p[i + 1] == '*';
                    if (doubleStar)
                    {
                        bool followedBySlash = (i + 2 < p.Length) && p[i + 2] == '/';
                        if (followedBySlash)
                        {
                            // "**/" matches zero or more leading segments.
                            sb.Append("(?:.*/)?");
                            i += 3;
                        }
                        else
                        {
                            sb.Append(".*");
                            i += 2;
                        }
                        continue;
                    }
                    sb.Append("[^/]*");
                    i++;
                    continue;
                }
                if (c == '?')
                {
                    sb.Append("[^/]");
                    i++;
                    continue;
                }
                sb.Append(Regex.Escape(c.ToString()));
                i++;
            }
            sb.Append('$');
            return sb.ToString();
        }
    }
}
