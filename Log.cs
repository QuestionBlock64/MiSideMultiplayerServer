using System;
using System.Collections.Generic;

namespace MiSideMultiplayer.RelayServer
{
    internal enum LogLevel { Debug, Info, Warn, Error, Fatal }

    internal static class Log
    {
        private static readonly object ConsoleLock = new object();
        private static readonly List<Action<LogLevel, string, string>> Handlers =
            new List<Action<LogLevel, string, string>>();

        public static LogLevel MinimumLevel = LogLevel.Info;

        public static void AddHandler(Action<LogLevel, string, string> handler)
        {
            lock (Handlers) Handlers.Add(handler);
        }

        public static void Debug(string source, string message) => Write(LogLevel.Debug, source, message);
        public static void Info(string source, string message)  => Write(LogLevel.Info, source, message);
        public static void Warn(string source, string message)  => Write(LogLevel.Warn, source, message);
        public static void Error(string source, string message) => Write(LogLevel.Error, source, message);

        public static void Fatal(string source, string message, Exception ex)
        {
            Write(LogLevel.Fatal, source,
                message + Environment.NewLine + "    " + ex.GetType().Name + ": " + ex.Message +
                (ex.StackTrace != null ? Environment.NewLine + ex.StackTrace : string.Empty));
        }

        private static void Write(LogLevel level, string source, string message)
        {
            if (level < MinimumLevel) return;

            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string levelTag  = GetLevelTag(level);
            ConsoleColor color = GetLevelColor(level);

            lock (ConsoleLock)
            {
                ConsoleColor previous = Console.ForegroundColor;
                try
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write("[" + timestamp + "] ");

                    Console.ForegroundColor = color;
                    Console.Write(levelTag + " ");

                    Console.ForegroundColor = ConsoleColor.DarkCyan;
                    Console.Write("[" + source + "] ");

                    Console.ForegroundColor = previous;
                    Console.WriteLine(message);
                    Console.Out.Flush();
                }
                catch
                {
                    // never let logging take down the thread
                }
                finally
                {
                    try { Console.ForegroundColor = previous; } catch { }
                }
            }

            lock (Handlers)
            {
                foreach (var h in Handlers)
                {
                    try { h(level, source, message); } catch { }
                }
            }
        }

        private static string GetLevelTag(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Debug: return "[DEBUG]";
                case LogLevel.Info:  return "[INFO] ";
                case LogLevel.Warn:  return "[WARN] ";
                case LogLevel.Error: return "[ERROR]";
                case LogLevel.Fatal: return "[FATAL]";
                default:             return "[LOG]  ";
            }
        }

        private static ConsoleColor GetLevelColor(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Debug: return ConsoleColor.Gray;
                case LogLevel.Info:  return ConsoleColor.Cyan;
                case LogLevel.Warn:  return ConsoleColor.Yellow;
                case LogLevel.Error: return ConsoleColor.Red;
                case LogLevel.Fatal: return ConsoleColor.Magenta;
                default:             return ConsoleColor.White;
            }
        }
    }
}