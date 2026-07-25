using Summoner.Core.Packs;

namespace Summoner.Plugin.Adapters
{
    /// <summary>IJsonCodec over the game's own JsonHelper (System.Text.Json under the hood; design §A4.2, CONVENTIONS.md).</summary>
    public sealed class GameJsonCodec : IJsonCodec
    {
        public T Deserialize<T>(string json) => JsonHelper.Deserialize<T>(json);
    }
}
