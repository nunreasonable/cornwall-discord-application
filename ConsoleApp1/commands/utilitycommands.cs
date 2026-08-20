using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CornwallUtilities.config;
using CornwallUtilities.Services;
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

            // A coleta vive em BotStatusSnapshot porque o /api/status precisa
            // exatamente dos mesmos numeros; manter duas copias faria o comando
            // e a pagina de status divergirem no primeiro ajuste.
            var embed = new DiscordEmbedBuilder()
                .WithTitle("Informações do bot")
                .WithColor(DiscordColor.Blurple)
                .AddField(new DiscordEmbedField("Sistema", Block(BotStatusSnapshot.SystemLines()), false))
                .AddField(new DiscordEmbedField("Processo", Block(BotStatusSnapshot.ProcessLines()), false))
                .AddField(new DiscordEmbedField("Discord", Block(BotStatusSnapshot.DiscordLines(ctx.Client)), false))
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
