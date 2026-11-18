using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Exiled.API.Features;
using LabApi.Events.Arguments.ServerEvents;
using LabApi.Events.Handlers;
using Newtonsoft.Json;
using LabPlayer = LabApi.Features.Wrappers.Player;

namespace RALogger
{
    public class Plugin : Plugin<Config>
    {
        public override string Name => "RALogger";
        public override string Author => "AdminTools";
        public override Version Version => new Version(1, 5, 0);
        public override Version RequiredExiledVersion => new Version(9, 0, 0);

        private static readonly HttpClient HttpClient = new HttpClient();
        private static readonly object FileLock = new object();
        private readonly ConcurrentQueue<object> _embedQueue = new ConcurrentQueue<object>();
        private CancellationTokenSource _cts;

        private static readonly HashSet<string> TargetCommands = new HashSet<string>
        {
            "give", "forceclass", "fc", "ban", "kick", "hp", "sethp", "god",
            "noclip", "kill", "jail", "unjail", "mute", "unmute", "imute",
            "iunmute", "overwatch", "bypass", "heal", "tp", "bring", "goto"
        };

        public override void OnEnabled()
        {
            _cts = new CancellationTokenSource();
            ServerEvents.CommandExecuting += OnCommandExecuting;
            _ = ProcessWebhookQueueAsync(_cts.Token);

            Log.Info("RALogger zostal wlaczony! Kompaktowe powiadomienia Discord aktywne.");
            base.OnEnabled();
        }

        public override void OnDisabled()
        {
            ServerEvents.CommandExecuting -= OnCommandExecuting;
            _cts?.Cancel();
            base.OnDisabled();
        }

