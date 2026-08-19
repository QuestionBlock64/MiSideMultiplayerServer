using System;
using System.Collections.Generic;

namespace MiSideMultiplayer.RelayServer
{
    internal static partial class Program
    {
        private static void HandleInventoryClaim(ClientConnection connection, RelayEnvelope envelope)
        {
            InventoryRequestPayload request = DeserializeInventoryRequest(envelope.Payload);
            if (connection.ClientId == null || !IsValidInventoryRequest(request))
                return;

            string sceneName = request.sceneName.Trim();
            string itemPath = request.itemPath.Trim();
            bool approved;
            string ownerId;

            lock (InventoryStateLock)
            {
                InventoryItemState state = GetInventoryItemState(connection.RoomName, sceneName, itemPath);
                if (string.IsNullOrEmpty(state.OwnerId))
                {
                    state.OwnerId = connection.ClientId;
                    approved = true;
                }
                else
                {
                    approved = string.Equals(state.OwnerId, connection.ClientId, StringComparison.Ordinal);
                }
                ownerId = state.OwnerId;
            }

            BroadcastInventoryPayload(
                connection.RoomName,
                sceneName,
                InventoryClaimResultEventName,
                new InventoryClaimResultPayload
                {
                    sceneName = sceneName,
                    itemPath = itemPath,
                    ownerId = ownerId,
                    approved = approved
                });

            Logger.Player.Debug(
                "Inventory claim " + (approved ? "approved" : "denied") +
                " for '" + itemPath + "' from " + connection.ClientId + ".");
        }

        private static void HandleInventoryKeyAdded(ClientConnection connection, RelayEnvelope envelope)
        {
            InventoryRequestPayload change = DeserializeInventoryRequest(envelope.Payload);
            if (connection.ClientId == null || !IsValidInventoryRequest(change))
                return;

            string sceneName = change.sceneName.Trim();
            string itemPath = change.itemPath.Trim();
            bool accepted = false;

            lock (InventoryStateLock)
            {
                InventoryItemState state = GetInventoryItemState(connection.RoomName, sceneName, itemPath);
                if (string.IsNullOrEmpty(state.OwnerId))
                    state.OwnerId = connection.ClientId;

                if (!state.Consumed && string.Equals(state.OwnerId, connection.ClientId, StringComparison.Ordinal))
                {
                    state.HasKey = true;
                    accepted = true;
                }
            }

            if (!accepted) return;

            BroadcastInventoryPayload(
                connection.RoomName,
                sceneName,
                InventoryKeyAddedEventName,
                new InventoryKeyAddedPayload
                {
                    sceneName = sceneName,
                    itemPath = itemPath,
                    ownerId = connection.ClientId
                });
        }

        private static void HandleInventoryConsume(ClientConnection connection, RelayEnvelope envelope)
        {
            InventoryRequestPayload request = DeserializeInventoryRequest(envelope.Payload);
            if (connection.ClientId == null || !IsValidInventoryRequest(request))
                return;

            string sceneName = request.sceneName.Trim();
            string itemPath = request.itemPath.Trim();
            bool approved;

            lock (InventoryStateLock)
            {
                InventoryItemState state = GetInventoryItemState(connection.RoomName, sceneName, itemPath);
                if (string.IsNullOrEmpty(state.OwnerId))
                    state.OwnerId = connection.ClientId;

                approved = !state.Consumed;
                if (approved)
                {
                    state.HasKey = false;
                    state.Consumed = true;
                }
            }

            BroadcastInventoryPayload(
                connection.RoomName,
                sceneName,
                InventoryConsumeResultEventName,
                new InventoryConsumeResultPayload
                {
                    sceneName = sceneName,
                    itemPath = itemPath,
                    approved = approved
                });
        }

