using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace MiSideMultiplayer.RelayServer
{
    /// <summary>
    /// Relay-owned state for one menu-started loading run. The participant
    /// snapshot is fixed when the host starts the session; join requests are
    /// locked at that point and scene-load completion is tracked per connection.
    /// </summary>
    internal static class LoadingSessionRegistry
    {
        internal sealed class Snapshot
        {
            public int StageIndex;
            public string PhaseId = "Loading";
            public float Progress;
            public int ReadyCount;
            public int ExpectedCount = 1;
            public string FriendlyName = "Loading...";
            public string StatusName = "Loaded Players";
            public bool IsDone;
        }

        internal sealed class ExpiredParticipants
        {
            public string RoomId = "";
            public int[] ConnectionIds = Array.Empty<int>();
            public Snapshot State = new Snapshot();
        }

        private sealed class State
        {
            public readonly object Sync = new object();
            public readonly HashSet<int> Participants;
            public readonly HashSet<int> ReadyConnections = new HashSet<int>();
            public readonly HashSet<int> LoadedConnections = new HashSet<int>();
            public readonly string SceneName;
            public readonly int HostConnectionId;
            public DateTime StageStartedUtc;
            public int StageIndex;
            public string PhaseId = "Loading";
            public float Progress;
            public string FriendlyName = "Loading...";
            public string StatusName = "Loaded Players";
            public bool IsDone;
            public int LastTimedOutStageIndex = -1;

            public State(string sceneName, IEnumerable<int> participants, int hostConnectionId)
            {
                SceneName = sceneName;
                HostConnectionId = hostConnectionId;
                StageStartedUtc = DateTime.UtcNow;
                Participants = new HashSet<int>(participants ?? Enumerable.Empty<int>());
                Participants.Add(hostConnectionId);
            }

            public Snapshot GetSnapshot()
            {
                return new Snapshot
                {
                    StageIndex = StageIndex,
                    PhaseId = PhaseId,
                    Progress = Progress,
                    ReadyCount = LoadedConnections.Count,
                    ExpectedCount = Math.Max(1, Participants.Count),
                    FriendlyName = FriendlyName,
                    StatusName = StatusName,
                    IsDone = IsDone
                };
            }
        }

        private static readonly ConcurrentDictionary<string, State> Sessions =
            new ConcurrentDictionary<string, State>(StringComparer.OrdinalIgnoreCase);

        private static readonly TimeSpan StageReadyTimeout = TimeSpan.FromSeconds(25);

        public static Snapshot StartOrGet(string roomId, string sceneName,
                                          IEnumerable<int> participants, int hostConnectionId,
                                          out bool created)
        {
            State state = new State(sceneName, participants, hostConnectionId);
            State existing = Sessions.GetOrAdd(roomId, state);
            created = ReferenceEquals(existing, state);
            lock (existing.Sync) return existing.GetSnapshot();
        }

        public static bool IsInProgress(string roomId)
        {
            return !string.IsNullOrWhiteSpace(roomId) && Sessions.ContainsKey(roomId);
        }

        public static bool TryMarkReady(string roomId, int connectionId, int stageIndex,
                                        out Snapshot snapshot)
        {
            snapshot = null;
            if (!Sessions.TryGetValue(roomId, out State state)) return false;

            lock (state.Sync)
            {
                if (state.IsDone || !state.Participants.Contains(connectionId))
                    return false;

                // A joiner can complete its scene load just before the host's
                // first phase packet reaches the relay. Accept that one
                // authoritative level-loaded acknowledgement instead of
                // losing the client's one-shot ACK to a harmless race.
                if (stageIndex == 1 && state.StageIndex == 0)
                {
                    state.StageIndex = 1;
                    state.PhaseId = "LoadingLevels";
                }
                else if (stageIndex != state.StageIndex)
                {
                    return false;
                }

                state.ReadyConnections.Add(connectionId);
                if (stageIndex == 1) state.LoadedConnections.Add(connectionId);
                snapshot = state.GetSnapshot();
                return true;
            }
        }

        public static bool TryUpdatePhase(string roomId, int hostConnectionId, int stageIndex,
                                          string phaseId, float progress,
                                          string friendlyName, string statusName,
                                          out Snapshot snapshot)
        {
            snapshot = null;
            if (!Sessions.TryGetValue(roomId, out State state)) return false;

            lock (state.Sync)
            {
                if (state.IsDone || hostConnectionId != state.HostConnectionId ||
                    stageIndex < state.StageIndex)
                    return false;

                if (stageIndex > state.StageIndex)
                {
                    state.StageIndex = stageIndex;
                    state.ReadyConnections.Clear();
                    state.StageStartedUtc = DateTime.UtcNow;
                }

                state.PhaseId = string.IsNullOrWhiteSpace(phaseId) ? state.PhaseId : phaseId;
                if (stageIndex >= 1 && state.Participants.Contains(state.HostConnectionId))
                    state.LoadedConnections.Add(state.HostConnectionId);

                state.Progress = Math.Clamp(progress, -1f, 1f);
                state.FriendlyName = string.IsNullOrWhiteSpace(friendlyName)
                    ? state.FriendlyName : friendlyName;
                state.StatusName = string.IsNullOrWhiteSpace(statusName)
                    ? state.StatusName : statusName;
                snapshot = state.GetSnapshot();
                return true;
            }
        }

        public static bool TryComplete(string roomId, out Snapshot snapshot)
        {
            snapshot = null;
            if (!Sessions.TryGetValue(roomId, out State state)) return false;

            lock (state.Sync)
            {
                state.IsDone = true;
                snapshot = state.GetSnapshot();
                return true;
            }
        }

        public static List<ExpiredParticipants> ExpireUnready(DateTime utcNow)
        {
            var expiredRooms = new List<ExpiredParticipants>();
            foreach (KeyValuePair<string, State> pair in Sessions)
            {
                State state = pair.Value;
                lock (state.Sync)
                {
                    if (state.IsDone ||
                        state.LastTimedOutStageIndex >= state.StageIndex ||
                        utcNow - state.StageStartedUtc < StageReadyTimeout)
                        continue;

                    HashSet<int> readyForStage = state.StageIndex <= 1
                        ? state.LoadedConnections
                        : state.ReadyConnections;
                    int[] timedOut = state.Participants
                        .Where(id => id != state.HostConnectionId &&
                                     !readyForStage.Contains(id))
                        .ToArray();

                    state.LastTimedOutStageIndex = state.StageIndex;

                    foreach (int connectionId in timedOut)
                    {
                        state.Participants.Remove(connectionId);
                        state.ReadyConnections.Remove(connectionId);
                        state.LoadedConnections.Remove(connectionId);
                    }

                    if (timedOut.Length > 0)
                    {
                        expiredRooms.Add(new ExpiredParticipants
                        {
                            RoomId = pair.Key,
                            ConnectionIds = timedOut,
                            State = state.GetSnapshot()
                        });
                    }
                }
            }

            return expiredRooms;
        }

        public static bool RemoveParticipant(int connectionId, out string roomId,
                                             out Snapshot snapshot)
        {
            roomId = null;
            snapshot = null;

            foreach (KeyValuePair<string, State> pair in Sessions)
            {
                State state = pair.Value;
                lock (state.Sync)
                {
                    if (!state.Participants.Remove(connectionId)) continue;
                    state.ReadyConnections.Remove(connectionId);
                    state.LoadedConnections.Remove(connectionId);
                    roomId = pair.Key;
                    snapshot = state.GetSnapshot();
                    return true;
                }
            }

            return false;
        }

        public static void Remove(string roomId)
        {
            if (!string.IsNullOrWhiteSpace(roomId))
                Sessions.TryRemove(roomId, out _);
        }
    }
}
