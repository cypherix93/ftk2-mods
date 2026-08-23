using System;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Splits a dot path (e.g. <c>RouterHelper.Env.NetworkData.IsHost</c>) into segments for
    /// <c>crucible_get</c>. Pure string logic — no game reference, no reflection — so it unit-tests
    /// with no game running.
    ///
    /// Malformed input is rejected here rather than downstream: an empty segment (<c>a..b</c>) would
    /// otherwise resolve as a confusing "member not found" deep in the reflective walk instead of an
    /// immediate, precise error.
    /// </summary>
    public static class PathParser
    {
        public static bool TryParse(string path, out string[] segments, out string error)
        {
            segments = null;
            error = null;

            if (string.IsNullOrEmpty(path) || path.Trim().Length == 0)
            {
                error = "empty path";
                return false;
            }

            string[] parts = path.Split('.');
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0)
                {
                    error = "empty segment at index " + i;
                    return false;
                }
            }

            segments = parts;
            return true;
        }
    }
}
