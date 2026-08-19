using System;
using System.Collections.Generic;
using System.Threading;

namespace MiSideMultiplayer.RelayServer
{
    internal enum LogLevel
    {
        Debug,
        Info,
        Warn,
        Error,
        Fatal
    }

    internal delegate void LogHandler(LogLevel level, string source, string message);

    /// <summary>
    /// Named logger instance. Each subsystem creates its own Logger with a source tag
    /// (e.g. new Logger("Discord"), new Logger("Bans")), mirroring SmoOnlineServer's
    /// per-component logger pattern. Handlers can be attached to forward log lines
    /// elsewhere (e.g. DiscordBot subscribes to mirror logs into a Discord channel).
    /// </summary>
    internal sealed class Logger
    {
        private static readonly object ConsoleLock = new object();
        private static readonly List<LogHandler> GlobalHandlers = new List<LogHandler>();
        private static readonly ThreadLocal<LogHandler?> ThreadHandler =
            new ThreadLocal<LogHandler?>();

        public static LogLevel MinimumLevel = LogLevel.Info;

        public string Source { get; }
        public ConsoleColor SourceColor { get; }

        private readonly List<LogHandler> localHandlers = new List<LogHandler>();

        public Logger(string source, ConsoleColor color = ConsoleColor.White)
        {
            Source = source;
            SourceColor = color;
        }

        /// <summary>Registers a handler that fires for every log line from every Logger instance.</summary>
        public static void AddGlobalLogHandler(LogHandler handler)
        {
            lock (GlobalHandlers)
                GlobalHandlers.Add(handler);
        }

        public static void RemoveGlobalLogHandler(LogHandler handler)
        {
            lock (GlobalHandlers)
                GlobalHandlers.Remove(handler);
        }

        /// <summary>
        /// Captures log entries written on the calling thread only. This is used
        /// for in-game commands so concurrent server activity cannot leak into
        /// a player's private command response.
        /// </summary>
        public static void SetCurrentThreadLogHandler(LogHandler handler)
        {
            ThreadHandler.Value = handler;
        }

        public static void ClearCurrentThreadLogHandler(LogHandler handler)
        {
            if (ThreadHandler.Value == handler)
                ThreadHandler.Value = null;
        }

        /// <summary>Registers a handler that fires only for log lines from this specific source.</summary>
        public void AddLogHandler(LogHandler handler)
        {
            lock (localHandlers)
                localHandlers.Add(handler);
        }

        public void Debug(string message) => Write(LogLevel.Debug, message);
        public void Info(string message) => Write(LogLevel.Info, message);
        public void Warn(string message) => Write(LogLevel.Warn, message);
        public void Error(string message) => Write(LogLevel.Error, message);
        public void Error(string message, Exception ex) => Write(LogLevel.Error, message + Environment.NewLine + FormatException(ex));
        public void Fatal(string message, Exception ex) => Write(LogLevel.Fatal, message + Environment.NewLine + FormatException(ex));

        private static string FormatException(Exception ex)
        {
            return "    " + ex.GetType().Name + ": " + ex.Message +
                   (ex.StackTrace != null ? Environment.NewLine + ex.StackTrace : string.Empty);
        }

        private void Write(LogLevel level, string message)
        {
            if (level < MinimumLevel)
                return;

            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string levelTag = GetLevelTag(level);
            ConsoleColor levelColor = GetLevelColor(level);

            lock (ConsoleLock)
            {
                ConsoleColor previous = Console.ForegroundColor;

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write("[" + timestamp + "] ");

                Console.ForegroundColor = levelColor;
                Console.Write(levelTag);
                Console.Write(" ");

                Console.ForegroundColor = SourceColor;
                Console.Write("[" + Source + "] ");

                Console.ForegroundColor = previous;
                Console.WriteLine(message);
            }

            // Fire local handlers (e.g. this specific source is mirrored to Discord).
            lock (localHandlers)
            {
                foreach (LogHandler handler in localHandlers)
                {
                    try { handler(level, Source, message); }
                    catch { /* a broken handler should never take down logging itself */ }
                }
            }

            LogHandler? threadHandler = ThreadHandler.Value;
            if (threadHandler != null)
            {
                try { threadHandler(level, Source, message); }
                catch { /* a command-output sink must never interrupt logging */ }
            }

            // Fire global handlers (e.g. a catch-all sink).
            lock (GlobalHandlers)
            {
                foreach (LogHandler handler in GlobalHandlers)
                {
                    try { handler(level, Source, message); }
                    catch { /* same as above */ }
                }
            }
        }

        private static string GetLevelTag(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Debug: return "[DEBUG]";
                case LogLevel.Info: return "[INFO] ";
                case LogLevel.Warn: return "[WARN] ";
                case LogLevel.Error: return "[ERROR]";
                case LogLevel.Fatal: return "[FATAL]";
                default: return "[LOG]  ";
            }
        }

        private static ConsoleColor GetLevelColor(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Debug: return ConsoleColor.Gray;
                case LogLevel.Info: return ConsoleColor.Cyan;
                case LogLevel.Warn: return ConsoleColor.Yellow;
                case LogLevel.Error: return ConsoleColor.Red;
                case LogLevel.Fatal: return ConsoleColor.Magenta;
                default: return ConsoleColor.White;
            }
        }

        /// <summary>Helper matching SmoOnlineServer's Logger.PrefixNewLines - prefixes every line of a
        /// multi-line message, used when forwarding formatted blocks (e.g. to Discord).</summary>
        public static string PrefixNewLines(string text, string prefix)
        {
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
                lines[i] = "[" + prefix + "] " + lines[i];
            return string.Join("\n", lines);
        }

        // Pre-defined loggers for the relay's two traffic channels, colored to match
        // the player model (navy hair) and Mita (red sweater/thigh-highs).
        public static readonly Logger Player = new Logger("Player", ConsoleColor.DarkBlue);
        public static readonly Logger Mita = new Logger("Mita", ConsoleColor.Red);
        public static readonly Logger Server = new Logger("Server", ConsoleColor.White);
    }
}
