using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace LiveDataHarness
{
    /// <summary>
    /// The only place the harness touches the game. Loads FTK2.dll out of the install's Managed folder and
    /// invokes ConfigsHelper.LoadConfigs(basePath) by reflection.
    ///
    /// No Harmony and no Unity shim are involved: the config-load path completes in a plain net10 process
    /// with zero patches applied, so the only requirement is Assembly.LoadFrom plus an AssemblyResolve
    /// fallback to the same folder. Resolution must target the live Managed folder rather than the repo's
    /// trimmed reference snapshot, which omits assemblies the load path pulls in transitively.
    ///
    /// Everything downstream sees plain string sets and boxed config objects, which keeps the
    /// game-coupled surface confined to this file.
    /// </summary>
    public sealed class GameData
    {
        public object Configs { get; private set; }
        private readonly Dictionary<string, ISet<string>> _idCache = new Dictionary<string, ISet<string>>(StringComparer.Ordinal);
        public string ManagedDir { get; private set; }

        public static GameData Load(GameInstall install)
        {
            var managed = install.ManagedDir;
            InstallResolver(managed);

            var ftk2 = Assembly.LoadFrom(Path.Combine(managed, "FTK2.dll"));
            var helper = ftk2.GetType("ConfigsHelper", true);
            var load = helper.GetMethod("LoadConfigs", BindingFlags.Public | BindingFlags.Static);
            if (load == null) throw new InvalidOperationException("ConfigsHelper.LoadConfigs(string) not found — a game update changed the entry point the harness depends on.");

            var configs = load.Invoke(null, new object[] { install.StreamingAssetsDir });
            if (configs == null) throw new InvalidOperationException("ConfigsHelper.LoadConfigs returned null.");

            return new GameData { Configs = configs, ManagedDir = managed };
        }

        private static bool _resolverInstalled;
        private static string _resolveDir;

        /// <summary>
        /// Subscribes the Managed-folder fallback exactly once per process. The handler must stay attached
        /// after the load returns — enum harvesting and field-level reflection pull further assemblies in
        /// lazily — but re-subscribing on a second load would stack duplicate handlers, so registration is
        /// idempotent and the target folder is updated in place.
        /// </summary>
        private static void InstallResolver(string managed)
        {
            _resolveDir = managed;
            if (_resolverInstalled) return;
            _resolverInstalled = true;
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var name = new AssemblyName(e.Name).Name;
                // A referenced assembly's simple name is never a path. Anything carrying a directory
                // separator or a ".." segment would probe outside the Managed folder, so it is refused
                // rather than resolved.
                if (string.IsNullOrEmpty(name) || !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal))
                    return null;
                var probe = Path.Combine(_resolveDir, name + ".dll");
                return File.Exists(probe) ? Assembly.LoadFrom(probe) : null;
            };
        }

        private object Dict(string dictName)
        {
            var field = Configs.GetType().GetField(dictName);
            if (field == null) throw new InvalidOperationException("Configs." + dictName + " does not exist — a game update changed the Configs shape the harness depends on.");
            return field.GetValue(Configs);
        }

        /// <summary>
        /// Ordinal key set of one Configs.* dictionary. Cached because most checks walk several id sets and
        /// the reflective key walk is repeated per check otherwise.
        /// </summary>
        public ISet<string> Ids(string dictName)
        {
            ISet<string> cached;
            if (_idCache.TryGetValue(dictName, out cached)) return cached;

            var dict = Dict(dictName);
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (dict != null)
            {
                var keys = dict.GetType().GetProperty("Keys").GetValue(dict, null) as IEnumerable;
                foreach (var k in keys) { var s = k as string; if (s != null) set.Add(s); }
            }
            _idCache[dictName] = set;
            return set;
        }

        public int Count(string dictName) { return Ids(dictName).Count; }

        /// <summary>Boxed values of one Configs.* dictionary, for field-level reflection.</summary>
        public IEnumerable<object> Values(string dictName)
        {
            var dict = Dict(dictName);
            if (dict == null) yield break;
            foreach (var kv in (IEnumerable)dict)
                yield return kv.GetType().GetProperty("Value").GetValue(kv, null);
        }

        /// <summary>Reads a public field off a boxed game config object. Returns null when the field is absent.</summary>
        public static object Field(object cfgObj, string fieldName)
        {
            if (cfgObj == null) return null;
            var f = cfgObj.GetType().GetField(fieldName);
            return f == null ? null : f.GetValue(cfgObj);
        }

        /// <summary>Configs.Langs[code] as a plain string dictionary. Returns null when the language is absent.</summary>
        public IDictionary<string, string> Lang(string code)
        {
            var langs = Dict("Langs");
            if (langs == null) return null;
            var contains = langs.GetType().GetMethod("ContainsKey");
            if (!(bool)contains.Invoke(langs, new object[] { code })) return null;
            return langs.GetType().GetProperty("Item").GetValue(langs, new object[] { code }) as IDictionary<string, string>;
        }
    }
}
