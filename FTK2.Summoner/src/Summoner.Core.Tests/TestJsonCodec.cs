using System.Text.Json;
using Summoner.Core.Packs;

namespace Summoner.Core.Tests
{
    /// <summary>
    /// IJsonCodec over System.Text.Json, which ships in the net8.0 shared framework (no NuGet
    /// package needed -- build rule: BCL only). Mirrors the role Summoner.Plugin/Adapters/
    /// GameJsonCodec.cs plays for the real game host, just backed by the plain library instead of
    /// the game's JsonHelper wrapper.
    /// </summary>
    public sealed class TestJsonCodec : IJsonCodec
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        public T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
    }
}
