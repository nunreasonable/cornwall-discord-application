using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;

namespace CornwallUtilities.commands
{
    internal class DmAnyMessage : ApplicationCommandModule
    {
        [SlashCommand("dmrolemsg", "Envia uma DM para um usuário ou para todos os membros de um cargo (sem código/link).")]
        public async Task DmRoleMessageCommand(
            InteractionContext ctx,
            [Option("role", "Cargo para enviar a mensagem (opcional)")] DiscordRole? role = null,
            [Option("user", "Usuário para enviar a mensagem (opcional)")] DiscordUser? user = null,
            [Option("message", "Mensagem personalizada a ser enviada")]
            string messageBody = "")
        {
            await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);

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
            var targetName = string.Empty;

            if (user != null)
            {
                // Ensure the user is in this guild
                try
                {
                    var guildMember = await ctx.Guild.GetMemberAsync(user.Id);
                    membersToDm = new List<DiscordMember> { guildMember };
                    targetName = $"{guildMember.Username}#{guildMember.Discriminator}";
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
            }
            else
            {
                membersToDm = ctx.Guild.Members.Values
                    .Where(m => role != null && m.Roles.Contains(role) && !m.IsBot)
                    .ToList();

                targetName = role?.Name ?? "destinatários";
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

            var dmEmbed = new DiscordEmbedBuilder()
                .WithTitle($"Mensagem para {targetName}")
                .WithColor(DiscordColor.Blurple)
                .AddField("Mensagem", string.IsNullOrWhiteSpace(messageBody) ? "(nenhuma mensagem fornecida)" : messageBody, false)
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
