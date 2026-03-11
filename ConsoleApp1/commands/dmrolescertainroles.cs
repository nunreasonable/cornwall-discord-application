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

        /// <summary>Mensagem curta em PT-BR para falha de DM (ex.: 403 = usuário não aceita DMs).</summary>
        private static string DmFailureReason(Exception ex)
        {
            var msg = ex.Message ?? "";
            if (msg.Contains("403") || msg.Contains("50007") || msg.Contains("Cannot send messages", StringComparison.OrdinalIgnoreCase))
                return "DMs desativadas ou bot bloqueado";
            return msg.Length > 80 ? msg[..77] + "..." : msg;
        }

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
                DiscordMember guildMember;
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
            else
            {
                // GetAllMembersAsync busca todos os membros na API do Discord; o cache (Members) só tem uma parte.
                var allMembers = await ctx.Guild.GetAllMembersAsync();
                membersToDm = allMembers
                    .Where(m => role != null && m.Roles.Contains(role) && !m.IsBot)
                    .ToList();
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

            // Link só no content para o Discord mostrar o preview; não duplicar no embed.
            var content = string.IsNullOrWhiteSpace(gameLink) ? null : gameLink;
            var dmEmbed = new DiscordEmbedBuilder()
                .WithTitle($"Mensagem para {targetName}")
                .WithColor(DiscordColor.Blurple)
                .AddField("Código", string.IsNullOrWhiteSpace(code) ? "(nenhum)" : code, true)
                .AddField("Mensagem", string.IsNullOrWhiteSpace(messageBody) ? "(nenhuma mensagem extra)" : messageBody, false)
                .WithTimestamp(DateTimeOffset.UtcNow);

            var sent = 0;
            var failed = 0;
            var failedUsers = new List<string>();

            var builtEmbed = dmEmbed.Build();

            foreach (var member in membersToDm)
            {
                try
                {
                    var dmChannel = await member.CreateDmChannelAsync();
                    var messageBuilder = new DiscordMessageBuilder().AddEmbed(builtEmbed);
                    if (!string.IsNullOrEmpty(content))
                        messageBuilder.WithContent(content);
                    await dmChannel.SendMessageAsync(messageBuilder);
                    sent++;
                }
                catch (Exception ex)
                {
                    failed++;
                    failedUsers.Add($"{member.Username}#{member.Discriminator} ({DmFailureReason(ex)})");
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
                var hint = failed == membersToDm.Count
                    ? " Ninguém recebeu: verifique se os destinatários permitem DMs de membros do servidor (Configurações do usuário > Privacidade)."
                    : "";
                summary.WithDescription($"Falha ao enviar para {failed} usuário(s). Exemplo: {failedList}{(failed > 10 ? "..." : "")}.{hint}");
            }

            await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(summary));
        }
    }
}
