namespace MiSideMultiplayer.RelayServer
{
    internal static class EventNames
    {
        // Client → Server
        public const string TransportHello     = "miside.transport.hello";

        public const string SessionRegister    = "miside.session.register";
        public const string SessionUnregister  = "miside.session.unregister";
        public const string SessionList        = "miside.session.list";
        public const string SessionResolve     = "miside.session.resolve";
        public const string SessionJoin        = "miside.session.join";

        // Server → Client
        public const string SessionResponse     = "miside.session.response";
        public const string SessionPlayerJoined = "miside.session.player.joined";
        public const string SessionPlayerLeft   = "miside.session.player.left";
        public const string SessionHostLeft     = "miside.session.host.left";

        // Game traffic that just gets relayed (server doesn't handle them)
        public const string PlayerState        = "miside.player.state";
        public const string MitaState          = "miside.mita.state";
        public const string MitaRpc            = "miside.mita.rpc";
        public const string MitaSyncRequest    = "miside.mita.sync.request";
        public const string MitaSyncComplete   = "miside.mita.sync.complete";
        public const string ChatMessage        = "miside.chat.message";
        public const string ChatSystem         = "miside.chat.system";

        // Session-internal events that also just get relayed (broadcast to room)
        public const string SessionHello       = "miside.session.hello";
        public const string SessionReady       = "miside.session.ready";
        public const string SessionScene       = "miside.session.scene";
        public const string SessionStart       = "miside.session.start";
        public const string SessionSceneLoaded = "miside.session.sceneloaded";
        public const string LoadingClientReady = "miside.loading.clientReady";
        public const string LoadingSyncDoneAck = "miside.loading.ackSyncDone";
        public const string LoadingPhase       = "miside.loading.phase";
        public const string LoadingDone        = "miside.loading.done";
        public const string LoadingParticipantDropped = "miside.loading.participantDropped";
    }
}
