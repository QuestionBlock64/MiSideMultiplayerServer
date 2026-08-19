using System.Collections.Generic;

namespace MiSideMultiplayer.RelayServer
{
    internal sealed class InventoryRequestPayload
    {
        public string sceneName { get; set; }
        public string itemPath { get; set; }
    }

    internal sealed class InventoryClaimResultPayload
    {
        public string sceneName { get; set; }
        public string itemPath { get; set; }
        public string ownerId { get; set; }
        public bool approved { get; set; }
    }

    internal sealed class InventoryKeyAddedPayload
    {
        public string sceneName { get; set; }
        public string itemPath { get; set; }
        public string ownerId { get; set; }
    }

    internal sealed class InventoryConsumeResultPayload
    {
        public string sceneName { get; set; }
        public string itemPath { get; set; }
        public bool approved { get; set; }
    }

    internal sealed class InventorySnapshotPayload
    {
        public string sceneName { get; set; }
        public List<InventorySnapshotItemPayload> items { get; set; }
    }

    internal sealed class InventorySnapshotItemPayload
    {
        public string itemPath { get; set; }
        public string ownerId { get; set; }
        public bool hasKey { get; set; }
        public bool consumed { get; set; }
    }

    internal sealed class InventoryItemState
    {
        public string OwnerId;
        public bool HasKey;
        public bool Consumed;
    }
}
