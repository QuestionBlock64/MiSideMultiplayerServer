using System;
using System.Collections.Generic;
using System.Threading;

namespace MiSideMultiplayer.RelayServer
{
    internal delegate void CommandAction(string[] args);

    internal static class CommandHandler
    {
        private static readonly Logger Log = new Logger("Console", ConsoleColor.White);
        private static readonly Dictionary<string, (string Description, CommandAction Action)> Commands =
            new Dictionary<string, (string, CommandAction)>(StringComparer.OrdinalIgnoreCase);

        private static Thread? inputThread;

        public static void RegisterCommand(string name, string description, CommandAction action)
        {
            Commands[name] = (description, action);
        }

        public static bool TryExecute(string name, string[] args)
        {
            if (Commands.TryGetValue(name, out var cmd))
            {
                try
                {
                    cmd.Action(args);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
            return false;
        }

        public static void Start(Func<bool> isRunning)
        {
            RegisterCommand("help", "Lists all available commands.", _ =>
            {
                foreach (var kvp in Commands)
                    Log.Info("  " + kvp.Key.PadRight(14) + kvp.Value.Description);
            });

            inputThread = new Thread(() => RunLoop(isRunning))
            {
                IsBackground = true,
                Name = "ConsoleInput"
            };
            inputThread.Start();
        }

        private static void RunLoop(Func<bool> isRunning)
        {
            while (isRunning())
            {
                string? line;

                try
                {
                    line = Console.ReadLine();
                }
                catch (Exception)
                {
                    return;
                }

                if (line == null)
                {
                    Thread.Sleep(250);
                    continue;
                }

                line = line.Trim();
                if (line.Length == 0)
                    continue;

                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string commandName = parts[0];
                string[] args = parts.Length > 1 ? parts[1..] : Array.Empty<string>();

                if (Commands.TryGetValue(commandName, out var command))
                {
                    try
                    {
                        command.Action(args);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Command '" + commandName + "' threw an exception.", ex);
                    }
                }
                else
                {
                    Log.Warn("Unknown command '" + commandName + "'. Type 'help' for a list of commands.");
                }
            }
        }
    }
}