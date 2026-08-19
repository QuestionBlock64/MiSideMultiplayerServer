using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace MiSideMultiplayer.RelayServer
{
    internal static partial class Program
    {
        private const string ServerVersion = "1.5.0";

        private static readonly Logger Log = Logger.Server;

        private static readonly ConcurrentDictionary<int, ClientConnection> Clients =
            new ConcurrentDictionary<int, ClientConnection>();

        private static int nextClientId;
        private static volatile bool isRunning = true;
        private static long totalBytesRelayed;
        private static long totalMessagesRelayed;
        private static readonly object CommandExecutionLock = new object();

        private static readonly object InventoryStateLock = new object();
        private static readonly Dictionary<string, Dictionary<string, InventoryItemState>> InventoryStates =
            new Dictionary<string, Dictionary<string, InventoryItemState>>(StringComparer.OrdinalIgnoreCase);
        // Event names
        private const string HelloEventName = "miside.transport.hello";
        private const string PlayerStateEventName = "miside.player.state";
        private const string MitaStateEventName = "miside.mita.state";
        private const string DeathLinkEventName = "miside.deathlink";
        private const string ChatEventName = "miside.chat.message";      // must match mod
        private const string ServerResponseEventName = "miside.server.response";

        private const string InventoryClaimRequestEventName = "miside.inventory.claim.request";
        private const string InventoryClaimResultEventName = "miside.inventory.claim.result";
        private const string InventoryKeyAddedEventName = "miside.inventory.key.add";
        private const string InventoryConsumeRequestEventName = "miside.inventory.consume.request";
        private const string InventoryConsumeResultEventName = "miside.inventory.consume.result";
        private const string InventorySnapshotEventName = "miside.inventory.snapshot";
        private static int Main(string[] args)
        {
            Settings.Load();
            Logger.MinimumLevel = Settings.ParseLogLevel(Settings.Instance.Logging.MinimumLevel, LogLevel.Info);

            if (HasFlag(args, "--verbose", "-v"))
                Logger.MinimumLevel = LogLevel.Debug;

            int port = Settings.Instance.Server.Port;
            IPAddress bindAddress = ParseBindAddress(Settings.Instance.Server.Address);
            TcpListener listener = new TcpListener(bindAddress, port);
            Stopwatch uptime = Stopwatch.StartNew();

            AppDomain.CurrentDomain.UnhandledException += delegate (object? sender, UnhandledExceptionEventArgs eventArgs)
            {
                Exception? ex = eventArgs.ExceptionObject as Exception;
                if (ex != null)
                    Log.Fatal("Unhandled exception on the process domain.", ex);
                else
                    Log.Fatal("Unhandled non-exception error object: " + eventArgs.ExceptionObject, new Exception("Unknown"));
            };

            Console.CancelKeyPress += delegate (object? sender, ConsoleCancelEventArgs eventArgs)
            {
                eventArgs.Cancel = true;
                Log.Info("Shutdown signal received (Ctrl+C). Closing listener and disconnecting clients...");
                isRunning = false;
                listener.Stop();
            };

            RegisterConsoleCommands(listener);
            CommandHandler.Start(() => isRunning);
            DiscordBot.Start();

            try
            {
                listener.Start();

                Log.Info("========================================");
                Log.Info(" MiSide Multiplayer Relay Server v" + ServerVersion);
                Log.Info("========================================");
                Log.Info("Listening on " + bindAddress + ":" + port + " (TCP)");
                Log.Info("Max clients: " + Settings.Instance.Server.MaxClients);
                Log.Info("Death Link: " + (Settings.Instance.DeathLink.Enabled ? "ON" : "OFF"));
                Log.Info("OP'd players: " + (Settings.Instance.OppedIds.Count > 0 ? string.Join(", ", Settings.Instance.OppedIds) : "none"));
                Log.Info("Type 'help' for a list of console commands.");

                while (isRunning)
                {
                    TcpClient tcpClient;

                    try
                    {
                        tcpClient = listener.AcceptTcpClient();
                    }
                    catch (SocketException) when (!isRunning)
                    {
                        break;
                    }
                    catch (ObjectDisposedException) when (!isRunning)
                    {
                        break;
                    }

                    HandleNewConnection(tcpClient);
                }

                return 0;
            }
            catch (SocketException ex)
            {
                if (isRunning)
                {
                    Log.Fatal("Failed to bind or accept on " + bindAddress + ":" + port + ". Is another instance already running, or is the port blocked?", ex);
                    return 1;
                }

                return 0;
            }
            catch (Exception ex)
            {
                Log.Fatal("Unexpected fatal error in the server's main loop.", ex);
                return 1;
            }
            finally
            {
                isRunning = false;
                listener.Stop();

                int remaining = Clients.Count;
                if (remaining > 0)
                    Log.Info("Disconnecting " + remaining + " remaining client(s)...");

                foreach (ClientConnection client in Clients.Values)
                    client.Dispose();

                Clients.Clear();

                uptime.Stop();
                Log.Info("Server stopped after " + FormatDuration(uptime.Elapsed) +
                          ". Relayed " + totalMessagesRelayed + " message(s), " + FormatBytes(totalBytesRelayed) + " total.");

                DiscordBot.Stop();
            }
        }

        private static void HandleNewConnection(TcpClient tcpClient)
        {
            tcpClient.NoDelay = true;

            IPAddress? remoteAddress = (tcpClient.Client.RemoteEndPoint as IPEndPoint)?.Address;

            if (remoteAddress != null && BanManager.IsIpBanned(remoteAddress))
            {
                Logger.Player.Info("Rejected connection from " + remoteAddress + " - IP is banned.");
                tcpClient.Close();
                return;
            }

            int maxClients = Settings.Instance.Server.MaxClients;
            if (maxClients > 0 && Clients.Count >= maxClients)
            {
                Logger.Player.Info("Rejected connection from " + DescribeEndpoint(tcpClient) + " - server is full (" + maxClients + " max).");
                tcpClient.Close();
                return;
            }

            int id = Interlocked.Increment(ref nextClientId);
            ClientConnection connection = new ClientConnection(id, tcpClient);
            Clients[id] = connection;

            string remote = DescribeEndpoint(tcpClient);
            Logger.Player.Info("Client #" + id + " connected from " + remote + ". (" + Clients.Count + " total online)");

            ThreadPool.QueueUserWorkItem(delegate { HandleClient(connection); });
        }

        private static void HandleClient(ClientConnection connection)
        {
            string remote = DescribeEndpoint(connection.TcpClient);
            Stopwatch sessionTimer = Stopwatch.StartNew();

            try
            {
                using (NetworkStream stream = connection.TcpClient.GetStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true))
                {
                    writer.NewLine = "\n";
                    writer.AutoFlush = true;
                    connection.AttachWriter(writer);

                    bool loggedFirstLine = false;

                    while (isRunning && connection.TcpClient.Connected)
                    {
                        string? line = reader.ReadLine();
                        if (line == null)
                            break;

                        if (!loggedFirstLine)
                        {
                            loggedFirstLine = true;
                            Logger.Player.Debug("Client #" + connection.Id + " (" + remote + ") first line received (raw): " + line);
                        }

                        RelayEnvelope? envelope = TryParseEnvelope(line);

                        if (envelope == null)
                        {
                            // Not a valid envelope – still relay as-is (backward compat)
                            Broadcast(connection, line, null);
                            continue;
                        }

                        if (envelope.EventName == HelloEventName)
                        {
                            HandleHello(connection, envelope, remote);
                            continue;
                        }

                        if (connection.ClientId == null && !string.IsNullOrEmpty(envelope.SenderId))
                            TryAdoptIdentity(connection, envelope, remote);

                        // A connection has one room for its lifetime. Do not allow
                        // later packets to jump to another room by changing the envelope.
                        if (!IsSameRoom(envelope.RoomName, connection.RoomName))
                        {
                            Logger.Player.Warn("Dropped packet from #" + connection.Id + " with a mismatched room.");
                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(envelope.DisplayName) &&
                            !string.IsNullOrEmpty(connection.ClientId))
                            connection.PlayerName = NormalizeDisplayName(envelope.DisplayName, connection.ClientId);

                        UpdateConnectionScene(connection, envelope);

                        // ── Death Link filter ─────────────────────────────
                        if (envelope.EventName == InventoryClaimRequestEventName)
                        {
                            HandleInventoryClaim(connection, envelope);
                            continue;
                        }
                        if (envelope.EventName == InventoryKeyAddedEventName)
                        {
                            HandleInventoryKeyAdded(connection, envelope);
                            continue;
                        }
                        if (envelope.EventName == InventoryConsumeRequestEventName)
                        {
                            HandleInventoryConsume(connection, envelope);
                            continue;
                        }
                        if (envelope.EventName == DeathLinkEventName && !Settings.Instance.DeathLink.Enabled)
                        {
                            Logger.Player.Debug("Death Link disabled – dropping event from #" + connection.Id);
                            continue;
                        }

                        // ── Chat and OP command handling ───────────────────
                        if (envelope.EventName == ChatEventName && !string.IsNullOrEmpty(envelope.Payload))
                        {
                            try
                            {
                                var chatPayload = System.Text.Json.JsonSerializer.Deserialize<ChatPayload>(envelope.Payload);
                                if (chatPayload == null || string.IsNullOrWhiteSpace(chatPayload.text))
                                    continue;

                                if (chatPayload.text.Length > 512)
                                    chatPayload.text = chatPayload.text.Substring(0, 512);

                                if (chatPayload.text.StartsWith("/"))
                                {
                                    // It's a command – only process if the player is OP
                                    if (!connection.IsOp)
                                    {
                                        SendServerResponse(connection, "You don't have permission to use server commands.", "red");
                                        continue; // don't broadcast
                                    }

                                    string commandLine = chatPayload.text.Substring(1).Trim();
                                    if (string.IsNullOrEmpty(commandLine)) continue;

                                    ExecuteCommandForClient(connection, commandLine);

                                    continue; // don't broadcast the chat line
                                }
                            }
                            catch
                            {
                                SendServerResponse(connection, "Invalid chat packet.", "red");
                                continue;
                            }
                        }

                        if (connection.ClientId != null && BanManager.IsIdBanned(connection.ClientId))
                        {
                            Logger.Player.Warn("Client #" + connection.Id + " (" + DescribeClient(connection, remote) + ") is banned. Disconnecting.");
                            break;
                        }

                        Broadcast(connection, line, envelope.EventName);
                    }
                }
            }
            catch (IOException ex)
            {
                Logger.Player.Warn("Client #" + connection.Id + " (" + DescribeClient(connection, remote) + ") connection dropped: " + ex.Message);
            }
            catch (SocketException ex)
            {
                Logger.Player.Warn("Client #" + connection.Id + " (" + DescribeClient(connection, remote) + ") socket error: " + ex.Message);
            }
            catch (Exception ex)
            {
                Log.Error("Unexpected error while handling client #" + connection.Id + " (" + DescribeClient(connection, remote) + ").", ex);
            }
            finally
            {
                if (connection.JoinAnnounced)
                    BroadcastSystemMessage(connection, connection.PlayerName + " left the game.", connection.SceneName);

                ClientConnection? removed;
                Clients.TryRemove(connection.Id, out removed);
                connection.Dispose();
                ClearInventoryIfRoomIsEmpty(connection.RoomName);
                sessionTimer.Stop();

                Logger.Player.Info("Client #" + connection.Id + " (" + DescribeClient(connection, remote) + ") disconnected. " +
                          "Session duration: " + FormatDuration(sessionTimer.Elapsed) +
                          ". (" + Clients.Count + " remaining online)");
            }
        }

        // ── Envelope / Chat Payload ────────────────────────────────────────
        private sealed class RelayEnvelope
        {
            public string? RoomName { get; set; }
            public string? SenderId { get; set; }
            public string? EventName { get; set; }
            public string? Payload { get; set; }
            public string? DisplayName { get; set; }
            public string? SceneName { get; set; }
        }

        private sealed class ChatPayload
        {
            public string? senderId { get; set; }
            public string? displayName { get; set; }
            public string? sceneName { get; set; }
            public string? text { get; set; }
        }

        private sealed class PlayerStatePayload
        {
            public string? sceneName { get; set; }
        }

        private static readonly System.Text.Json.JsonSerializerOptions EnvelopeJsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private static RelayEnvelope? TryParseEnvelope(string line)
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<RelayEnvelope>(line, EnvelopeJsonOptions);
            }
            catch
            {
                return null;
            }
        }

        private static void HandleHello(ClientConnection connection, RelayEnvelope envelope, string remote)
        {
            if (string.IsNullOrWhiteSpace(envelope.SenderId))
            {
                Logger.Player.Warn("Client #" + connection.Id + " (" + remote + ") sent hello with no senderId.");
                return;
            }

            if (BanManager.IsIdBanned(envelope.SenderId))
            {
                Logger.Player.Warn("Client #" + connection.Id + " (" + remote + ") is banned. Disconnecting.");
                connection.Dispose();
                return;
            }

            connection.ClientId = envelope.SenderId;
            connection.RoomName = NormalizeRoomName(envelope.RoomName);
            connection.PlayerName = NormalizeDisplayName(envelope.DisplayName, envelope.SenderId);
            connection.SceneName = envelope.SceneName;
            connection.IsOp = Settings.Instance.OppedIds.Contains(envelope.SenderId);
            TryAnnounceJoin(connection);

            SendInventorySnapshot(connection);
            Logger.Player.Info("Client #" + connection.Id + " identified as " + DescribeClient(connection, remote) +
                               (connection.IsOp ? " (OP)" : "") + ".");
        }

        private static void TryAdoptIdentity(ClientConnection connection, RelayEnvelope envelope, string remote)
        {
            if (string.IsNullOrWhiteSpace(envelope.SenderId)) return;
            if (BanManager.IsIdBanned(envelope.SenderId)) return;

            connection.ClientId = envelope.SenderId;
            connection.RoomName = NormalizeRoomName(envelope.RoomName);
            connection.PlayerName = NormalizeDisplayName(envelope.DisplayName, envelope.SenderId);
            connection.SceneName = envelope.SceneName;
            connection.IsOp = Settings.Instance.OppedIds.Contains(envelope.SenderId);
            TryAnnounceJoin(connection);

            SendInventorySnapshot(connection);
            Logger.Player.Info("Client #" + connection.Id + " identified as " + DescribeClient(connection, remote) +
                               " (from state packet)" + (connection.IsOp ? " (OP)" : "") + ".");
        }

        private static void UpdateConnectionScene(ClientConnection connection, RelayEnvelope envelope)
        {
            if (envelope.EventName != PlayerStateEventName || string.IsNullOrWhiteSpace(envelope.Payload))
                return;

            try
            {
                PlayerStatePayload? state = System.Text.Json.JsonSerializer.Deserialize<PlayerStatePayload>(envelope.Payload);
                if (state == null || string.IsNullOrWhiteSpace(state.sceneName))
                    return;

                string previousScene = connection.SceneName;
                connection.SceneName = state.sceneName;
                if (!string.Equals(previousScene, connection.SceneName, StringComparison.OrdinalIgnoreCase))
                    SendInventorySnapshot(connection);
                TryAnnounceJoin(connection);
            }
            catch
            {
                // A malformed state packet is handled by the recipient as before;
                // it simply cannot be used to determine a join/leave scene.
            }
        }

        private static void TryAnnounceJoin(ClientConnection connection)
        {
            if (connection.JoinAnnounced || !IsGameScene(connection.SceneName))
                return;

            connection.JoinAnnounced = true;
            BroadcastSystemMessage(connection, connection.PlayerName + " joined the game.", connection.SceneName);
        }

        private static bool IsSameRoom(string? first, string? second)
        {
            return string.Equals(NormalizeRoomName(first), NormalizeRoomName(second), StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeRoomName(string? roomName)
        {
            return string.IsNullOrWhiteSpace(roomName) ? "default" : roomName.Trim();
        }

        private static string NormalizeDisplayName(string? displayName, string fallback)
        {
            string result = string.IsNullOrWhiteSpace(displayName) ? fallback : displayName.Trim();
            return result.Length > 48 ? result.Substring(0, 48) : result;
        }

        private static bool IsGameScene(string? sceneName)
        {
            return !string.IsNullOrWhiteSpace(sceneName) &&
                   !string.Equals(sceneName, "SceneMenu", StringComparison.OrdinalIgnoreCase);
        }

        private static string DescribeClient(ClientConnection connection, string remote)
        {
            if (connection.PlayerName != null)
                return connection.PlayerName + ", " + remote;
            return remote;
        }

        // ── Broadcasting ───────────────────────────────────────────────────
        private static void Broadcast(ClientConnection sender, string line, string? eventName)
        {
            bool isMitaSync = eventName == MitaStateEventName;
            bool isDeathLink = eventName == DeathLinkEventName;

            int delivered = 0, failed = 0;
            foreach (ClientConnection client in Clients.Values)
            {
                if (client.Id == sender.Id || !IsSameRoom(client.RoomName, sender.RoomName)) continue;
                if (client.TrySend(line)) delivered++;
                else failed++;
            }

            Interlocked.Increment(ref totalMessagesRelayed);
            Interlocked.Add(ref totalBytesRelayed, Encoding.UTF8.GetByteCount(line));

            if (isMitaSync)
            {
                if (failed > 0) Logger.Mita.Warn("Mita sync failed to " + failed + " client(s).");
                Logger.Mita.Debug("Relayed Mita sync from #" + sender.Id + " to " + delivered + " client(s).");
            }
            else if (isDeathLink)
            {
                Logger.Player.Info("Death Link: broadcast from #" + sender.Id + " to " + delivered + " client(s).");
            }
            else
            {
                if (failed > 0) Logger.Player.Warn("Failed to deliver message from #" + sender.Id + " to " + failed + " client(s).");
                Logger.Player.Debug("Relayed message from #" + sender.Id + " to " + delivered + " client(s).");
            }
        }

        private static void BroadcastSystemMessage(ClientConnection sender, string text, string? sceneName)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(sceneName))
                return;

            string payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                text,
                color = "yellow",
                sceneName
            });
            string line = System.Text.Json.JsonSerializer.Serialize(new RelayEnvelope
            {
                RoomName = sender.RoomName,
                SenderId = "SERVER",
                EventName = "miside.chat.system",
                Payload = payload,
                SceneName = sceneName
            });

            foreach (ClientConnection client in Clients.Values)
            {
                if (client.Id == sender.Id ||
                    !IsSameRoom(client.RoomName, sender.RoomName) ||
                    !string.Equals(client.SceneName, sceneName, StringComparison.OrdinalIgnoreCase))
                    continue;
                client.TrySend(line);
            }
        }

        // ── Send a private message to a specific client ───────────────────
        private static void SendServerResponse(ClientConnection client, string text, string color)
        {
            var response = new { text, color };
            string json = System.Text.Json.JsonSerializer.Serialize(response);
            var envelope = new RelayEnvelope
            {
                RoomName = client.RoomName,
                SenderId = "SERVER",
                EventName = ServerResponseEventName,
                Payload = json
            };
            string line = System.Text.Json.JsonSerializer.Serialize(envelope);
            client.TrySend(line);
        }

        private static void ExecuteCommandForClient(ClientConnection client, string commandLine)
        {
            var captured = new List<(string text, string color)>();

            lock (CommandExecutionLock)
            {
                LogHandler captureHandler = (level, src, msg) =>
                {
                    if (level < LogLevel.Info) return;
                    string color = level switch
                    {
                        LogLevel.Warn => "yellow",
                        LogLevel.Error => "red",
                        LogLevel.Fatal => "darkred",
                        _ => "white"
                    };
                    captured.Add((msg, color));
                };

                Logger.SetCurrentThreadLogHandler(captureHandler);
                try
                {
                    string[] parts = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    string commandName = parts[0];
                    string[] commandArgs = parts.Length > 1 ? parts[1..] : Array.Empty<string>();

                    if (!CommandHandler.TryExecute(commandName, commandArgs))
                        captured.Add(("Unknown command: " + commandName, "red"));
                }
                finally
                {
                    Logger.ClearCurrentThreadLogHandler(captureHandler);
                }
            }

            if (captured.Count == 0)
                captured.Add(("Command completed.", "white"));

            foreach (var (text, color) in captured)
                SendServerResponse(client, text, color);
        }

        // ── Console Commands ───────────────────────────────────────────────
        private static void RegisterConsoleCommands(TcpListener listener)
        {
            CommandHandler.RegisterCommand("status", "Shows server uptime and connection stats.", _ =>
            {
                Log.Info("Clients online: " + Clients.Count + " / " + (Settings.Instance.Server.MaxClients > 0 ? Settings.Instance.Server.MaxClients.ToString() : "unlimited"));
                Log.Info("Messages relayed: " + totalMessagesRelayed + " (" + FormatBytes(totalBytesRelayed) + ")");
                Log.Info("Death Link: " + (Settings.Instance.DeathLink.Enabled ? "ON" : "OFF"));
                Log.Info("OP'd players: " + (Settings.Instance.OppedIds.Count > 0 ? string.Join(", ", Settings.Instance.OppedIds) : "none"));
            });

            CommandHandler.RegisterCommand("list", "Lists currently connected clients.", _ =>
            {
                if (Clients.IsEmpty) { Log.Info("No clients connected."); return; }
                foreach (var client in Clients.Values)
                    Log.Info("  #" + client.Id + "  " +
                              (client.PlayerName ?? "?") + "  " +
                              DescribeEndpoint(client.TcpClient) +
                              (client.ClientId != null ? "  id=" + client.ClientId : "") +
                              (client.IsOp ? " [OP]" : ""));
            });

            CommandHandler.RegisterCommand("kick", "Usage: kick <clientId>", cmdArgs =>
            {
                if (cmdArgs.Length < 1 || !int.TryParse(cmdArgs[0], out int targetId)) { Log.Warn("Usage: kick <clientId>"); return; }
                if (Clients.TryGetValue(targetId, out var target))
                {
                    Logger.Player.Info("Kicking client #" + targetId + " (" + DescribeEndpoint(target.TcpClient) + ").");
                    target.Dispose();
                }
                else Log.Warn("No connected client with ID " + targetId + ".");
            });

            CommandHandler.RegisterCommand("ban", "Usage: ban ip <address> | ban id <clientId>", cmdArgs =>
            {
                if (cmdArgs.Length < 2) { Log.Warn("Usage: ban ip <address> | ban id <clientId>"); return; }
                if (cmdArgs[0].Equals("ip", StringComparison.OrdinalIgnoreCase)) BanManager.BanIp(cmdArgs[1]);
                else if (cmdArgs[0].Equals("id", StringComparison.OrdinalIgnoreCase)) BanManager.BanId(cmdArgs[1]);
                else Log.Warn("Usage: ban ip <address> | ban id <clientId>");
            });

            CommandHandler.RegisterCommand("unban", "Usage: unban ip <address> | unban id <clientId>", cmdArgs =>
            {
                if (cmdArgs.Length < 2) { Log.Warn("Usage: unban ip <address> | unban id <clientId>"); return; }
                if (cmdArgs[0].Equals("ip", StringComparison.OrdinalIgnoreCase)) BanManager.UnbanIp(cmdArgs[1]);
                else if (cmdArgs[0].Equals("id", StringComparison.OrdinalIgnoreCase)) BanManager.UnbanId(cmdArgs[1]);
                else Log.Warn("Usage: unban ip <address> | unban id <clientId>");
            });

            CommandHandler.RegisterCommand("bans", "Lists all active bans.", _ => BanManager.ListBans());

            CommandAction setSharedLife = cmdArgs =>
            {
                if (cmdArgs.Length != 1 ||
                    (!cmdArgs[0].Equals("on", StringComparison.OrdinalIgnoreCase) &&
                     !cmdArgs[0].Equals("off", StringComparison.OrdinalIgnoreCase)))
                {
                    Log.Warn("Usage: sharedlife on|off");
                    return;
                }

                bool enable = cmdArgs[0].Equals("on", StringComparison.OrdinalIgnoreCase);
                Settings.Instance.DeathLink.Enabled = enable;
                Settings.Save();
                Log.Info("Shared life " + (enable ? "enabled" : "disabled") + ".");
            };
            CommandHandler.RegisterCommand("sharedlife", "Usage: sharedlife on|off", setSharedLife);
            CommandHandler.RegisterCommand("deathlink", "Alias for sharedlife on|off", setSharedLife);

            CommandHandler.RegisterCommand("op", "Usage: op <name|id>", cmdArgs =>
            {
                if (cmdArgs.Length < 1) { Log.Warn("Usage: op <name|id>"); return; }
                string target = string.Join(" ", cmdArgs);
                var client = FindClient(target);
                if (client == null) { Log.Warn("No connected player matching '" + target + "'."); return; }
                if (string.IsNullOrEmpty(client.ClientId)) { Log.Warn("That client hasn't identified yet."); return; }

                if (Settings.Instance.OppedIds.Contains(client.ClientId))
                {
                    Log.Info(client.PlayerName + " is already an operator.");
                    return;
                }
                Settings.Instance.OppedIds.Add(client.ClientId);
                client.IsOp = true;
                Settings.Save();
                Log.Info("Opped " + client.PlayerName + ".");
                SendServerResponse(client, "You are now an operator.", "green");
            });

            CommandHandler.RegisterCommand("deop", "Usage: deop <name|id>", cmdArgs =>
            {
                if (cmdArgs.Length < 1) { Log.Warn("Usage: deop <name|id>"); return; }
                string target = string.Join(" ", cmdArgs);
                var client = FindClient(target);
                if (client == null) { Log.Warn("No connected player matching '" + target + "'."); return; }
                if (string.IsNullOrEmpty(client.ClientId)) { Log.Warn("That client hasn't identified yet."); return; }

                if (!Settings.Instance.OppedIds.Contains(client.ClientId))
                {
                    Log.Info(client.PlayerName + " is not an operator.");
                    return;
                }
                Settings.Instance.OppedIds.Remove(client.ClientId);
                client.IsOp = false;
                Settings.Save();
                Log.Info("De-opped " + client.PlayerName + ".");
                SendServerResponse(client, "You are no longer an operator.", "yellow");
            });

            CommandHandler.RegisterCommand("loadsettings", "Reloads settings.json without restarting.", _ =>
            {
                Settings.Load();
                Logger.MinimumLevel = Settings.ParseLogLevel(Settings.Instance.Logging.MinimumLevel, LogLevel.Info);
                Log.Info("Settings reloaded.");
                Log.Info("Death Link: " + (Settings.Instance.DeathLink.Enabled ? "ON" : "OFF"));
                Log.Info("OP'd players: " + (Settings.Instance.OppedIds.Count > 0 ? string.Join(", ", Settings.Instance.OppedIds) : "none"));
                // Update OP status on all connected clients
                foreach (var client in Clients.Values)
                {
                    if (client.ClientId != null)
                        client.IsOp = Settings.Instance.OppedIds.Contains(client.ClientId);
                }
            });

            CommandHandler.RegisterCommand("stop", "Stops the server.", _ =>
            {
                Log.Info("Shutting down...");
                isRunning = false;
                listener.Stop();
            });
        }

        private static ClientConnection? FindClient(string nameOrId)
        {
            // Try numeric ID first
            if (int.TryParse(nameOrId, out int id) && Clients.TryGetValue(id, out var byId))
                return byId;

            // Try exact name or ID match
            foreach (var client in Clients.Values)
            {
                if (client.PlayerName != null && client.PlayerName.Equals(nameOrId, StringComparison.OrdinalIgnoreCase))
                    return client;
                if (client.ClientId != null && client.ClientId.Equals(nameOrId, StringComparison.OrdinalIgnoreCase))
                    return client;
            }
            return null;
        }

        // ── CommandHandler needs a public TryExecute ──
        // (Add this method to CommandHandler.cs)
        // public static bool TryExecute(string name, string[] args)
        // {
        //     if (Commands.TryGetValue(name, out var cmd))
        //     {
        //         try { cmd.Action(args); return true; }
        //         catch { return false; }
        //     }
        //     return false;
        // }

        private static IPAddress ParseBindAddress(string address) => address == "*" || string.IsNullOrEmpty(address) ? IPAddress.Any : (IPAddress.TryParse(address, out var ip) ? ip : IPAddress.Any);

        private static bool HasFlag(string[] args, params string[] flags)
        {
            foreach (var arg in args)
                foreach (var flag in flags)
                    if (arg.Equals(flag, StringComparison.OrdinalIgnoreCase))
                        return true;
            return false;
        }

        private static string DescribeEndpoint(TcpClient tcpClient)
        {
            try { return (tcpClient.Client.RemoteEndPoint as IPEndPoint)?.ToString() ?? "unknown"; }
            catch { return "unknown"; }
        }

        private static string FormatDuration(TimeSpan span)
        {
            if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m {span.Seconds}s";
            if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m {span.Seconds}s";
            return $"{span.TotalSeconds:F1}s";
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double value = bytes;
            int idx = 0;
            while (value >= 1024 && idx < units.Length - 1) { value /= 1024; idx++; }
            return $"{value:0.##} {units[idx]}";
        }

        private sealed class ClientConnection : IDisposable
        {
            private readonly object writerLock = new();
            private StreamWriter? writer;

            public int Id { get; }
            public TcpClient TcpClient { get; private set; }
            public string? ClientId { get; set; }
            public string? PlayerName { get; set; }
            public string RoomName { get; set; } = "default";
            public string? SceneName { get; set; }
            public bool JoinAnnounced { get; set; }
            public bool IsOp { get; set; }

            public ClientConnection(int id, TcpClient tcpClient) { Id = id; TcpClient = tcpClient; }

            public void AttachWriter(StreamWriter streamWriter) { lock (writerLock) writer = streamWriter; }

            public bool TrySend(string line)
            {
                lock (writerLock)
                {
                    if (writer == null) return false;
                    try { writer.WriteLine(line); return true; }
                    catch { return false; }
                }
            }

            public void Dispose()
            {
                lock (writerLock) writer = null;
                try { TcpClient.Close(); } catch { }
            }
        }
    }
}