        private void OnCommandExecuting(CommandExecutingEventArgs ev)
        {
            if (ev.CommandType != LabApi.Features.Enums.CommandType.RemoteAdmin)
                return;

            LabPlayer admin = ev.Sender != null ? LabPlayer.Get(ev.Sender) : null;

            if (admin == null && Config.IgnoreServerConsole)
                return;

            string adminGroup = admin?.GroupName ?? "Host";

            // 1. Pomijanie wykluczonych rang z configu
            if (Config.IgnoredGroups != null && Config.IgnoredGroups.Any(g => g.Equals(adminGroup, StringComparison.OrdinalIgnoreCase)))
                return;

            string commandName = ev.CommandName.ToLower();

            // 2. Biała lista komend (loguj TYLKO te komendy, które są wpisane w LoggedCommands)
            if (Config.LoggedCommands != null && Config.LoggedCommands.Count > 0)
            {
                bool isAllowedToLog = Config.LoggedCommands.Any(c => c.Equals(commandName, StringComparison.OrdinalIgnoreCase));
                if (!isAllowedToLog)
                    return;
            }

            // 3. Opcjonalne pomijanie zablokowanych komend (gdy ktoś kliknie bez uprawnień)
            if (!ev.IsAllowed && !Config.LogBlockedCommands)
                return;

            string adminNick = admin?.Nickname ?? ev.Sender?.Nickname ?? "Konsola Serwera";
            string rawId = admin?.UserId ?? ev.Sender?.SenderId ?? "CONSOLE";
            string fullCommand = ev.Arguments.Count > 0
                ? $"{ev.CommandName} {string.Join(" ", ev.Arguments)}"
                : ev.CommandName;
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            string actionDetails = ResolveCommandDetails(commandName, ev.Arguments);

            if (Config.SaveToLocalFile)
            {
                string logExtra = string.IsNullOrEmpty(actionDetails) ? "" : $" | Szczegoly: {actionDetails.Replace("`", "").Replace("*", "").Replace("\n", " ")}";
                string logLine = $"[{timestamp}] [{adminGroup}] {adminNick} ({rawId}) uzyl: {fullCommand}{logExtra}";
                _ = Task.Run(() => SaveLocalLog(logLine));
            }

            if (!string.IsNullOrWhiteSpace(Config.DiscordWebhookUrl) && Config.DiscordWebhookUrl != "WPISZ_TUTAJ_WEBHOOK")
            {
                var embed = BuildEmbed(adminNick, rawId, adminGroup, fullCommand, actionDetails, ev.IsAllowed);
                _embedQueue.Enqueue(embed);
            }
        }

        private object BuildEmbed(string nick, string userId, string group, string command, string actionDetails, bool isAllowed)
        {
            int embedColor = isAllowed ? 15105570 : 15158332; // Pomarańczowy dla wykonanych, Czerwony dla zablokowanych
            long unixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string formattedUser = FormatUserField(nick, userId);
            string cleanServerName = string.IsNullOrWhiteSpace(Config.ServerDisplayName)
                ? CleanServerName(Server.Name)
                : Config.ServerDisplayName;
            string statusIcon = isAllowed ? "✅" : "⛔";

            StringBuilder desc = new StringBuilder();
            desc.Append($"{statusIcon} **`{command}`**\n");
            desc.Append($"👤 {formattedUser} • 🎖️ `{group}` • <t:{unixTime}:t>");

            if (!string.IsNullOrEmpty(actionDetails))
            {
                string compactDetails = actionDetails.Replace("\n", " • ");
                desc.Append($"\n{compactDetails}");
            }

            return new
            {
                color = embedColor,
                description = desc.ToString(),
                footer = new
                {
                    text = cleanServerName
                }
            };
        }

        private async Task ProcessWebhookQueueAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, Config.BatchIntervalSeconds)), token);

                    if (_embedQueue.IsEmpty)
                        continue;

                    List<object> batch = new List<object>();
                    while (batch.Count < 10 && _embedQueue.TryDequeue(out object item))
                    {
                        batch.Add(item);
                    }

                    if (batch.Count > 0)
                    {
                        await SendBatchToDiscordAsync(batch);
                    }
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error($"Blad w kolejce webhooka RA: {ex.Message}");
                }
            }
        }

        private async Task SendBatchToDiscordAsync(List<object> embeds)
        {
            try
            {
                var payload = new
                {
                    username = Config.WebhookName,
                    embeds = embeds.ToArray()
                };

                string json = JsonConvert.SerializeObject(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                HttpResponseMessage response = await HttpClient.PostAsync(Config.DiscordWebhookUrl, content);
                if (!response.IsSuccessStatusCode)
                {
                    Log.Warn($"Discord zwrocil blad podczas wysylania paczki logow RA: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Blad wysylania webhooka RA: {ex.Message}");
            }
        }

        private string ResolveCommandDetails(string cmd, IReadOnlyList<string> args)
        {
            if (args == null || args.Count == 0 || !TargetCommands.Contains(cmd))
                return string.Empty;

            string[] targetIds = args[0].Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            List<string> resolvedPlayers = new List<string>();

            foreach (string idStr in targetIds)
            {
                if (int.TryParse(idStr, out int playerId))
                {
                    LabPlayer target = LabPlayer.Get(playerId);
                    if (target != null)
                    {
                        string shortId = target.UserId.Replace("@steam", "");
                        resolvedPlayers.Add($"**{target.Nickname}** (`{shortId}`)");
                    }
                }
            }

            if (resolvedPlayers.Count == 0)
                return string.Empty;

            StringBuilder sb = new StringBuilder();
            sb.Append($"🎯 **Cel:** {string.Join(", ", resolvedPlayers)}");

            if (cmd == "give" && args.Count >= 2)
            {
                string[] itemArgs = args[1].Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
                List<string> itemNames = new List<string>();

                foreach (string itemStr in itemArgs)
                {
                    if (Enum.TryParse(itemStr, true, out ItemType itemType) && itemType != ItemType.None)
                    {
                        itemNames.Add(itemType.ToString());
                    }
                    else
                    {
                        itemNames.Add(itemStr);
                    }
                }

                if (itemNames.Count > 0)
                {
                    sb.Append($"\n🎒 **Przedmiot:** `{string.Join(", ", itemNames)}`");
                }
            }
            else if ((cmd == "forceclass" || cmd == "fc") && args.Count >= 2)
            {
                sb.Append($"\n🎭 **Rola:** `{args[1]}`");
            }

            return sb.ToString();
        }

        private void SaveLocalLog(string line)
        {
            try
            {
                lock (FileLock)
                {
                    string folderPath = Path.Combine(Paths.Configs, "RALogs");
                    if (!Directory.Exists(folderPath))
                        Directory.CreateDirectory(folderPath);

                    string filePath = Path.Combine(folderPath, $"ra-logs-{DateTime.Now:yyyy-MM-dd}.txt");
                    File.AppendAllText(filePath, line + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Blad podczas zapisu lokalnego logu RA: {ex.Message}");
            }
        }

        private string FormatUserField(string nick, string userId)
        {
            if (userId.EndsWith("@steam"))
            {
                string steam64 = userId.Replace("@steam", "");
                return $"**[{nick}](https://steamcommunity.com/profiles/{steam64})** (`{steam64}`)";
            }
            return $"**{nick}** (`{userId}`)";
        }

        private string CleanServerName(string rawName)
        {
            if (string.IsNullOrEmpty(rawName))
                return "Serwer SCP:SL";

            string noTags = Regex.Replace(rawName, "<.*?>", string.Empty);
            int pipeIndex = noTags.IndexOf('|');
            if (pipeIndex > 0)
            {
                noTags = noTags.Substring(0, pipeIndex);
            }

            return noTags.Trim();
        }
    }

    public class Config : Exiled.API.Interfaces.IConfig
    {
        [Description("Czy plugin ma byc wlaczony?")]
        public bool IsEnabled { get; set; } = true;

        [Description("Tryb debugowania.")]
        public bool Debug { get; set; } = false;

        [Description("Link do Webhooka Discord, na ktory beda wysylane logi.")]
        public string DiscordWebhookUrl { get; set; } = "WPISZ_TUTAJ_WEBHOOK";

        [Description("Nazwa bota wyswietlana na Discordzie.")]
        public string WebhookName { get; set; } = "SCP:SL - Kontrola RA";

        [Description("Wlasna nazwa serwera w stopce (zostaw puste, aby pobrac automatycznie i wyczyscic z tagow).")]
        public string ServerDisplayName { get; set; } = "";

        [Description("Co ile sekund wysylac zebrana paczke logow na Discorda.")]
        public int BatchIntervalSeconds { get; set; } = 3;

        [Description("Czy logowac takze zablokowane proby uzycia komend (czerwony pasek)?")]
        public bool LogBlockedCommands { get; set; } = true;

        [Description("Lista nazw rang (z config_remoteadmin.txt), ktore nie beda monitorowane.")]
        public List<string> IgnoredGroups { get; set; } = new List<string>
        {
            "owner",
            "headadmin"
        };

        [Description("BIALA LISTA: Logowane beda TYLKO komendy z tej listy. Usun lub dodaj dowolna komende.")]
        public List<string> LoggedCommands { get; set; } = new List<string>
        {
            "give",
            "forceclass",
            "fc",
            "ban",
            "kick",
            "hp",
            "sethp",
            "god",
            "noclip",
            "kill",
            "jail",
            "unjail",
            "overwatch",
            "bypass",
            "tp",
            "bring",
            "goto"
        };

        [Description("Czy zapisywac kopie logow rowniez w pliku tekstowym w folderze Configs/RALogs?")]
        public bool SaveToLocalFile { get; set; } = true;

        [Description("Czy ignorowac komendy wpisywane bezposrednio z konsoli serwera?")]
        public bool IgnoreServerConsole { get; set; } = true;
    }
}