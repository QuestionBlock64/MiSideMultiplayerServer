using System;
using System.Collections.Generic;
using System.Threading;

namespace MiSideMultiplayer.RelayServer
{
    internal static class ConsoleCommands
    {
        private static readonly Dictionary<string, (string Desc, Action<string[]> Run)> _commands =
            new Dictionary<string, (string, Action<string[]>)>(StringComparer.OrdinalIgnoreCase);

        private static Thread? _inputThread;

        public static void Register(string name, string desc, Action<string[]> run)
        {
            _commands[name] = (desc, run);
        }

        public static void Start(Func<bool> isRunning)
        {
            Register("help", "Lists all available commands.", _ =>
            {
                foreach (var kvp in _commands)
                    Log.Info("Console", "  " + kvp.Key.PadRight(12) + kvp.Value.Desc);
            });

            _inputThread = new Thread(() => Loop(isRunning)) { IsBackground = true, Name = "ConsoleInput" };
            _inputThread.Start();
        }

        private static void Loop(Func<bool> isRunning)
        {
            while (isRunning())
            {
                string? line;
                try { line = Console.ReadLine(); }
                catch { return; }

                if (line == null) { Thread.Sleep(200); continue; }
                line = line.Trim();
                if (line.Length == 0) continue;

                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string cmd = parts[0];
                string[] args = parts.Length > 1 ? parts[1..] : Array.Empty<string>();

                if (_commands.TryGetValue(cmd, out var entry))
                {
                    try { entry.Run(args); }
                    catch (Exception ex) { Log.Error("Console", "Command '" + cmd + "' threw: " + ex.Message); }
                }
                else
                {
                    Log.Warn("Console", "Unknown command '" + cmd + "'. Type 'help'.");
                }
            }
        }
    }
}