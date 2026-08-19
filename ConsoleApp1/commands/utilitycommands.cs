using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using CornwallUtilities.config;
using DisCatSharp.CommandsNext;
using DisCatSharp.CommandsNext.Attributes;
using DisCatSharp.Entities;

namespace CornwallUtilities.commands
{
    internal class UtilityCommands : BaseCommandModule
    {
        public string userTag = "<@1072212634201505952>";

        [Command("osinfo")]
        [Description("Mostra informações do sistema, do processo e da conexão com o Discord.")]
        public async Task OsInfo(CommandContext ctx)
        {
            // Expoe dados da maquina que hospeda o bot, entao segue o mesmo
            // criterio de staff dos comandos de auditoria em vez de ser aberto.
            var config = new JSONReader();
            await config.ReadJSON();

            var denied = AuditPermissions.CheckStaff(ctx.Guild, ctx.Member, config);
            if (denied is not null)
            {
                await ctx.Channel.SendMessageAsync(new DiscordMessageBuilder()
                    .WithReply(ctx.Message.Id)
                    .AddEmbed(denied));
                return;
            }

            var embed = new DiscordEmbedBuilder()
                .WithTitle("Informações do bot")
                .WithColor(DiscordColor.Blurple)
                .AddField(new DiscordEmbedField("Sistema", Block(SystemLines()), false))
                .AddField(new DiscordEmbedField("Processo", Block(ProcessLines()), false))
                .AddField(new DiscordEmbedField("Discord", Block(DiscordLines(ctx)), false))
                .WithTimestamp(DateTimeOffset.UtcNow);

            var avatar = ctx.Client.CurrentUser?.AvatarUrl;
            if (!string.IsNullOrWhiteSpace(avatar))
                embed.WithThumbnail(avatar);

            await ctx.Channel.SendMessageAsync(new DiscordMessageBuilder()
                .WithReply(ctx.Message.Id)
                .AddEmbed(embed));
        }

        /// <summary>Alinha "rotulo  valor" num bloco monoespacado.</summary>
        private static string Block(IEnumerable<(string Label, string Value)> rows)
        {
            var list = rows.ToList();
            if (list.Count == 0)
                return "```\n(indisponível)\n```";

            var width = list.Max(r => r.Label.Length);
            var sb = new StringBuilder("```\n");
            foreach (var (label, value) in list)
                sb.Append(label.PadRight(width)).Append("  ").Append(value).Append('\n');
            sb.Append("```");
            return sb.ToString();
        }

        private static IEnumerable<(string, string)> SystemLines()
        {
            yield return ("SO", RuntimeInformation.OSDescription);
            yield return ("Arquitetura", $"{RuntimeInformation.OSArchitecture} (processo: {RuntimeInformation.ProcessArchitecture})");
            yield return ("Núcleos", Environment.ProcessorCount.ToString());

            var load = LoadAverage();
            if (load is not null)
                yield return ("Carga média", load);

            yield return ("Uptime da máquina", FormatSpan(TimeSpan.FromMilliseconds(Environment.TickCount64)));

            var disk = DiskUsage();
            if (disk is not null)
                yield return ("Disco", disk);
        }

        private static IEnumerable<(string, string)> ProcessLines()
        {
            yield return (".NET", RuntimeInformation.FrameworkDescription);

            var version = Assembly.GetEntryAssembly()?.GetName().Version;
            if (version is not null)
                yield return ("Versão do bot", version.ToString());

            // O uptime que interessa ao rodar este comando e o do BOT, nao o da
            // maquina: o processo pode ter reiniciado ha um minuto numa maquina
            // ligada ha semanas.
            //
            // A coleta acontece antes dos yields porque C# nao permite yield
            // dentro de um try com catch.
            foreach (var row in CurrentProcessLines())
                yield return row;

            yield return ("Memória do GC", $"{GC.GetTotalMemory(false) / (1024.0 * 1024.0):F1} MB");
        }

        private static List<(string, string)> CurrentProcessLines()
        {
            try
            {
                using var proc = Process.GetCurrentProcess();
                return new List<(string, string)>
                {
                    ("Uptime do bot", FormatSpan(DateTime.Now - proc.StartTime)),
                    ("Memória residente", $"{proc.WorkingSet64 / (1024.0 * 1024.0):F1} MB"),
                    ("Threads", proc.Threads.Count.ToString())
                };
            }
            catch (Exception)
            {
                // Alguns ambientes restringem a leitura do proprio processo; o
                // resto do embed continua util sem esses campos.
                return new List<(string, string)>();
            }
        }

        private static IEnumerable<(string, string)> DiscordLines(CommandContext ctx)
        {
            yield return ("Latência", $"{ctx.Client.Ping} ms");
            yield return ("Servidores", ctx.Client.Guilds.Count.ToString());

            long users = ctx.Client.Guilds.Values.Sum(g => (long)(g.MemberCount ?? 0));
            yield return ("Membros", users.ToString("N0"));

            yield return ("Biblioteca", $"DisCatSharp {ctx.Client.VersionString}");
        }

        /// <summary>Carga média do Linux. Null nos sistemas que não expõem /proc.</summary>
        private static string? LoadAverage()
        {
            try
            {
                if (!File.Exists("/proc/loadavg"))
                    return null;

                var parts = File.ReadAllText("/proc/loadavg").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return parts.Length >= 3 ? $"{parts[0]} {parts[1]} {parts[2]}" : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string? DiskUsage()
        {
            try
            {
                var path = Path.GetPathRoot(AppContext.BaseDirectory);
                if (string.IsNullOrWhiteSpace(path))
                    return null;

                var drive = new DriveInfo(path);
                if (!drive.IsReady)
                    return null;

                var totalGb = drive.TotalSize / (1024.0 * 1024 * 1024);
                var freeGb = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
                var usedPct = totalGb <= 0 ? 0 : (1 - freeGb / totalGb) * 100;
                return $"{freeGb:F1} GB livres de {totalGb:F1} GB ({usedPct:F0}% em uso)";
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string FormatSpan(TimeSpan span)
        {
            if (span < TimeSpan.Zero)
                span = TimeSpan.Zero;

            if (span.TotalDays >= 1)
                return $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes}m";

            return span.TotalHours >= 1
                ? $"{span.Hours}h {span.Minutes}m"
                : $"{span.Minutes}m {span.Seconds}s";
        }

        [Command("vsfdliliane")]
        public async Task Vsfdliliane(CommandContext ctx)
        {
            const ulong allowedUserId = 1402344358199689348;
            if (ctx.User.Id != allowedUserId)
            {
                await ctx.Channel.SendMessageAsync(new DiscordMessageBuilder()
                .WithContent("Você não tem permissão para usar este comando.")
                .WithReply(ctx.Message.Id));
                return;
            }
            var responses = new[]
            {
                $"Vai se fuder {userTag}",
                $"Vai tomar no cu,{userTag} porra",
                $"Caralho, {userTag} burrão hein.",
                $"Vai pra casa do chapéu, {userTag} fdppppp!!!!1!!1.",
            };

            var rand = new Random();
            var response = responses[rand.Next(responses.Length)];
            await ctx.Channel.SendMessageAsync(new DiscordMessageBuilder()
                .WithContent(response)
                .WithReply(ctx.Message.Id));
        }
    }
}