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
    internal static class Program
    {
        private const string ServerVersion = "2.0.1-dc1";
        private const string LogSource     = "Server";
        private const string PlayerSource  = "Player";

        private static readonly ConcurrentDictionary<int, ClientConnection> _clients = new();
        private static readonly object _sessionMembershipLock = new object();
        private static int  _nextClientId;
        private static long _totalBytesRelayed;
        private static long _totalMessagesRelayed;

        private static volatile bool _isRunning = true;
        private static Thread? _keepaliveThread;
        private static Timer? _loadingTimeoutTimer;

        private const string DefaultBindAddress = "0.0.0.0";
        private const int    DefaultBindPort    = 24805;
        private const int    MaxClients  = 32;

        private const int KeepaliveIntervalMs = 2000;

        private static int Main(string[] args)
        {
            try { return Run(args); }
            catch (Exception ex)
            {
                Log.Fatal(LogSource, "Fatal error in Main.", ex);
                try
                {
                    File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"),
                        "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + ex + Environment.NewLine);
                }
                catch { }
                return 1;
            }
        }

        private static int Run(string[] args)
        {
            QBRelayModeController.Configure(args);

            if (args.Any(a => a.Equals("--verbose", StringComparison.OrdinalIgnoreCase) ||
                              a.Equals("-v", StringComparison.OrdinalIgnoreCase)))
                Log.MinimumLevel = LogLevel.Debug;

            string bindAddress = GetOption(args, "--bind") ?? DefaultBindAddress;
            int bindPort = DefaultBindPort;
            string portOption = GetOption(args, "--port");
            if (!string.IsNullOrWhiteSpace(portOption) &&
                (!int.TryParse(portOption, out bindPort) || bindPort < 1 || bindPort > 65535))
                throw new ArgumentException("--port must be a number from 1 to 65535.");

            IPAddress bindAddr = ParseBindAddress(bindAddress);
            var listener = new TcpListener(bindAddr, bindPort);
            var uptime = Stopwatch.StartNew();

            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                Log.Info(LogSource, "Shutdown signal received. Closing listener...");
                _isRunning = false;
                listener.Stop();
            };

            RegisterCommands(listener);
            ConsoleCommands.Start(() => _isRunning);

            Log.Info(LogSource, "About to bind " + bindAddr + ":" + bindPort + "...");
            listener.Start();
            Log.Info(LogSource, "Bind successful.");

            Log.Info(LogSource, "===========================================");
            Log.Info(LogSource, " MiSide Multiplayer Relay Server v" + ServerVersion);
            Log.Info(LogSource, "===========================================");
            Log.Info(LogSource, "Listening on " + bindAddr + ":" + bindPort + " (TCP)");
            Log.Info(LogSource, "Lobby mode: directconnect (fixed; Public/Private disabled)");
            Log.Info(LogSource, "Max clients: " + MaxClients);
            Log.Info(LogSource, "Type 'help' for a list of console commands.");

            StartKeepalive();
            _loadingTimeoutTimer = new Timer(_ =>
            {
                if (!_isRunning) return;
                try { ProcessLoadingTimeouts(); }
                catch (Exception ex) { Log.Warn(LogSource, "Loading timeout poll failed: " + ex.Message); }
            }, null, 250, 250);

            try
            {
                while (_isRunning)
                {
                    TcpClient tcpClient;
                    try { tcpClient = listener.AcceptTcpClient(); }
                    catch (SocketException) when (!_isRunning) { break; }
                    catch (ObjectDisposedException) when (!_isRunning) { break; }

                    HandleNewConnection(tcpClient);
                }
                return 0;
            }
            catch (Exception ex)
            {
                Log.Fatal(LogSource, "Listener loop threw.", ex);
                return 1;
            }
            finally
            {
                _isRunning = false;
                try { _loadingTimeoutTimer?.Dispose(); } catch { }
                try { listener.Stop(); } catch { }

                foreach (var c in _clients.Values) c.Dispose();
                _clients.Clear();

                uptime.Stop();
                Log.Info(LogSource, "Server stopped after " + FormatDuration(uptime.Elapsed) +
                                    ". Relayed " + _totalMessagesRelayed + " message(s), " +
                                    FormatBytes(_totalBytesRelayed) + " total.");
            }
        }

        private static void StartKeepalive()
        {
            _keepaliveThread = new Thread(KeepaliveLoop) { IsBackground = true, Name = "Keepalive" };
            _keepaliveThread.Start();
        }

        private static void KeepaliveLoop()
        {
            while (_isRunning)
            {
                Thread.Sleep(KeepaliveIntervalMs);
                if (!_isRunning) break;

                foreach (var kvp in _clients)
                {
                    var conn = kvp.Value;
                    if (conn == null) continue;

                    var env = new Envelope
                    {
                        RoomId    = string.IsNullOrWhiteSpace(conn.JoinedRoomId) ? conn.RoomName : conn.JoinedRoomId,
                        RoomName  = conn.RoomName,
                        SenderId  = "SERVER",
                        EventName = "miside.transport.ping",
                        Payload   = "{}"
                    };

                    if (!conn.Send(env.Serialize()))
                    {
                        Log.Info(PlayerSource, "Keepalive write failed for client #" + conn.Id +
                                               "; closing socket.");
                        try { conn.Dispose(); } catch { }
                    }
                }
            }
        }

        private static void ProcessLoadingTimeouts()
        {
            List<LoadingSessionRegistry.ExpiredParticipants> expired =
                LoadingSessionRegistry.ExpireUnready(DateTime.UtcNow);
            foreach (LoadingSessionRegistry.ExpiredParticipants room in expired)
            {
                foreach (int connectionId in room.ConnectionIds)
                {
                    if (!_clients.TryGetValue(connectionId, out ClientConnection? participant) ||
                        participant == null || participant.IsRoomHost ||
                        !string.Equals(participant.JoinedRoomId, room.RoomId,
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    participant.SendEnvelope(EventNames.LoadingParticipantDropped,
                        Envelope.SerializeObj(new
                        {
                            roomId = room.RoomId,
                            playerId = participant.ClientId,
                            timeoutSeconds = 25
                        }));

                    NotifyRoomPlayerLeft(participant);
                    participant.IsRoomHost = false;
                    participant.JoinedRoomId = null;
                    participant.IsLoadingTimedOut = true;

                    int remainingPlayers = _clients.Values.Count(c => c != null &&
                        string.Equals(c.JoinedRoomId, room.RoomId,
                            StringComparison.OrdinalIgnoreCase));
                    SessionRegistry.UpdatePlayerCount(room.RoomId, remainingPlayers);

                    Log.Warn(PlayerSource, "Client #" + participant.Id + " (" + participant.PlayerName +
                                           ") was removed from room '" + room.RoomId +
                                           "' after missing the 25-second loading-stage timeout.");
                }

                BroadcastLoadingPhase(room.RoomId, room.State);
            }
        }

        private static void HandleNewConnection(TcpClient tcpClient)
        {
            tcpClient.NoDelay = true;
            try { tcpClient.SendTimeout = 2000; } catch { }

            string remote = DescribeEndpoint(tcpClient);

            if (MaxClients > 0 && _clients.Count >= MaxClients)
            {
                Log.Warn(PlayerSource, "Rejected " + remote + " — server full (" + MaxClients + ").");
                try { tcpClient.Close(); } catch { }
                return;
            }

            int id = Interlocked.Increment(ref _nextClientId);
            var conn = new ClientConnection(id, tcpClient, remote);
            _clients[id] = conn;

            Log.Info(PlayerSource, "Client #" + id + " connected from " + remote +
                                   ". (" + _clients.Count + " total online)");

            ThreadPool.QueueUserWorkItem(_ => HandleClient(conn));
        }

        private static void HandleClient(ClientConnection conn)
        {
            var timer = Stopwatch.StartNew();
            string? exitReason = null;

            try
            {
                using var stream = conn.TcpClient.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true);
                writer.NewLine   = "\n";
                writer.AutoFlush = true;
                conn.AttachWriter(writer);

                bool loggedFirst = false;

                while (_isRunning && conn.TcpClient.Connected)
                {
                    string? line;
                    try
                    {
                        line = reader.ReadLine();
                    }
                    catch (Exception ex)
                    {
                        exitReason = "ReadLine threw: " + ex.GetType().Name + ": " + ex.Message +
                                     (ex.InnerException != null
                                        ? "  inner: " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message
                                        : "");
                        throw;
                    }

                    if (line == null)
                    {
                        exitReason = "ReadLine returned null (peer closed the socket)";
                        break;
                    }

                    if (!loggedFirst)
                    {
                        loggedFirst = true;
                        Log.Info(PlayerSource, "Client #" + conn.Id + " first line: " +
                                               (line.Length > 300 ? line.Substring(0, 300) + "..." : line));
                    }

                    var env = Envelope.TryParse(line);
                    if (env == null)
                    {
                        Log.Warn(PlayerSource, "Client #" + conn.Id + " sent unparseable line; ignoring.");
                        continue;
                    }

                    if (env.EventName == "miside.transport.ping")
                    {
                        if (string.IsNullOrWhiteSpace(env.RoomId) ||
                            !IsSameRoom(env.RoomId, GetConnectionRoomId(conn)) ||
                            (!string.IsNullOrWhiteSpace(env.RoomName) && !IsSameRoom(env.RoomName, env.RoomId)))
                            Log.Warn(PlayerSource, "Client #" + conn.Id + " sent ping with missing or mismatched RoomId.");
                        continue;
                    }

                    Dispatch(conn, env, line);
                }

                if (exitReason == null)
                {
                    exitReason = _isRunning
                        ? "loop condition failed (TcpClient.Connected became false)"
                        : "server is shutting down";
                }
            }
            catch (IOException ex)
            {
                if (exitReason == null)
                    exitReason = "IOException: " + ex.GetType().Name + ": " + ex.Message +
                                 (ex.InnerException != null
                                    ? "  inner: " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message
                                    : "");
                Log.Warn(PlayerSource, "Client #" + conn.Id + " (" + conn.Remote + ") dropped: " + exitReason);
            }
            catch (SocketException ex)
            {
                if (exitReason == null)
                    exitReason = "SocketException: " + ex.SocketErrorCode + ": " + ex.Message;
                Log.Warn(PlayerSource, "Client #" + conn.Id + " (" + conn.Remote + ") socket error: " + exitReason);
            }
            catch (ObjectDisposedException ex)
            {
                if (exitReason == null)
                    exitReason = "ObjectDisposedException on " + (ex.ObjectName ?? "?");
                Log.Warn(PlayerSource, "Client #" + conn.Id + " (" + conn.Remote + ") socket disposed: " + exitReason);
            }
            catch (Exception ex)
            {
                if (exitReason == null)
                    exitReason = "Unhandled: " + ex.GetType().Name + ": " + ex.Message;
                Log.Error(PlayerSource, "Client #" + conn.Id + " handler threw: " + ex.ToString());
            }
            finally
            {
                Log.Info(PlayerSource, "Client #" + conn.Id + " FINALLY: IsRoomHost=" +
                                       conn.IsRoomHost + " JoinedRoomId='" +
                                       (conn.JoinedRoomId ?? "<null>") + "' RoomName='" +
                                       (conn.RoomName ?? "<null>") + "'.");

                if (conn.IsRoomHost && !string.IsNullOrEmpty(conn.JoinedRoomId))
                {
                    Log.Info(PlayerSource, "Client #" + conn.Id + " was a host; calling NotifyRoomHostLeft.");
                    NotifyRoomHostLeft(conn);
                    DisconnectRoomJoiners(conn.JoinedRoomId!, conn.Id);
                    LoadingSessionRegistry.Remove(conn.JoinedRoomId!);
                }
                else if (!conn.IsRoomHost && !string.IsNullOrEmpty(conn.JoinedRoomId))
                {
                    Log.Info(PlayerSource, "Client #" + conn.Id + " was a joiner; calling NotifyRoomPlayerLeft.");
                    NotifyRoomPlayerLeft(conn);
                    if (LoadingSessionRegistry.RemoveParticipant(conn.Id, out string loadingRoom,
                        out LoadingSessionRegistry.Snapshot loadingState))
                        BroadcastLoadingPhase(loadingRoom, loadingState);
                }
                else
                {
                    Log.Info(PlayerSource, "Client #" + conn.Id +
                                           " was neither host nor joiner in a room; no notification sent.");
                }

                SessionRegistry.UnregisterByHost(conn.Id.ToString());

                _clients.TryRemove(conn.Id, out _);
                conn.Dispose();
                timer.Stop();

                Log.Info(PlayerSource, "Client #" + conn.Id + " (" + conn.Remote + ") disconnected. " +
                                       "Reason: " + (exitReason ?? "unknown") +
                                       ".  Session " + FormatDuration(timer.Elapsed) +
                                       ". (" + _clients.Count + " remaining)");
            }
        }

        private static void Dispatch(ClientConnection conn, Envelope env, string rawLine)
        {
            if (conn.IsLoadingTimedOut) return;
            string evt = env.EventName ?? "";

            if (evt == EventNames.TransportHello)
            {
                HandleHello(conn, env);
                return;
            }

            if (conn.ClientId == null && !string.IsNullOrEmpty(env.SenderId))
                AdoptIdentity(conn, env);

            string expectedRoomId = GetConnectionRoomId(conn);
            if (string.IsNullOrWhiteSpace(env.RoomId) ||
                !IsSameRoom(env.RoomId, expectedRoomId) ||
                (!string.IsNullOrWhiteSpace(env.RoomName) && !IsSameRoom(env.RoomName, env.RoomId)))
            {
                Log.Warn(PlayerSource, "Client #" + conn.Id + " packet with missing or mismatched RoomId; dropped.");
                return;
            }

            switch (evt)
            {
                case EventNames.SessionRegister:   HandleSessionRegister(conn, env); return;
                case EventNames.SessionUnregister: HandleSessionUnregister(conn, env); return;
                case EventNames.SessionList:       HandleSessionList(conn); return;
                case EventNames.SessionResolve:    HandleSessionResolve(conn, env); return;
                case EventNames.SessionJoin:       HandleSessionJoin(conn, env); return;
                case EventNames.SessionStart:      HandleSessionStart(conn, env); return;
                case EventNames.LoadingClientReady:
                    HandleLoadingClientReady(conn, env, EventNames.LoadingClientReady);
                    return;
                case EventNames.LoadingSyncDoneAck:
                    HandleLoadingClientReady(conn, env, EventNames.LoadingSyncDoneAck);
                    return;
                case EventNames.LoadingPhase:       HandleLoadingPhase(conn, env); return;
                case EventNames.LoadingDone:        HandleLoadingDone(conn); return;
            }

            if (evt == EventNames.MitaSyncRequest)
            {
                if (conn.IsRoomHost || string.IsNullOrWhiteSpace(conn.JoinedRoomId))
                {
                    Log.Warn(PlayerSource, "Dropped Mita snapshot request from non-member or host client #" + conn.Id + ".");
                    return;
                }
                RouteToRoomHost(conn.JoinedRoomId!, conn, env);
                return;
            }

            if (evt == EventNames.MitaState || evt == EventNames.MitaRpc ||
                evt == EventNames.MitaSyncComplete)
            {
                if (!conn.IsRoomHost || string.IsNullOrWhiteSpace(conn.JoinedRoomId))
                {
                    Log.Warn(PlayerSource, "Dropped host-authoritative Mita event from non-host client #" + conn.Id + ".");
                    return;
                }
                RouteToRoom(conn.JoinedRoomId!, conn, env);
                return;
            }

            if ((evt == "miside.world.door" || evt == "miside.world.storyobj") &&
                !string.IsNullOrWhiteSpace(conn.JoinedRoomId) && !conn.IsRoomHost)
            {
                RouteToRoomHost(conn.JoinedRoomId!, conn, env);
                return;
            }

            Broadcast(conn, env, evt);
        }

        // Session start is server-authoritative: the relay supplies the exact
        // number of connected room participants and returns one identical start
        // packet to the host and every joiner. Client roster hello packets can
        // still be in flight, so they are not safe for this count.
        private static void HandleSessionStart(ClientConnection conn, Envelope env)
        {
            if (!conn.IsRoomHost || string.IsNullOrWhiteSpace(conn.JoinedRoomId))
            {
                Log.Warn(PlayerSource, "Ignoring session start from non-host client #" + conn.Id + ".");
                return;
            }

            Envelope.SessionStartRequest? request =
                Envelope.TryDeserialize<Envelope.SessionStartRequest>(env.Payload);
            if (request == null || string.IsNullOrWhiteSpace(request.sceneName))
            {
                Log.Warn(PlayerSource, "Ignoring malformed session start from client #" + conn.Id + ".");
                return;
            }

            string roomId = conn.JoinedRoomId!;
            LoadingSessionRegistry.Snapshot state;
            bool created;
            lock (_sessionMembershipLock)
            {
                if (!SessionRegistry.TryLockInGame(roomId, conn.Id.ToString()))
                {
                    Reply(conn, new { type = "session.started", ok = false, reason = "host_not_registered" });
                    Log.Warn(PlayerSource, "Ignoring session start from client #" + conn.Id +
                                           " because its room is not registered to that host.");
                    return;
                }

                int[] participants = _clients.Values
                    .Where(c => c != null && string.Equals(c.JoinedRoomId, roomId,
                        StringComparison.OrdinalIgnoreCase))
                    .Select(c => c.Id)
                    .ToArray();
                state = LoadingSessionRegistry.StartOrGet(
                    roomId, request.sceneName!, participants, conn.Id, out created);
            }

            string payload = Envelope.SerializeObj(new
            {
                sceneName = request.sceneName,
                startedByPlayerId = string.IsNullOrWhiteSpace(request.startedByPlayerId)
                    ? conn.ClientId : request.startedByPlayerId,
                expectedPlayerCount = state.ExpectedCount
            });

            int delivered = BroadcastToLoadingRoom(roomId, EventNames.SessionStart, payload);
            BroadcastLoadingPhase(roomId, state);

            Log.Info(PlayerSource, "Session start: room='" + roomId +
                                   "' scene='" + request.sceneName +
                                   "' expectedPlayers=" + state.ExpectedCount +
                                   " delivered=" + delivered +
                                   (created ? "." : " (replayed existing loading state)."));
        }

        private static void HandleLoadingClientReady(ClientConnection conn, Envelope env, string eventName)
        {
            if (string.IsNullOrWhiteSpace(conn.JoinedRoomId)) return;
            Envelope.LoadingClientReadyRequest? request =
                Envelope.TryDeserialize<Envelope.LoadingClientReadyRequest>(env.Payload);
            if (request == null) return;
            if (eventName == EventNames.LoadingSyncDoneAck && request.stageIndex != 5)
            {
                Log.Warn(PlayerSource, "Ignoring ACK_SYNC_DONE from client #" + conn.Id +
                                       " for stage " + request.stageIndex + ".");
                return;
            }

            string roomId = conn.JoinedRoomId!;
            if (!LoadingSessionRegistry.TryMarkReady(roomId, conn.Id, request.stageIndex,
                out LoadingSessionRegistry.Snapshot state)) return;

            if (!conn.IsRoomHost)
            {
                ClientConnection host = _clients.Values.FirstOrDefault(c => c != null &&
                    c.IsRoomHost && string.Equals(c.JoinedRoomId, roomId,
                        StringComparison.OrdinalIgnoreCase));
                if (host != null)
                {
                    string hostPayload = Envelope.SerializeObj(new
                    {
                        playerId = conn.ClientId ?? request.playerId,
                        stageIndex = request.stageIndex
                    });
                    host.SendEnvelope(eventName, hostPayload);
                }
            }

            BroadcastLoadingPhase(roomId, state);
        }

        private static void HandleLoadingPhase(ClientConnection conn, Envelope env)
        {
            if (!conn.IsRoomHost || string.IsNullOrWhiteSpace(conn.JoinedRoomId)) return;
            Envelope.LoadingPhaseRequest? request =
                Envelope.TryDeserialize<Envelope.LoadingPhaseRequest>(env.Payload);
            if (request == null) return;

            if (LoadingSessionRegistry.TryUpdatePhase(conn.JoinedRoomId!, conn.Id, request.stageIndex,
                request.phaseId ?? "", request.progress, request.friendlyName ?? "",
                request.statusName ?? "", out LoadingSessionRegistry.Snapshot state))
                BroadcastLoadingPhase(conn.JoinedRoomId!, state);
        }

        private static void HandleLoadingDone(ClientConnection conn)
        {
            if (!conn.IsRoomHost || string.IsNullOrWhiteSpace(conn.JoinedRoomId)) return;
            if (LoadingSessionRegistry.TryComplete(conn.JoinedRoomId!,
                out LoadingSessionRegistry.Snapshot state))
                BroadcastToLoadingRoom(conn.JoinedRoomId!, EventNames.LoadingDone, "{}");
        }

        private static void BroadcastLoadingPhase(string roomId, LoadingSessionRegistry.Snapshot state)
        {
            string payload = Envelope.SerializeObj(new
            {
                stageIndex = state.StageIndex,
                phaseId = state.PhaseId,
                progress = state.Progress,
                readyCount = state.ReadyCount,
                expectedCount = state.ExpectedCount,
                friendlyName = state.FriendlyName,
                statusName = state.StatusName
            });
            BroadcastToLoadingRoom(roomId, EventNames.LoadingPhase, payload);
        }

        private static int BroadcastToLoadingRoom(string roomId, string eventName, string payload)
        {
            int delivered = 0;
            foreach (ClientConnection c in _clients.Values)
            {
                if (c == null || !string.Equals(c.JoinedRoomId, roomId,
                    StringComparison.OrdinalIgnoreCase)) continue;
                if (c.SendEnvelope(eventName, payload)) delivered++;
            }
            return delivered;
        }

        private static void HandleHello(ClientConnection conn, Envelope env)
        {
            if (string.IsNullOrWhiteSpace(env.SenderId))
            {
                Log.Warn(PlayerSource, "Client #" + conn.Id + " hello missing senderId.");
                return;
            }

            if (string.IsNullOrWhiteSpace(env.RoomId) ||
                (!string.IsNullOrWhiteSpace(env.RoomName) && !IsSameRoom(env.RoomId, env.RoomName)))
            {
                Log.Warn(PlayerSource, "Client #" + conn.Id + " hello missing or mismatched RoomId.");
                return;
            }

            conn.ClientId   = env.SenderId;
            conn.RoomName   = NormalizeRoom(env.RoomId);
            conn.PlayerName = NormalizeName(env.DisplayName, env.SenderId);
            conn.SceneName  = env.SceneName;

            Log.Info(PlayerSource, "Client #" + conn.Id + " identified as " +
                                   conn.PlayerName + " (" + conn.Remote + ")" +
                                   "  room='" + conn.RoomName + "'");
        }

        private static void AdoptIdentity(ClientConnection conn, Envelope env)
        {
            if (string.IsNullOrWhiteSpace(env.SenderId)) return;
            if (string.IsNullOrWhiteSpace(env.RoomId)) return;

            conn.ClientId   = env.SenderId;
            conn.RoomName   = NormalizeRoom(env.RoomId);
            conn.PlayerName = NormalizeName(env.DisplayName, env.SenderId);
            conn.SceneName  = env.SceneName;

            Log.Info(PlayerSource, "Client #" + conn.Id + " identified (from state) as " +
                                   conn.PlayerName + "  room='" + conn.RoomName + "'");
        }

        private static void HandleSessionRegister(ClientConnection conn, Envelope env)
        {
            if (string.IsNullOrEmpty(conn.ClientId))
            {
                Reply(conn, new { type = "session.registered", ok = false, reason = "not_identified" });
                return;
            }

            var req = Envelope.TryDeserialize<Envelope.RegisterRequest>(env.Payload);
            if (req == null || string.IsNullOrWhiteSpace(req.roomId))
            {
                Reply(conn, new { type = "session.registered", ok = false, reason = "bad_request" });
                return;
            }

            if (!IsSameRoom(req.roomId, env.RoomId))
            {
                Reply(conn, new { type = "session.registered", ok = false, reason = "room_id_mismatch" });
                return;
            }

            int vis = req.visibility;
            if (vis < 0 || vis > 2) vis = 0;

            string modeReason;
            if (!QBRelayModeController.AllowsVisibility(vis, req.roomId!, req.code ?? "", out modeReason))
            {
                Reply(conn, new
                {
                    type = "session.registered",
                    ok = false,
                    roomId = req.roomId,
                    reason = modeReason
                });
                Log.Warn(PlayerSource, "Rejected session registration for room='" + req.roomId +
                                       "' visibility=" + vis + " in " + QBRelayModeController.ModeName + " mode.");
                return;
            }

            if (vis == 1 && string.IsNullOrWhiteSpace(req.code))
            {
                Reply(conn, new { type = "session.registered", ok = false, reason = "missing_code" });
                return;
            }

            bool ok = SessionRegistry.Register(
                req.roomId!, conn.ClientId!, conn.Id.ToString(),
                req.sessionName ?? "Session", conn.PlayerName ?? "Player",
                req.maxPlayers, vis, req.code ?? "");

            if (ok)
            {
                conn.IsRoomHost   = true;
                conn.JoinedRoomId = req.roomId;
                Log.Info(PlayerSource, "Session registered: room='" + req.roomId +
                                       "' by " + conn.PlayerName +
                                       " (" + (vis == 0 ? "Public" : vis == 1 ? "Private" : "Direct") + ")");
            }

            Reply(conn, new
            {
                type   = "session.registered",
                ok     = ok,
                roomId = req.roomId,
                reason = ok ? "" : "already_reserved"
            });
        }

        private static void HandleSessionUnregister(ClientConnection conn, Envelope env)
        {
            var req = Envelope.TryDeserialize<Envelope.UnregisterRequest>(env.Payload);
            if (req == null || string.IsNullOrWhiteSpace(req.roomId)) return;

            bool hostIsLeaving = conn.IsRoomHost &&
                string.Equals(conn.JoinedRoomId, req.roomId, StringComparison.OrdinalIgnoreCase);
            if (hostIsLeaving)
            {
                NotifyRoomHostLeft(conn);
                DisconnectRoomJoiners(req.roomId!, conn.Id);
            }

            SessionRegistry.Unregister(req.roomId!);
            LoadingSessionRegistry.Remove(req.roomId!);
            Log.Info(PlayerSource, "Session unregistered: room='" + req.roomId + "'.");

            if (hostIsLeaving)
            {
                conn.IsRoomHost   = false;
                conn.JoinedRoomId = null;
            }
        }

        private static void HandleSessionList(ClientConnection conn)
        {
            if (QBRelayModeController.Mode == QBRelayLobbyMode.DirectConnect)
            {
                // Direct Connect relays never contribute rows to the
                // directory used by Public/Private clients.
                Reply(conn, new { type = "session.list.result", sessions = Array.Empty<object>() });
                return;
            }

            foreach (var s in SessionRegistry.ListPublic())
            {
                int count = 0;
                foreach (var c in _clients.Values)
                    if (c != null && string.Equals(c.JoinedRoomId, s.SessionId,
                        StringComparison.OrdinalIgnoreCase)) count++;
                SessionRegistry.UpdatePlayerCount(s.SessionId, count);
            }

            var sessions = SessionRegistry.ListPublic();
            var list = sessions.Select(s => new
            {
                sessionId       = s.SessionId,
                sessionName     = s.SessionName,
                hostDisplayName = s.HostDisplayName,
                playerCount     = s.PlayerCount,
                maxPlayers      = s.MaxPlayers,
                visibility      = s.Visibility,
                state           = s.State,
                inGame          = SessionRegistry.IsLockedInGame(s.SessionId)
            }).ToArray();

            Reply(conn, new { type = "session.list.result", sessions = list });
        }

        private static void HandleSessionResolve(ClientConnection conn, Envelope env)
        {
            if (QBRelayModeController.Mode == QBRelayLobbyMode.DirectConnect)
            {
                Reply(conn, new
                {
                    type = "session.resolve.result",
                    found = false,
                    reason = "server_mode_mismatch"
                });
                return;
            }

            var req = Envelope.TryDeserialize<Envelope.ResolveRequest>(env.Payload);
            var s = req != null ? SessionRegistry.ResolveByCode(req.code ?? "") : null;

            Reply(conn, new
            {
                type            = "session.resolve.result",
                found           = s != null,
                sessionId       = s?.SessionId,
                sessionName     = s?.SessionName,
                hostDisplayName = s?.HostDisplayName,
                maxPlayers      = s?.MaxPlayers ?? 0,
                visibility      = s?.Visibility ?? 0
            });
        }

        private static void HandleSessionJoin(ClientConnection conn, Envelope env)
        {
            var req = Envelope.TryDeserialize<Envelope.JoinRequest>(env.Payload);
            if (req == null || string.IsNullOrWhiteSpace(req.roomId))
            {
                Reply(conn, new { type = "session.joined", ok = false, reason = "bad_request" });
                return;
            }

            if (!IsSameRoom(req.roomId, env.RoomId))
            {
                Reply(conn, new { type = "session.joined", ok = false, reason = "room_id_mismatch" });
                return;
            }

            int playerCount;
            lock (_sessionMembershipLock)
            {
                SessionRegistry.Session session = SessionRegistry.Get(req.roomId!);
                if (session == null)
                {
                    Reply(conn, new { type = "session.joined", ok = false, reason = "no_such_room" });
                    return;
                }

                bool sessionIsDirect = session.Visibility == 2;
                bool serverIsDirect = QBRelayModeController.Mode == QBRelayLobbyMode.DirectConnect;
                if (sessionIsDirect != serverIsDirect)
                {
                    Reply(conn, new { type = "session.joined", ok = false, reason = "server_mode_mismatch" });
                    return;
                }

                if (SessionRegistry.IsLockedInGame(req.roomId!) ||
                    LoadingSessionRegistry.IsInProgress(req.roomId!))
                {
                    Reply(conn, new { type = "session.joined", ok = false, reason = "Locked_InGame" });
                    Log.Info(PlayerSource, "Rejected join to room='" + req.roomId + "' from client #" +
                                           conn.Id + " — lobby is Locked_InGame.");
                    return;
                }

                playerCount = _clients.Values.Count(c => c != null &&
                    string.Equals(c.JoinedRoomId, req.roomId, StringComparison.OrdinalIgnoreCase));
                if (playerCount >= session.MaxPlayers)
                {
                    Reply(conn, new { type = "session.joined", ok = false, reason = "lobby_full" });
                    Log.Info(PlayerSource, "Rejected join to room='" + req.roomId + "' from client #" +
                                           conn.Id + " — lobby full (" + playerCount + "/" + session.MaxPlayers + ").");
                    return;
                }

                conn.IsRoomHost   = false;
                conn.JoinedRoomId = req.roomId;
                conn.RoomName     = req.roomId!;
                playerCount++;
                SessionRegistry.UpdatePlayerCount(req.roomId!, playerCount);
            }

            Reply(conn, new
            {
                type        = "session.joined",
                ok          = true,
                roomId      = req.roomId,
                playerCount = playerCount
            });

            Log.Info(PlayerSource, "Client #" + conn.Id + " (" + conn.PlayerName +
                                   ") joined session room='" + req.roomId + "'.");

            NotifyRoomPlayerJoined(conn);
        }

        private static void NotifyRoomPlayerJoined(ClientConnection joiner)
        {
            if (string.IsNullOrEmpty(joiner.JoinedRoomId)) return;

            int delivered = 0;
            foreach (var c in _clients.Values)
            {
                if (c == null) continue;
                if (c.Id == joiner.Id) continue;
                if (!string.Equals(c.JoinedRoomId, joiner.JoinedRoomId, StringComparison.OrdinalIgnoreCase)) continue;

                c.SendEnvelope(EventNames.SessionPlayerJoined, Envelope.SerializeObj(new
                {
                    type       = "session.player.joined",
                    roomId     = joiner.JoinedRoomId,
                    playerId   = joiner.ClientId,
                    playerName = joiner.PlayerName
                }));
                delivered++;
            }
            Log.Info(PlayerSource, "NotifyRoomPlayerJoined: room='" + joiner.JoinedRoomId +
                                   "' delivered to " + delivered + " client(s).");
        }

        private static void NotifyRoomPlayerLeft(ClientConnection leaver)
        {
            if (string.IsNullOrEmpty(leaver.JoinedRoomId)) return;

            int delivered = 0;
            foreach (var c in _clients.Values)
            {
                if (c == null) continue;
                if (c.Id == leaver.Id) continue;
                if (!string.Equals(c.JoinedRoomId, leaver.JoinedRoomId, StringComparison.OrdinalIgnoreCase)) continue;

                c.SendEnvelope(EventNames.SessionPlayerLeft, Envelope.SerializeObj(new
                {
                    type       = "session.player.left",
                    roomId     = leaver.JoinedRoomId,
                    playerId   = leaver.ClientId,
                    playerName = leaver.PlayerName
                }));
                delivered++;
            }
            Log.Info(PlayerSource, "NotifyRoomPlayerLeft: room='" + leaver.JoinedRoomId +
                                   "' delivered to " + delivered + " client(s).");
        }

        private static void NotifyRoomHostLeft(ClientConnection host)
        {
            if (string.IsNullOrEmpty(host.JoinedRoomId)) return;

            int delivered = 0;
            foreach (var c in _clients.Values)
            {
                if (c == null) continue;
                if (c.Id == host.Id) continue;
                if (!string.Equals(c.JoinedRoomId, host.JoinedRoomId, StringComparison.OrdinalIgnoreCase)) continue;

                bool ok = c.SendEnvelope(EventNames.SessionHostLeft, Envelope.SerializeObj(new
                {
                    type       = "session.host.left",
                    roomId     = host.JoinedRoomId,
                    playerId   = host.ClientId,
                    playerName = host.PlayerName
                }));
                Log.Info(PlayerSource, "NotifyRoomHostLeft: -> client #" + c.Id + " ok=" + ok);
                delivered++;
            }
            Log.Info(PlayerSource, "NotifyRoomHostLeft: room='" + host.JoinedRoomId +
                                   "' delivered to " + delivered + " client(s).");
        }

        private static void DisconnectRoomJoiners(string roomId, int hostConnectionId)
        {
            int disconnected = 0;
            foreach (var c in _clients.Values)
            {
                if (c == null || c.Id == hostConnectionId) continue;
                if (!string.Equals(c.JoinedRoomId, roomId,
                    StringComparison.OrdinalIgnoreCase)) continue;

                // HostLeft is sent before this. Clear the membership before
                // closing so each joiner's own disconnect cleanup cannot
                // emit a misleading player-left event for a dead room.
                // Give that event a short delivery window; it drives the
                // client-side native return-to-menu and correct host-left UI.
                c.IsRoomHost = false;
                c.JoinedRoomId = null;
                ClientConnection joiner = c;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    Thread.Sleep(250);
                    try { joiner.Dispose(); } catch { }
                });
                disconnected++;
            }

            Log.Info(PlayerSource, "DisconnectRoomJoiners: room='" + roomId +
                                   "' scheduled " + disconnected + " joiner connection(s) for closure after host departure.");
        }

        private static void Broadcast(ClientConnection sender, Envelope packet, string eventName)
        {
            RouteToRoom(GetConnectionRoomId(sender), sender, packet, eventName);
        }

        private static int RouteToRoom(string roomId, ClientConnection sender,
                                       Envelope packet, string? eventName = null)
        {
            if (string.IsNullOrWhiteSpace(roomId)) return 0;

            // A registered lobby is routed by authenticated membership, never
            // by the room name supplied during hello. Unregistered direct
            // rooms retain the legacy room-name route.
            bool registeredSession = SessionRegistry.IsReserved(roomId);
            if (registeredSession && !string.Equals(sender.JoinedRoomId, roomId,
                StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn(PlayerSource, "Dropped packet from non-member client #" + sender.Id +
                                       " for registered room '" + roomId + "'.");
                return 0;
            }

            string line = CreateRoutedEnvelope(roomId, sender, packet).Serialize();
            int delivered = 0, failed = 0;
            foreach (var c in _clients.Values)
            {
                if (c.Id == sender.Id) continue;
                bool isRecipient = registeredSession
                    ? string.Equals(c.JoinedRoomId, roomId, StringComparison.OrdinalIgnoreCase)
                    : IsSameRoom(GetConnectionRoomId(c), roomId);
                if (!isRecipient) continue;
                if (c.Send(line)) delivered++;
                else failed++;
            }

            Interlocked.Increment(ref _totalMessagesRelayed);
            Interlocked.Add(ref _totalBytesRelayed, Encoding.UTF8.GetByteCount(line));

            if (failed > 0)
                Log.Warn(PlayerSource, "Failed delivery to " + failed + " client(s).");
            string routedEventName = eventName ?? packet.EventName ?? "";
            if (string.Equals(routedEventName, EventNames.MitaSyncComplete, StringComparison.Ordinal))
                Log.Info(PlayerSource, "Relayed Mita snapshot completion in room '" + roomId +
                                       "' from script host #" + sender.Id + " to " + delivered +
                                       " room client(s).");
            if (Log.MinimumLevel == LogLevel.Debug)
                Log.Debug(PlayerSource, "Relayed '" + routedEventName +
                                        "' from #" + sender.Id + " in room '" + roomId +
                                        "' to " + delivered + " client(s).");
            return delivered;
        }

        private static void RouteToRoomHost(string roomId, ClientConnection sender, Envelope packet)
        {
            ClientConnection host = _clients.Values.FirstOrDefault(c => c != null && c.IsRoomHost &&
                string.Equals(c.JoinedRoomId, roomId, StringComparison.OrdinalIgnoreCase));
            if (host == null)
            {
                Log.Warn(PlayerSource, "No script host is connected for room '" + roomId +
                                       "'; dropped '" + (packet.EventName ?? "") + "'.");
                return;
            }

            Envelope routed = CreateRoutedEnvelope(roomId, sender, packet);
            if (!host.Send(routed.Serialize()))
            {
                Log.Warn(PlayerSource, "Failed to route '" + (packet.EventName ?? "") +
                                       "' from client #" + sender.Id + " to room host #" + host.Id + ".");
                return;
            }

            if (string.Equals(packet.EventName, EventNames.MitaSyncRequest, StringComparison.Ordinal))
                Log.Info(PlayerSource, "Forwarded Mita snapshot request in room '" + roomId +
                                       "' from client #" + sender.Id + " to script host #" + host.Id + ".");
        }

        private static Envelope CreateRoutedEnvelope(string roomId, ClientConnection sender,
                                                     Envelope packet)
        {
            return new Envelope
            {
                RoomId = roomId,
                RoomName = roomId,
                SenderId = sender.ClientId ?? packet.SenderId,
                EventName = packet.EventName,
                Payload = packet.Payload,
                DisplayName = sender.PlayerName ?? packet.DisplayName,
                SceneName = packet.SceneName ?? sender.SceneName
            };
        }

        private static string GetConnectionRoomId(ClientConnection connection)
        {
            return NormalizeRoom(string.IsNullOrWhiteSpace(connection.JoinedRoomId)
                ? connection.RoomName
                : connection.JoinedRoomId);
        }

        private static void Reply(ClientConnection conn, object payload)
        {
            conn.SendEnvelope(EventNames.SessionResponse, Envelope.SerializeObj(payload));
        }

        private static bool IsSameRoom(string? a, string? b)
        {
            return string.Equals(NormalizeRoom(a), NormalizeRoom(b), StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeRoom(string? room)
        {
            return string.IsNullOrWhiteSpace(room) ? "default" : room!.Trim();
        }

        private static string NormalizeName(string? name, string fallback)
        {
            string n = string.IsNullOrWhiteSpace(name) ? fallback : name!.Trim();
            return n.Length > 48 ? n.Substring(0, 48) : n;
        }

        private static IPAddress ParseBindAddress(string s)
        {
            if (s == "*" || string.IsNullOrEmpty(s)) return IPAddress.Any;
            return IPAddress.TryParse(s, out var ip) ? ip : IPAddress.Any;
        }

        private static string? GetOption(string[] args, string name)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i] ?? string.Empty;
                if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                    return arg.Substring(name.Length + 1).Trim();
                if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    return args[i + 1]?.Trim();
            }
            return null;
        }

        private static string DescribeEndpoint(TcpClient c)
        {
            try { return (c.Client.RemoteEndPoint as IPEndPoint)?.ToString() ?? "unknown"; }
            catch { return "unknown"; }
        }

        private static string FormatDuration(TimeSpan t)
        {
            if (t.TotalHours   >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m {t.Seconds}s";
            if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds}s";
            return $"{t.TotalSeconds:F1}s";
        }

        private static string FormatBytes(long bytes)
        {
            string[] u = { "B", "KB", "MB", "GB" };
            double v = bytes; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return $"{v:0.##} {u[i]}";
        }

        private static void RegisterCommands(TcpListener listener)
        {
            ConsoleCommands.Register("directconnect", "Confirms this relay is Direct Connect-only.", _ =>
            {
                Log.Info(LogSource, "This dedicated relay is already fixed to Direct Connect mode.");
            });

            ConsoleCommands.Register("normal", "Unavailable in this Direct Connect-only relay.", _ =>
            {
                Log.Warn(LogSource, "Cannot do that in this current environment. This relay only supports Direct Connect.");
            });

            ConsoleCommands.Register("mode", "Shows this relay's lobby mode.", _ =>
            {
                Log.Info(LogSource, "Lobby mode: " + QBRelayModeController.ModeName + ".");
            });

            ConsoleCommands.Register("status", "Shows uptime and connection stats.", _ =>
            {
                Log.Info(LogSource, "Clients online: " + _clients.Count + " / " + MaxClients);
                Log.Info(LogSource, "Messages relayed: " + _totalMessagesRelayed +
                                    " (" + FormatBytes(_totalBytesRelayed) + ")");
            });

            ConsoleCommands.Register("sessions", "Lists all registered sessions.", _ =>
            {
                var list = SessionRegistry.ListPublic();
                if (list.Count == 0)
                {
                    Log.Info(LogSource, "No public sessions registered.");
                    return;
                }
                foreach (var s in list)
                {
                    Log.Info(LogSource, "  room=" + s.SessionId +
                                        "  name='" + s.SessionName + "'" +
                                        "  host=" + s.HostDisplayName +
                                        "  players=" + s.PlayerCount + "/" + s.MaxPlayers);
                }
            });

            ConsoleCommands.Register("list", "Lists currently connected clients.", _ =>
            {
                if (_clients.IsEmpty) { Log.Info(LogSource, "No clients connected."); return; }
                foreach (var c in _clients.Values)
                {
                    Log.Info(LogSource, "  #" + c.Id + "  " +
                                        (c.PlayerName ?? "?") + "  " +
                                        c.Remote +
                                        (c.ClientId != null ? "  id=" + c.ClientId : "") +
                                        (c.IsRoomHost ? "  [HOST]" : "") +
                                        "  joinedRoom='" + (c.JoinedRoomId ?? "<null>") + "'");
                }
            });

            ConsoleCommands.Register("kick", "Usage: kick <clientId>", args =>
            {
                if (args.Length < 1 || !int.TryParse(args[0], out int id))
                {
                    Log.Warn(LogSource, "Usage: kick <clientId>");
                    return;
                }
                if (_clients.TryGetValue(id, out var c))
                {
                    Log.Info(LogSource, "Kicking client #" + id + " (" + c.Remote + ").");
                    c.Dispose();
                }
                else Log.Warn(LogSource, "No client with ID " + id + ".");
            });

            ConsoleCommands.Register("stop", "Stops the server.", _ =>
            {
                Log.Info(LogSource, "Shutting down...");
                _isRunning = false;
                listener.Stop();
            });
        }
    }
}
