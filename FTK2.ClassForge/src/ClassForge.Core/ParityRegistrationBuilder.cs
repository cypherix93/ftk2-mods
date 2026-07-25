using System;
using System.Linq;

namespace ClassForge.Core
{
    /// <summary>
    /// Builds the payload for the reflection-based call to
    /// <c>FTK2.DevKit.Core.ParityRegistry.Register(string guid, string version, string dataHash, string[] enabledFeatures)</c>
    /// (SPEC.md §3, §6, §9.6). Core has no DevKit reference (mods never reference each other's assemblies);
    /// the Plugin resolves the call by name at runtime and no-ops gracefully if DevKit is absent.
    /// </summary>
    public static class ParityRegistrationBuilder
    {
        public static ParityRegistration Build(PackLoadResult result, string guid, string version)
        {
            return new ParityRegistration
            {
                Guid = guid,
                Version = version,
                DataHash = result.DataHash,
                EnabledFeatures = result.EnabledOrderedPacks
                    .Select(p => p.Id)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToArray()
            };
        }
    }
}
