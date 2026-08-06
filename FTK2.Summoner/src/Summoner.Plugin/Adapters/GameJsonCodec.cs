using System.Text.Json;
using Summoner.Core.Packs;

namespace Summoner.Plugin.Adapters
{
    /// <summary>
    /// IJsonCodec for Summoner's OWN pack DTOs (PackManifest, FollowerEntry, ...). Day-one in-game
    /// finding: this used to delegate to the game's <c>JsonHelper</c>, whose options bind
    /// case-SENSITIVELY — pack.json's lowercase keys ("id", "loadOrder") never bound to the DTO
    /// properties and every pack was skipped as "has no 'id'". The offline suite passed because
    /// Summoner.Core.Tests' TestJsonCodec binds case-insensitively; this codec must match that
    /// behavior, since it parses the same camelCase pack files the tests validate. Game config
    /// types are NOT parsed here (ConfigsSink builds them in code), so game-parity parsing is not
    /// this class's job.
    /// </summary>
    public sealed class GameJsonCodec : IJsonCodec
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        public T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
    }
}
