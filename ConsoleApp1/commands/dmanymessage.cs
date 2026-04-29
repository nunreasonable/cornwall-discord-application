using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CornwallUtilities;
using CornwallUtilities.config;
using DisCatSharp;
using DisCatSharp.Entities;
using DisCatSharp.ApplicationCommands;
using DisCatSharp.Enums;
using DisCatSharp.ApplicationCommands.Context;
using DisCatSharp.ApplicationCommands.Attributes;

namespace CornwallUtilities.commands
{
    internal class DmAnyMessage : ApplicationCommandsModule
    {
        /// <summary>Mensagem curta em PT-BR para falha de DM (ex.: 403 = usuário não aceita DMs).</summary>
        private static string DmFailureReason(Exception ex)
        {
            var msg = ex.Message ?? "";
            if (msg.Contains("403") || msg.Contains("50007") || msg.Contains("Cannot send messages", StringComparison.OrdinalIgnoreCase))
                return "DMs desativadas ou bot bloqueado";
            return msg.Length > 80 ? msg[..77] + "..." : msg;
        }
        [SlashCommand("dmreminder", "Envia uma DM para um usuário ou para todos os membros de um cargo (sem código/link).")]
        public async Task DmRoleMessageCommand(
            InteractionContext ctx,
            [Option("role", "Cargo para enviar a mensagem (opcional)")] DiscordRole? role = null,
            [Option("user", "Usuário para enviar a mensagem (opcional)")] DiscordUser? user = null,
            [Option("message", "Mensagem personalizada a ser enviada")]
            string messageBody = "")
        {
            await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);

            var config = new JSONReader();
            await config.ReadJSON();

            if (ctx.Guild is null)
            {
                var noGuildEmbed = new DiscordEmbedBuilder()
                    .WithTitle("Erro")
                    .WithDescription("Este comando só pode ser usado em servidores (guilds).")
                    .WithColor(DiscordColor.IndianRed);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(noGuildEmbed));
                return;
            }

            if (!config.enlistPermissionRoleId.HasValue)
            {
                var missingConfig = new DiscordEmbedBuilder()
                    .WithTitle("Configuração inválida")
                    .WithDescription("O ID do cargo com permissão para usar este comando não está configurado. Verifique o arquivo config.json (enlistPermissionRoleId).")
                    .WithColor(DiscordColor.IndianRed);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(missingConfig));
                return;
            }

            var requiredRoleId = config.enlistPermissionRoleId.Value;
            var hasPermission = ctx.Member?.Roles.Any(r => r.Id == requiredRoleId) ?? false;
            if (!hasPermission)
            {
                var permEmbed = new DiscordEmbedBuilder()
                    .WithTitle("Permissão negada")
                    .WithDescription("Você não possui o cargo necessário para usar este comando.")
                    .WithColor(DiscordColor.IndianRed);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(permEmbed));
                return;
            }

            if (role is null && user is null)
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

            if (user is not null)
            {
                // Ensure the user is in this guild
                try
                {
                    var guildMember = await ctx.Guild.GetMemberAsync(user.Id);
                    membersToDm = new List<DiscordMember> { guildMember };
                    targetName = guildMember.DisplayName ?? guildMember.Username;
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
                // GetAllMembersAsync busca todos os membros na API do Discord; o cache (Members) só tem uma parte.
                var allMembers = await ctx.Guild.GetAllMembersAsync();
                membersToDm = allMembers
                    .Where(m => role is not null && m.Roles.Contains(role) && !m.IsBot)
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

            // Mensagem só no content para links terem preview; embed só título para não duplicar texto.
            var content = string.IsNullOrWhiteSpace(messageBody)
                ? "(nenhuma mensagem fornecida)"
                : (messageBody.Length <= 2000 ? messageBody : messageBody[..1997] + "...");
            var dmEmbed = new DiscordEmbedBuilder()
                .WithTitle($"Mensagem para {targetName}")
                .WithColor(DiscordColor.Blurple)
                .WithTimestamp(DateTimeOffset.UtcNow);

            var sent = 0;
            var failed = 0;
            var failedUsers = new List<string>();

            var builtEmbed = dmEmbed.Build();

            foreach (var member in membersToDm)
            {
                await DmRateLimiter.WaitForSlotAsync();

                try
                {
                    var dmChannel = await member.CreateDmChannelAsync();
                    await dmChannel.SendMessageAsync(new DiscordMessageBuilder()
                        .WithContent(content)
                        .AddEmbed(builtEmbed));
                    sent++;
                }
                catch (Exception ex)
                {
                    failed++;
                    failedUsers.Add($"{member.DisplayName ?? member.Username} ({DmFailureReason(ex)})");
                }

                // Space out sends so we don't burst when under the 10/3min limit
                await Task.Delay(1200);
            }

            var summary = new DiscordEmbedBuilder()
                .WithTitle("Envio finalizado")
                .WithColor(DiscordColor.Green)
                .AddField(new DiscordEmbedField("Alvo", targetName, true))
                .AddField(new DiscordEmbedField("Total de destinatários", membersToDm.Count.ToString(), true))
                .AddField(new DiscordEmbedField("Mensagens enviadas", sent.ToString(), true))
                .AddField(new DiscordEmbedField("Falhas", failed.ToString(), true))
                .WithTimestamp(DateTimeOffset.UtcNow);

            if (failed > 0)
            {
                var failedList = string.Join(", ", failedUsers.Take(10));
                var hint = failed == membersToDm.Count
                    ? " Ninguém recebeu: verifique se os destinatários permitem DMs de membros do servidor (Configurações do usuário > Privacidade)."
                    : "";
                summary.WithDescription($"Falha ao enviar para {failed} usuário(s). Exemplo: {failedList}{(failed > 10 ? "..." : "")}.{hint}");
            }

            await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(summary));
        }
    }
}
