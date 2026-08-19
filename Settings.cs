using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiSideMultiplayer.RelayServer
{
    internal sealed class Settings
    {
        private static readonly Logger Log = new Logger("Settings", ConsoleColor.Green);
        private static readonly string SettingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        public static Settings Instance { get; private set; } = new Settings();

        public ServerTable Server { get; set; } = new ServerTable();
        public BansTable Bans { get; set; } = new BansTable();
        public DiscordTable Discord { get; set; } = new DiscordTable();
        public LoggingTable Logging { get; set; } = new LoggingTable();
        public DeathLinkTable DeathLink { get; set; } = new DeathLinkTable();

        // ── OP list ────────────────────────────────────────────────────────
        public List<string> OppedIds { get; set; } = new List<string>();

        public sealed class ServerTable
        {
            public string Address { get; set; } = "0.0.0.0";
            public int Port { get; set; } = 7777;
            public int MaxClients { get; set; } = 32;
        }

        public sealed class BansTable
        {
            public List<string> BannedIps { get; set; } = new List<string>();
            public List<string> BannedIds { get; set; } = new List<string>();
        }

        public sealed class DiscordTable
        {
            public string? WebhookUrl { get; set; } = null;
            public string MinimumLevel { get; set; } = "Info";
        }

        public sealed class LoggingTable
        {
            public string MinimumLevel { get; set; } = "Info";
        }

        public sealed class DeathLinkTable
        {
            public bool Enabled { get; set; } = false;
        }

        public static void Load()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                {
                    Log.Info("No settings.json found, creating one with default values at " + SettingsPath);
                    Instance = new Settings();
                    Save();
                    return;
                }

                string json = File.ReadAllText(SettingsPath);
                Settings? loaded = JsonSerializer.Deserialize<Settings>(json, JsonOptions);

                if (loaded == null)
                {
                    Log.Error("settings.json parsed to null. Keeping previous settings in memory.");
                    return;
                }

                Instance = loaded;
                Log.Info("Loaded settings.json.");
            }
            catch (JsonException ex)
            {
                Log.Error("settings.json is malformed and could not be parsed. Keeping previous settings in memory.", ex);
            }
            catch (IOException ex)
            {
                Log.Error("Failed to read settings.json.", ex);
            }
        }

        public static void Save()
        {
            try
            {
                string json = JsonSerializer.Serialize(Instance, JsonOptions);
                File.WriteAllText(SettingsPath, json);
            }
            catch (IOException ex)
            {
                Log.Error("Failed to write settings.json.", ex);
            }
        }

        public static LogLevel ParseLogLevel(string value, LogLevel fallback)
        {
            return Enum.TryParse(value, true, out LogLevel parsed) ? parsed : fallback;
        }
    }
}