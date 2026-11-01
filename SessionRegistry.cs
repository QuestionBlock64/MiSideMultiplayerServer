using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace MiSideMultiplayer.RelayServer
{
    internal static class SessionRegistry
    {
        public sealed class Session
        {
            public string SessionId;         // the room name
            public string HostClientId;
            public string HostConnectionId;
            public string SessionName;
            public string HostDisplayName;
            public int    MaxPlayers;
            public int    PlayerCount;
            public int    Visibility;        // 0 = Public, 1 = Private, 2 = Direct
            public string Code;
            public string State = "Open";
            public DateTime CreatedUtc;
        }

        private static readonly ConcurrentDictionary<string, Session> _byRoomId =
            new ConcurrentDictionary<string, Session>(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, string> _byCode =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static readonly object _lock = new object();

        public static bool Register(
            string roomId,
            string hostClientId,
            string hostConnectionId,
            string sessionName,
            string hostDisplayName,
            int maxPlayers,
            int visibility,
            string code)
        {
            lock (_lock)
            {
                if (string.IsNullOrWhiteSpace(roomId)) return false;
                if (_byRoomId.ContainsKey(roomId)) return false;

                if (visibility == 1)
                {
                    code = NormalizeCode(code);
                    if (string.IsNullOrWhiteSpace(code)) return false;
                    if (_byCode.ContainsKey(code)) return false;
                }

                var s = new Session
                {
                    SessionId        = roomId,
                    HostClientId     = hostClientId,
                    HostConnectionId = hostConnectionId,
                    SessionName      = sessionName ?? "Session",
                    HostDisplayName  = hostDisplayName ?? "Player",
                    MaxPlayers       = Math.Clamp(maxPlayers, 1, 4),
                    PlayerCount      = 1,
                    Visibility       = visibility,
                    Code             = code ?? "",
                    CreatedUtc       = DateTime.UtcNow
                };
                _byRoomId[roomId] = s;
                if (visibility == 1) _byCode[code] = roomId;
                return true;
            }
        }

        public static void Unregister(string roomId)
        {
            if (string.IsNullOrWhiteSpace(roomId)) return;
            lock (_lock)
            {
                if (!_byRoomId.TryRemove(roomId, out var s) || s == null) return;
                if (!string.IsNullOrEmpty(s.Code)) _byCode.TryRemove(s.Code, out _);
            }
        }

        public static void UnregisterByHost(string hostConnectionId)
        {
            if (string.IsNullOrWhiteSpace(hostConnectionId)) return;

            List<string> toRemove = new List<string>();
            foreach (var kvp in _byRoomId)
            {
                if (kvp.Value != null &&
                    string.Equals(kvp.Value.HostConnectionId, hostConnectionId, StringComparison.Ordinal))
                    toRemove.Add(kvp.Key);
            }
            foreach (var id in toRemove) Unregister(id);
        }

        public static void UpdatePlayerCount(string roomId, int count)
        {
            if (string.IsNullOrWhiteSpace(roomId)) return;
            if (_byRoomId.TryGetValue(roomId, out var s) && s != null)
                s.PlayerCount = count;
        }

        public static bool TryLockInGame(string roomId, string hostConnectionId)
        {
            if (string.IsNullOrWhiteSpace(roomId) || string.IsNullOrWhiteSpace(hostConnectionId))
                return false;

            lock (_lock)
            {
                if (!_byRoomId.TryGetValue(roomId, out var session) || session == null)
                    return false;
                if (!string.Equals(session.HostConnectionId, hostConnectionId, StringComparison.Ordinal))
                    return false;

                session.State = "Locked_InGame";
                return true;
            }
        }

        public static bool IsLockedInGame(string roomId)
        {
            if (string.IsNullOrWhiteSpace(roomId)) return false;
            lock (_lock)
            {
                return _byRoomId.TryGetValue(roomId, out var session) && session != null &&
                       string.Equals(session.State, "Locked_InGame", StringComparison.Ordinal);
            }
        }

        public static List<Session> ListPublic()
        {
            return _byRoomId.Values
                .Where(s => s != null && s.Visibility == 0)
                .OrderByDescending(s => s.CreatedUtc)
                .ToList();
        }

        public static Session? ResolveByCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;
            if (!_byCode.TryGetValue(NormalizeCode(code), out var roomId)) return null;
            if (!_byRoomId.TryGetValue(roomId, out var s)) return null;
            return s;
        }

        public static bool HasRegisteredSessions()
        {
            return !_byRoomId.IsEmpty;
        }

        public static Session? Get(string roomId)
        {
            if (string.IsNullOrWhiteSpace(roomId)) return null;
            if (!_byRoomId.TryGetValue(roomId, out var s)) return null;
            return s;
        }

        public static bool IsReserved(string roomId)
        {
            return !string.IsNullOrWhiteSpace(roomId) && _byRoomId.ContainsKey(roomId);
        }

        private static string NormalizeCode(string code)
        {
            return string.IsNullOrWhiteSpace(code) ? string.Empty : code.Trim().ToUpperInvariant();
        }
    }
}
