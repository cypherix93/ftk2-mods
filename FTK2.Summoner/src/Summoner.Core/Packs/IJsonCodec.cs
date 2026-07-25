namespace Summoner.Core.Packs
{
    /// <summary>
    /// JSON deserialization abstraction (design §A4). Summoner.Core carries zero JSON-library
    /// dependency; Summoner.Plugin's Adapters/GameJsonCodec.cs wraps the game's own JsonHelper
    /// (System.Text.Json), the test console runner wraps System.Text.Json directly (available in
    /// the net8.0 shared framework, no NuGet package needed).
    /// </summary>
    public interface IJsonCodec
    {
        T Deserialize<T>(string json);
    }
}
