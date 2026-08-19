using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MiSideMultiplayer.RelayServer
{
    /// <summary>
    /// Mirrors server logs to a Discord channel via an incoming webhook.
    /// This intentionally avoids a full bot-client dependency (no DSharpPlus/Discord.Net) -
    /// a webhook covers the "post log lines to a channel" use case from SmoOnlineServer's
    /// DiscordBot without pulling in a gateway connection, since the relay never needs to
    /// read messages back or respond to commands via Discord.
    /// </summary>
    internal static class DiscordBot
    {
        private static readonly Logger Log = new Logger("Discord", ConsoleColor.DarkMagenta);
        private static readonly HttpClient Http = new HttpClient();
        private static readonly BlockingCollection<string> Queue = new BlockingCollection<string>(1000);
        private static Thread? senderThread;
        private static volatile bool running;

        public static void Start()
        {
            string? webhookUrl = Settings.Instance.Discord.WebhookUrl;

            if (string.IsNullOrWhiteSpace(webhookUrl))
            {
                Log.Info("No Discord webhook URL configured in settings.json (Discord.WebhookUrl) - Discord logging disabled.");
                return;
            }

            running = true;
            senderThread = new Thread(() => SendLoop(webhookUrl))
            {
                IsBackground = true,
                Name = "DiscordLogSender"
            };
            senderThread.Start();

            LogLevel minimumLevel = Settings.ParseLogLevel(Settings.Instance.Discord.MinimumLevel, LogLevel.Info);

            Logger.AddGlobalLogHandler((level, source, message) =>
            {
                if (level < minimumLevel)
                    return;

                // Don't forward the Discord logger's own lines - avoids feedback loops if a webhook post fails.
                if (source == "Discord")
                    return;

                string prefixed = Logger.PrefixNewLines(message, level + " [" + source + "]");
                Queue.TryAdd(prefixed, 0);
            });

            Log.Info("Discord log mirroring enabled (minimum level: " + minimumLevel + ").");
        }

        public static void Stop()
        {
            running = false;
            Queue.CompleteAdding();
        }

        private static void SendLoop(string webhookUrl)
        {
            foreach (string message in Queue.GetConsumingEnumerable())
            {
                if (!running)
                    break;

                foreach (string chunk in SplitMessage(message, 1900))
                {
                    try
                    {
                        SendWebhookMessage(webhookUrl, chunk).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        // Deliberately does not use Log.Error here for delivery failures - that would
                        // re-enter the log pipeline and could loop. Write directly to console instead.
                        Console.Error.WriteLine("[Discord] Failed to deliver webhook message: " + ex.Message);
                    }
                }
            }
        }

        private static async Task SendWebhookMessage(string webhookUrl, string content)
        {
            var payload = new { content = "```" + content + "```" };
            string json = JsonSerializer.Serialize(payload);

            using var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync(webhookUrl, httpContent);

            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync();
                Console.Error.WriteLine("[Discord] Webhook returned " + (int)response.StatusCode + ": " + body);
            }
        }

        /// <summary>Splits a message into Discord-safe chunks, leaving room for the surrounding code-block fence.</summary>
        private static System.Collections.Generic.IEnumerable<string> SplitMessage(string text, int maxLength)
        {
            if (text.Length <= maxLength)
            {
                yield return text;
                yield break;
            }

            int offset = 0;
            while (offset < text.Length)
            {
                int length = Math.Min(maxLength, text.Length - offset);
                yield return text.Substring(offset, length);
                offset += length;
            }
        }
    }
}