        private static void SendInventorySnapshot(ClientConnection connection)
        {
            if (connection == null || !IsGameScene(connection.SceneName))
                return;

            List<InventorySnapshotItemPayload> items = new List<InventorySnapshotItemPayload>();
            lock (InventoryStateLock)
            {
                Dictionary<string, InventoryItemState> sceneItems;
                if (InventoryStates.TryGetValue(GetInventoryStateKey(connection.RoomName, connection.SceneName), out sceneItems))
                {
                    foreach (KeyValuePair<string, InventoryItemState> pair in sceneItems)
                    {
                        InventoryItemState state = pair.Value;
                        items.Add(new InventorySnapshotItemPayload
                        {
                            itemPath = pair.Key,
                            ownerId = state.OwnerId,
                            hasKey = state.HasKey,
                            consumed = state.Consumed
                        });
                    }
                }
            }

            RelayEnvelope envelope = new RelayEnvelope
            {
                RoomName = connection.RoomName,
                SenderId = "SERVER",
                EventName = InventorySnapshotEventName,
                Payload = System.Text.Json.JsonSerializer.Serialize(new InventorySnapshotPayload
                {
                    sceneName = connection.SceneName,
                    items = items
                }),
                SceneName = connection.SceneName
            };
            connection.TrySend(System.Text.Json.JsonSerializer.Serialize(envelope));
        }

        private static void BroadcastInventoryPayload(string roomName, string sceneName, string eventName, object payload)
        {
            RelayEnvelope envelope = new RelayEnvelope
            {
                RoomName = roomName,
                SenderId = "SERVER",
                EventName = eventName,
                Payload = System.Text.Json.JsonSerializer.Serialize(payload),
                SceneName = sceneName
            };
            string line = System.Text.Json.JsonSerializer.Serialize(envelope);

            foreach (ClientConnection client in Clients.Values)
            {
                if (IsSameRoom(client.RoomName, roomName))
                    client.TrySend(line);
            }
        }

        private static InventoryRequestPayload DeserializeInventoryRequest(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload)) return null;
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<InventoryRequestPayload>(payload, EnvelopeJsonOptions);
            }
            catch
            {
                return null;
            }
        }

        private static bool IsValidInventoryRequest(InventoryRequestPayload request)
        {
            return request != null &&
                   !string.IsNullOrWhiteSpace(request.sceneName) &&
                   !string.IsNullOrWhiteSpace(request.itemPath) &&
                   request.sceneName.Length <= 128 &&
                   request.itemPath.Length <= 1024 &&
                   IsGameScene(request.sceneName);
        }

        private static InventoryItemState GetInventoryItemState(string roomName, string sceneName, string itemPath)
        {
            string stateKey = GetInventoryStateKey(roomName, sceneName);
            Dictionary<string, InventoryItemState> sceneItems;
            if (!InventoryStates.TryGetValue(stateKey, out sceneItems))
            {
                sceneItems = new Dictionary<string, InventoryItemState>(StringComparer.Ordinal);
                InventoryStates[stateKey] = sceneItems;
            }

            InventoryItemState state;
            if (!sceneItems.TryGetValue(itemPath, out state))
            {
                state = new InventoryItemState();
                sceneItems[itemPath] = state;
            }

            return state;
        }

        private static string GetInventoryStateKey(string roomName, string sceneName)
        {
            return NormalizeRoomName(roomName) + "\u001f" + (sceneName ?? string.Empty).Trim();
        }

        private static void ClearInventoryIfRoomIsEmpty(string roomName)
        {
            foreach (ClientConnection client in Clients.Values)
            {
                if (IsSameRoom(client.RoomName, roomName))
                    return;
            }

            string prefix = NormalizeRoomName(roomName) + "\u001f";
            lock (InventoryStateLock)
            {
                List<string> emptyRoomKeys = new List<string>();
                foreach (string stateKey in InventoryStates.Keys)
                {
                    if (stateKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        emptyRoomKeys.Add(stateKey);
                }
                for (int i = 0; i < emptyRoomKeys.Count; i++)
                    InventoryStates.Remove(emptyRoomKeys[i]);
            }
        }
    }
}
