namespace MiSideMultiplayer.RelayServer
{
    internal enum QBRelayLobbyMode
    {
        DirectConnect = 1
    }

    /// <summary>
    /// Protocol gate for the distributable Direct Connect relay. This build is
    /// permanently Direct Connect-only; no command-line argument or console
    /// command can enable Public/Private directory behavior.
    /// </summary>
    internal static class QBRelayModeController
    {
        public static QBRelayLobbyMode Mode { get { return QBRelayLobbyMode.DirectConnect; } }
        public static string ModeName { get { return "directconnect"; } }

        public static void Configure(string[] args)
        {
            // Intentionally ignore mode flags: this binary has one protocol mode.
        }

        public static bool AllowsVisibility(int visibility, string roomId, string code,
            out string reason)
        {
            if (visibility != 2)
            {
                reason = "server_mode_mismatch";
                return false;
            }
            if (!string.Equals(roomId, "default", System.StringComparison.OrdinalIgnoreCase))
            {
                reason = "direct_room_must_be_default";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(code))
            {
                reason = "direct_code_not_supported";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        public static bool TrySetMode(QBRelayLobbyMode requested, out string reason)
        {
            reason = "Cannot do that in this current environment.";
            return false;
        }
    }
}
