using BepInEx.Logging;
using Summoner.Core.Diagnostics;

namespace Summoner.Plugin.Adapters
{
    /// <summary>ILog over BepInEx's ManualLogSource (design §A4.2).</summary>
    public sealed class BepInExLog : ILog
    {
        private readonly ManualLogSource _log;

        public BepInExLog(ManualLogSource log) => _log = log;

        public void Info(string message) => _log.LogInfo(message);
        public void Warn(string message) => _log.LogWarning(message);
        public void Error(string message) => _log.LogError(message);
        public void Debug(string message) => _log.LogDebug(message);
    }
}
