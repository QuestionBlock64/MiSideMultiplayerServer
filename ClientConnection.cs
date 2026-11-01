using System;
using System.IO;
using System.Net.Sockets;

namespace MiSideMultiplayer.RelayServer
{
    internal sealed class ClientConnection : IDisposable
    {
        private readonly object _writerLock = new object();
        private StreamWriter? _writer;

        public int    Id          { get; }
        public TcpClient TcpClient { get; }
        public string Remote      { get; }

        // Bound during hello.
        public string? ClientId    { get; set; }
        public string? PlayerName  { get; set; }
        public string  RoomName    { get; set; } = "default";
        public string? SceneName   { get; set; }

        // Session role.
        public bool    IsRoomHost   { get; set; }
        public string? JoinedRoomId { get; set; }
        public bool    IsLoadingTimedOut { get; set; }

        public ClientConnection(int id, TcpClient tcpClient, string remote)
        {
            Id        = id;
            TcpClient = tcpClient;
            Remote    = remote;
        }

        public void AttachWriter(StreamWriter writer)
        {
            lock (_writerLock) _writer = writer;
        }

        public bool Send(string line)
        {
            lock (_writerLock)
            {
                if (_writer == null) return false;
                try { _writer.WriteLine(line); return true; }
                catch { return false; }
            }
        }

        public bool SendEnvelope(string eventName, string payload)
        {
            var env = new Envelope
            {
                RoomId    = string.IsNullOrWhiteSpace(JoinedRoomId) ? RoomName : JoinedRoomId,
                RoomName  = RoomName,
                SenderId  = "SERVER",
                EventName = eventName,
                Payload   = payload
            };
            return Send(env.Serialize());
        }

        public void Dispose()
        {
            lock (_writerLock) _writer = null;
            try { TcpClient.Close(); } catch { }
        }
    }
}
