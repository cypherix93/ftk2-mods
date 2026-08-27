using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace LiveDataHarness
{
    /// <summary>
    /// The only place the harness touches the game. Loads FTK2.dll out of the install's Managed folder and
    /// invokes <c>ConfigsHelper.LoadConfigs(basePath)</c> by reflection — no compile-time game reference, no
    /// Harmony, no Unity shim. Everything downstream sees plain string sets and boxed config objects, so a
    /// game update can only break this file plus GameVocabulary.
    /// </summary>
    public sealed class GameData
    {
        public object Configs { get; private set; }
        public string ManagedDir { get; private set; }

        private readonly Dictionary<string, ISet<string>> _idCache =
            new Dictionary<string, ISet<string>>(StringComparer.Ordinal);

        public static GameData Load(GameInstall install)
        {
            string managed = install.ManagedDir;

            // Sibling assemblies (UnityEngine, Photon, ...) resolve on demand out of the live Managed folder.
            // tools/bin/refs is deliberately NOT used: it is a partial snapshot and the live folder is the
            // only place every dependency actually exists.
            AppDomain.CurrentDomain.AssemblyResolve += delegate (object sender, ResolveEventArgs e)
            {
                string name = new AssemblyName(e.Name).Name;
                string probe = Path.Combine(managed, name + ".dll");
                return File.Exists(probe) ? Assembly.LoadFrom(probe) : null;
            };

            Assembly ftk2 = Assembly.LoadFrom(Path.Combine(managed, "FTK2.dll"));

            Type helper = ftk2.GetType("ConfigsHelper", false);
            if (helper == null)
                throw new InvalidOperationException("Type 'ConfigsHelper' not found in FTK2.dll — a game update broke the harness. Re-ground with TypeProbe.");

            MethodInfo load = helper.GetMethod("LoadConfigs", BindingFlags.Public | BindingFlags.Static,
                null, new Type[] { typeof(string) }, null);
            if (load == null)
                throw new InvalidOperationException("ConfigsHelper.LoadConfigs(string) not found — a game update broke the harness. Re-ground with TypeProbe.");

            object configs = load.Invoke(null, new object[] { install.StreamingAssetsDir });
            if (configs == null)
                throw new InvalidOperationException("ConfigsHelper.LoadConfigs returned null.");

            GameData data = new GameData();
            data.Configs = configs;
            data.ManagedDir = managed;
            return data;
        }

        private object Dict(string dictName)
        {
            FieldInfo field = Configs.GetType().GetField(dictName);
            if (field == null)
                throw new InvalidOperationException("Configs." + dictName + " does not exist — a game update broke the harness. Re-ground with TypeProbe.");
            return field.GetValue(Configs);
        }

        /// <summary>Ordinal key set of one Configs.* dictionary. Cached: LoadConfigs is seconds and key walks
        /// over a 2000-entry SerializedSortedDictionary are not free either.</summary>
        public ISet<string> Ids(string dictName)
        {
            ISet<string> cached;
            if (_idCache.TryGetValue(dictName, out cached)) return cached;

            object dict = Dict(dictName);
            HashSet<string> set = new HashSet<string>(StringComparer.Ordinal);
            if (dict != null)
            {
                IEnumerable keys = dict.GetType().GetProperty("Keys").GetValue(dict, null) as IEnumerable;
                if (keys != null)
                {
                    foreach (object k in keys)
                    {
                        string s = k as string;
                        if (s != null) set.Add(s);
                    }
                }
            }
            _idCache[dictName] = set;
            return set;
        }

        public int Count(string dictName) { return Ids(dictName).Count; }

        /// <summary>Boxed values of one Configs.* dictionary, for field-level reflection
        /// (e.g. CharacterConfig.BaseType).</summary>
        public IEnumerable<object> Values(string dictName)
        {
            object dict = Dict(dictName);
            if (dict == null) yield break;
            foreach (object kv in (IEnumerable)dict)
                yield return kv.GetType().GetProperty("Value").GetValue(kv, null);
        }

        /// <summary>Reads a public field off a boxed game config object. Returns null when the field is
        /// absent, so a game update degrades to a null rather than an exception (spec §3 error posture).</summary>
        public static object Field(object cfgObj, string fieldName)
        {
            if (cfgObj == null) return null;
            FieldInfo f = cfgObj.GetType().GetField(fieldName);
            return f == null ? null : f.GetValue(cfgObj);
        }

        /// <summary>Configs.Langs[code] as a plain string dictionary. Returns null when absent.</summary>
        public IDictionary<string, string> Lang(string code)
        {
            object langs = Dict("Langs");
            if (langs == null) return null;
            MethodInfo contains = langs.GetType().GetMethod("ContainsKey");
            if (contains == null) return null;
            if (!(bool)contains.Invoke(langs, new object[] { code })) return null;
            return langs.GetType().GetProperty("Item").GetValue(langs, new object[] { code }) as IDictionary<string, string>;
        }
    }
}
