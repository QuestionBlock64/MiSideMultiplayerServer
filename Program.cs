using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace MiSideMultiplayer.RelayServer
{
    internal static class Program
    {
        private static readonly ConcurrentDictionary<int, ClientConnection> Clients =
            new ConcurrentDictionary<int, ClientConnection>();

        private static int nextClientId;
        private static volatile bool isRunning = true;

        private static int Main(string[] args)
        {
            int port = ParsePort(args, 7777);
            TcpListener listener = new TcpListener(IPAddress.Any, port);

            Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs eventArgs)
            {
                eventArgs.Cancel = true;
                isRunning = false;
                listener.Stop();
            };

            try
            {
                listener.Start();
                Console.WriteLine("MiSide Multiplayer Relay listening on 0.0.0.0:" + port);
                Console.WriteLine("Clients should set Networking.ServerHost to this machine's LAN IP.");

                while (isRunning)
                {
                    TcpClient tcpClient = listener.AcceptTcpClient();
                    tcpClient.NoDelay = true;

                    int id = Interlocked.Increment(ref nextClientId);
                    ClientConnection connection = new ClientConnection(id, tcpClient);
                    Clients[id] = connection;

                    Console.WriteLine("Client #" + id + " connected.");
                    ThreadPool.QueueUserWorkItem(delegate { HandleClient(connection); });
                }

                return 0;
            }
            catch (SocketException)
            {
                return isRunning ? 1 : 0;
            }
            finally
            {
                isRunning = false;
                listener.Stop();

                foreach (ClientConnection client in Clients.Values)
                    client.Dispose();
            }
        }

        private static void HandleClient(ClientConnection connection)
        {
            try
            {
                using (NetworkStream stream = connection.TcpClient.GetStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true))
                {
                    writer.NewLine = "\n";
                    writer.AutoFlush = true;
                    connection.AttachWriter(writer);

                    while (isRunning && connection.TcpClient.Connected)
                    {
                        string line = reader.ReadLine();
                        if (line == null)
                            break;

                        Broadcast(connection.Id, line);
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
            finally
            {
                ClientConnection removed;
                Clients.TryRemove(connection.Id, out removed);
                connection.Dispose();
                Console.WriteLine("Client #" + connection.Id + " disconnected.");
            }
        }

        private static void Broadcast(int senderId, string line)
        {
            foreach (ClientConnection client in Clients.Values)
            {
                if (client.Id == senderId)
                    continue;

                client.TrySend(line);
            }
        }

        private static int ParsePort(string[] args, int fallback)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if ((args[i] == "--port" || args[i] == "-p") && i + 1 < args.Length)
                {
                    int parsed;
                    if (int.TryParse(args[i + 1], out parsed) && parsed > 0)
                        return parsed;
                }
            }

            return fallback;
        }

        private sealed class ClientConnection : IDisposable
        {
            private readonly object writerLock = new object();
            private StreamWriter writer;

            public int Id { get; private set; }
            public TcpClient TcpClient { get; private set; }

            public ClientConnection(int id, TcpClient tcpClient)
            {
                Id = id;
                TcpClient = tcpClient;
            }

            public void AttachWriter(StreamWriter streamWriter)
            {
                lock (writerLock)
                    writer = streamWriter;
            }

            public bool TrySend(string line)
            {
                lock (writerLock)
                {
                    if (writer == null)
                        return false;

                    try
                    {
                        writer.WriteLine(line);
                        return true;
                    }
                    catch (IOException)
                    {
                        return false;
                    }
                    catch (ObjectDisposedException)
                    {
                        return false;
                    }
                }
            }

            public void Dispose()
            {
                lock (writerLock)
                    writer = null;

                if (TcpClient != null)
                {
                    TcpClient.Close();
                    TcpClient = null;
                }
            }
        }
    }
}
