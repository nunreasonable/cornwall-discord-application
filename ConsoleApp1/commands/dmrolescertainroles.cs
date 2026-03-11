using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CornwallUtilities.config;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;

namespace CornwallUtilities.commands
{
    internal class DmRolesCertainRoles : ApplicationCommandModule
    {
        private const string FallbackGameLink = "https://example.com";

        [SlashCommand("dmrole", "Envia uma DM para um usuário ou para todos os membros de um cargo.")]
        public async Task DmRoleCommand(
            InteractionContext ctx,
            [Option("role", "Cargo para enviar a mensagem (opcional)")] DiscordRole? role = null,
            [Option("user", "Usuário para enviar a mensagem (opcional)")] DiscordUser? user = null,
            [Option("code", "Código a ser incluído na mensagem (pode ser um código do jogo)")] string code = "",
            [Option("message", "Mensagem personalizada a ser enviada (pode conter instruções)")] string messageBody = "")
        {
            await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);

            var config = new JSONReader();
            await config.ReadJSON();

            var gameLink = string.IsNullOrWhiteSpace(config.defaultGameLink)
                ? FallbackGameLink
                : config.defaultGameLink;

            if (ctx.Guild == null)
            {
                var noGuildEmbed = new DiscordEmbedBuilder()
                    .WithTitle("Erro")
                    .WithDescription("Este comando só pode ser usado em servidores (guilds).")
                    .WithColor(DiscordColor.IndianRed);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(noGuildEmbed));
                return;
            }

            if (role == null && user == null)
            {
                var missingEmbed = new DiscordEmbedBuilder()
                    .WithTitle("Parâmetros insuficientes")
                    .WithDescription("Você deve fornecer **um cargo** ou **um usuário** para enviar a mensagem.")
                    .WithColor(DiscordColor.IndianRed);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(missingEmbed));
                return;
            }

            List<DiscordMember> membersToDm;

            if (user != null)
            {
                // Prefer fetching guild member if the user is in this guild; otherwise fall back to DMing the user object.
                try
                {
                    var guildUser = await ctx.Guild.GetMemberAsync(user.Id);
                    membersToDm = new List<DiscordMember> { guildUser };
                }
                catch
                {
                    membersToDm = new List<DiscordMember>();
                }
            }
            else
            {
                membersToDm = ctx.Guild.Members.Values
                    .Where(m => role != null && m.Roles.Contains(role) && !m.IsBot)
                    .ToList();
            }

            if (user != null)
            {
                // Ensure the user is a member of this guild (only members can be DMed via DiscordMember).
                DiscordMember? guildMember = null;
                try
                {
                    guildMember = await ctx.Guild.GetMemberAsync(user.Id);
                }
                catch
                {
                    var notMemberEmbed = new DiscordEmbedBuilder()
                        .WithTitle("Usuário não encontrado")
                        .WithDescription("O usuário fornecido não é um membro deste servidor.")
                        .WithColor(DiscordColor.IndianRed);

                    await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(notMemberEmbed));
                    return;
                }

                membersToDm = new List<DiscordMember> { guildMember };
            }

            if (membersToDm.Count == 0)
            {
                var emptyEmbed = new DiscordEmbedBuilder()
                    .WithTitle("Nenhum membro encontrado")
                    .WithDescription($"Não foi encontrado nenhum membro com o cargo **{role?.Name}** (excluindo bots).")
                    .WithColor(DiscordColor.IndianRed);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(emptyEmbed));
                return;
            }

            const int maxMembersToProcess = 500;
            if (membersToDm.Count > maxMembersToProcess)
            {
                var tooManyEmbed = new DiscordEmbedBuilder()
                    .WithTitle("Cargo muito grande")
                    .WithDescription($"O cargo **{role?.Name}** possui {membersToDm.Count} membros. Para evitar timeouts ou rate limits, limite o envio a {maxMembersToProcess} membros por execução.")
                    .WithColor(DiscordColor.IndianRed);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(tooManyEmbed));
                return;
            }

            var targetName = user != null
                ? $"{membersToDm[0].Username}#{membersToDm[0].Discriminator}"
                : (role?.Name ?? "destinatário");

            var dmEmbed = new DiscordEmbedBuilder()
                .WithTitle($"Mensagem para {targetName}")
                .WithColor(DiscordColor.Blurple)
                .AddField("Código", string.IsNullOrWhiteSpace(code) ? "(nenhum)" : code, true)
                .AddField("Link do jogo", gameLink, false)
                .AddField("Mensagem", string.IsNullOrWhiteSpace(messageBody) ? "(nenhuma mensagem extra)" : messageBody, false)
                .WithTimestamp(DateTimeOffset.UtcNow);

            var sent = 0;
            var failed = 0;
            var failedUsers = new List<string>();

            foreach (var member in membersToDm)
            {
                try
                {
                    var dmChannel = await member.CreateDmChannelAsync();
                    await dmChannel.SendMessageAsync(embed: dmEmbed);
                    sent++;
                }
                catch (Exception)
                {
                    failed++;
                    failedUsers.Add($"{member.Username}#{member.Discriminator}");
                }

                // Small delay to reduce the chance of hitting global rate limits
                await Task.Delay(1200);
            }

            var summary = new DiscordEmbedBuilder()
                .WithTitle("Envio finalizado")
                .WithColor(DiscordColor.Green)
                .AddField("Alvo", targetName, true)
                .AddField("Total de destinatários", membersToDm.Count.ToString(), true)
                .AddField("Mensagens enviadas", sent.ToString(), true)
                .AddField("Falhas", failed.ToString(), true)
                .WithTimestamp(DateTimeOffset.UtcNow);

            if (failed > 0)
            {
                var failedList = string.Join(", ", failedUsers.Take(10));
                summary.WithDescription($"Falha ao enviar para {failed} usuário(s). Exemplo: {failedList}{(failed > 10 ? "..." : "")}");
            }

            await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(summary));
        }
    }
}
