using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiSideMultiplayer.RelayServer
{
    /// <summary>
    /// The wire format the client sends and receives. One envelope per line,
    /// newline-delimited, JSON-encoded.
    /// </summary>
    internal sealed class Envelope
    {
        public string? RoomId      { get; set; }
        public string? RoomName    { get; set; }
        public string? SenderId    { get; set; }
        public string? EventName   { get; set; }
        public string? Payload     { get; set; }
        public string? DisplayName { get; set; }
        public string? SceneName   { get; set; }

        public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        public static Envelope? TryParse(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;
            try
            {
                return JsonSerializer.Deserialize<Envelope>(line, JsonOptions);
            }
            catch
            {
                return null;
            }
        }

        public string Serialize()
        {
            return JsonSerializer.Serialize(this, JsonOptions);
        }

        // ── Helpers for nested payload payloads ─────────────────────────────

        public sealed class RegisterRequest
        {
            public string? roomId      { get; set; }
            public string? sessionName { get; set; }
            public int     maxPlayers  { get; set; }
            public int     visibility  { get; set; }
            public string? code        { get; set; }
        }

        public sealed class UnregisterRequest
        {
            public string? roomId { get; set; }
        }

        public sealed class ResolveRequest
        {
            public string? code { get; set; }
        }

        public sealed class JoinRequest
        {
            public string? roomId { get; set; }
        }

        public sealed class SessionStartRequest
        {
            public string? sceneName { get; set; }
            public string? startedByPlayerId { get; set; }
            public int expectedPlayerCount { get; set; }
        }

        public sealed class LoadingClientReadyRequest
        {
            public string? playerId { get; set; }
            public int stageIndex { get; set; }
        }

        public sealed class LoadingPhaseRequest
        {
            public int stageIndex { get; set; }
            public string? phaseId { get; set; }
            public float progress { get; set; }
            public int readyCount { get; set; }
            public int expectedCount { get; set; }
            public string? friendlyName { get; set; }
            public string? statusName { get; set; }
        }

        public sealed class PlayerStatePayload
        {
            public string? sceneName { get; set; }
        }

        public static T? TryDeserialize<T>(string? json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonSerializer.Deserialize<T>(json!, JsonOptions); }
            catch { return null; }
        }

        public static string SerializeObj(object o)
        {
            return JsonSerializer.Serialize(o, JsonOptions);
        }
    }
}
