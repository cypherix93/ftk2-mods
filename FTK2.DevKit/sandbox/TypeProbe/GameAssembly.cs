using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace TypeProbe
{
    /// <summary>
    /// Loads the retail FTK2 assembly out-of-process. There is no compile-time game reference, so
    /// this project builds on a machine with no game installed and degrades to a clear message at
    /// runtime. Verified 2026-08-23: Assembly.LoadFrom plus an AssemblyResolve fallback over the
    /// Managed folder loads 5389 types in a plain net10 process with no Unity and no Harmony.
    /// </summary>
    public static class GameAssembly
    {
        public static bool TryLoad(string managedDir, out Type[] types, out string error)
        {
            types = new Type[0];
            error = null;

            if (string.IsNullOrEmpty(managedDir) || !Directory.Exists(managedDir))
            {
                error = "Managed directory not found: " + managedDir;
                return false;
            }

            string ftk2 = Path.Combine(managedDir, "FTK2.dll");
            if (!File.Exists(ftk2))
            {
                error = "FTK2.dll not found in: " + managedDir;
                return false;
            }

            // Sibling assemblies (UnityEngine, Photon, …) are resolved on demand from the same folder.
            ResolveEventHandler handler = delegate (object sender, ResolveEventArgs args)
            {
                string name = new AssemblyName(args.Name).Name;
                string candidate = Path.Combine(managedDir, name + ".dll");
                return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
            };
            AppDomain.CurrentDomain.AssemblyResolve += handler;

            try
            {
                Assembly asm = Assembly.LoadFrom(ftk2);
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    // Partial load is expected and useful: Unity types that cannot resolve outside
                    // the player still leave the game's plain data types describable.
                    List<Type> loaded = new List<Type>();
                    foreach (Type t in ex.Types) if (t != null) loaded.Add(t);
                    types = loaded.ToArray();
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "Failed to load FTK2.dll: " + ex.Message;
                return false;
            }
        }
    }
}
