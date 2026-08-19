using System;
using System.Linq;
using System.Net;

namespace MiSideMultiplayer.RelayServer
{
    /// <summary>
    /// Ban list backed by Settings.Instance.Bans, persisted to settings.json.
    /// Bans check both IP address (always available from the TCP connection) and an
    /// optional client-supplied ID (once the mod sends one - see ClientId on ClientConnection).
    /// </summary>
    internal static class BanManager
    {
        private static readonly Logger Log = new Logger("Bans", ConsoleColor.DarkRed);

        public static bool IsIpBanned(IPAddress address)
        {
            string text = address.ToString();
            return Settings.Instance.Bans.BannedIps.Contains(text);
        }

        public static bool IsIdBanned(string? clientId)
        {
            if (string.IsNullOrEmpty(clientId))
                return false;

            return Settings.Instance.Bans.BannedIds.Contains(clientId);
        }

        public static bool BanIp(string ipText)
        {
            if (!IPAddress.TryParse(ipText, out _))
            {
                Log.Warn("Refused to ban '" + ipText + "' - not a valid IP address.");
                return false;
            }

            if (Settings.Instance.Bans.BannedIps.Contains(ipText))
            {
                Log.Info("IP " + ipText + " is already banned.");
                return false;
            }

            Settings.Instance.Bans.BannedIps.Add(ipText);
            Settings.Save();
            Log.Info("Banned IP " + ipText + ".");
            return true;
        }

        public static bool UnbanIp(string ipText)
        {
            bool removed = Settings.Instance.Bans.BannedIps.RemoveAll(ip => ip == ipText) > 0;
            if (removed)
            {
                Settings.Save();
                Log.Info("Unbanned IP " + ipText + ".");
            }
            else
            {
                Log.Info("IP " + ipText + " was not in the ban list.");
            }

            return removed;
        }

        public static bool BanId(string clientId)
        {
            if (string.IsNullOrWhiteSpace(clientId))
            {
                Log.Warn("Refused to ban an empty client ID.");
                return false;
            }

            if (Settings.Instance.Bans.BannedIds.Contains(clientId))
            {
                Log.Info("Client ID " + clientId + " is already banned.");
                return false;
            }

            Settings.Instance.Bans.BannedIds.Add(clientId);
            Settings.Save();
            Log.Info("Banned client ID " + clientId + ".");
            return true;
        }

        public static bool UnbanId(string clientId)
        {
            bool removed = Settings.Instance.Bans.BannedIds.RemoveAll(id => id == clientId) > 0;
            if (removed)
            {
                Settings.Save();
                Log.Info("Unbanned client ID " + clientId + ".");
            }
            else
            {
                Log.Info("Client ID " + clientId + " was not in the ban list.");
            }

            return removed;
        }

        public static void ListBans()
        {
            var ips = Settings.Instance.Bans.BannedIps;
            var ids = Settings.Instance.Bans.BannedIds;

            if (ips.Count == 0 && ids.Count == 0)
            {
                Log.Info("No active bans.");
                return;
            }

            if (ips.Count > 0)
                Log.Info("Banned IPs: " + string.Join(", ", ips));

            if (ids.Count > 0)
                Log.Info("Banned client IDs: " + string.Join(", ", ids));
        }
    }
}
