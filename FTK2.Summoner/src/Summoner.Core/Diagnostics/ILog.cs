namespace Summoner.Core.Diagnostics
{
    /// <summary>Host-agnostic logging seam. Summoner.Plugin's Adapters/BepInExLog.cs wraps BepInEx's ManualLogSource.</summary>
    public interface ILog
    {
        void Info(string message);
        void Warn(string message);
        void Error(string message);
        void Debug(string message);
    }
}
