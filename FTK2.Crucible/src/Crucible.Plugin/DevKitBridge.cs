using System;
using System.Reflection;

namespace Crucible.Plugin
{
    /// <summary>
    /// Soft dependency on FTK2.DevKit, matching the convention in FTK2.DevKit/SPEC.md §3: resolve by
    /// name at runtime, no-op when DevKit is absent. Never a compile-time reference — that is what
    /// keeps sibling mods independently loadable in any order.
    ///
    /// DevKitLog and PatchRegistry are specced but not yet built; resolving to null is the designed
    /// no-op, and this starts forwarding the day DevKit ships them.
    /// </summary>
    internal static class DevKitBridge
    {
        private static MethodInfo _log;
        private static MethodInfo _report;
        private static bool _resolved;

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                Type logType = Type.GetType("FTK2Mods.DevKit.DevKitLog, ftk2mods.devkit");
                if (logType != null)
                    _log = logType.GetMethod("Log", new Type[] { typeof(string), typeof(string) });

                Type registryType = Type.GetType("FTK2Mods.DevKit.PatchRegistry, ftk2mods.devkit");
                if (registryType != null)
                    _report = registryType.GetMethod("Report", new Type[] { typeof(string), typeof(string), typeof(MethodBase) });
            }
            catch (Exception)
            {
                // DevKit absent, or a version with a different shape. This is optional tooling:
                // staying silent is correct, and never blocks Crucible from working alone.
            }
        }

        internal static void Log(string category, string message)
        {
            Resolve();
            if (_log == null) return;
            try { _log.Invoke(null, new object[] { category, message }); }
            catch (Exception) { }
        }

        internal static void ReportTarget(string description, MethodBase resolved)
        {
            Resolve();
            if (_report == null) return;
            try { _report.Invoke(null, new object[] { CruciblePlugin.PluginGuid, description, resolved }); }
            catch (Exception) { }
        }
    }
}
