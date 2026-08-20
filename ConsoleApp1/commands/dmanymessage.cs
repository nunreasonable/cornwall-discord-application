using System;
using System.Linq;
using System.Threading.Tasks;
using CornwallUtilities.config;
using CornwallUtilities.Services;
using DisCatSharp.ApplicationCommands;
using DisCatSharp.ApplicationCommands.Attributes;
using DisCatSharp.ApplicationCommands.Context;
using DisCatSharp.Entities;
using DisCatSharp.Enums;

namespace CornwallUtilities.commands
{
    internal class DmAnyMessage : ApplicationCommandsModule
    {
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
                await ReplyErrorAsync(ctx, "Erro", "Este comando só pode ser usado em servidores (guilds).");
                return;
            }

            if (!config.enlistPermissionRoleId.HasValue)
            {
                await ReplyErrorAsync(ctx, "Configuração inválida",
                    "O ID do cargo com permissão para usar este comando não está configurado. Verifique o arquivo config.json (enlistPermissionRoleId).");
                return;
            }

            var requiredRoleId = config.enlistPermissionRoleId.Value;
            if (!(ctx.Member?.Roles.Any(r => r.Id == requiredRoleId) ?? false))
            {
                await ReplyErrorAsync(ctx, "Permissão negada", "Você não possui o cargo necessário para usar este comando.");
                return;
            }

            var (membersToDm, targetName, error) = await MassDmService.ResolveRecipientsAsync(ctx.Guild, role, user);
            if (membersToDm is null)
            {
                await ReplyErrorAsync(ctx, "Envio não realizado", error ?? "Alvo inválido.");
                return;
            }

            // Mensagem só no content para links terem preview; embed só título para não duplicar texto.
            var content = string.IsNullOrWhiteSpace(messageBody)
                ? "(nenhuma mensagem fornecida)"
                : (messageBody.Length <= 2000 ? messageBody : messageBody[..1997] + "...");

            var dmEmbed = new DiscordEmbedBuilder()
                .WithTitle($"Mensagem para {targetName}")
                .WithColor(DiscordColor.Blurple)
                .WithTimestamp(DateTimeOffset.UtcNow)
                .Build();

            var result = await MassDmService.SendAsync(membersToDm, content, dmEmbed);

            var summary = new DiscordEmbedBuilder()
                .WithTitle("Envio finalizado")
                .WithColor(DiscordColor.Green)
                .AddField(new DiscordEmbedField("Alvo", targetName, true))
                .AddField(new DiscordEmbedField("Total de destinatários", result.Total.ToString(), true))
                .AddField(new DiscordEmbedField("Mensagens enviadas", result.Sent.ToString(), true))
                .AddField(new DiscordEmbedField("Falhas", result.Failed.ToString(), true))
                .WithTimestamp(DateTimeOffset.UtcNow);

            var failureText = MassDmService.DescribeFailures(result);
            if (failureText is not null)
                summary.WithDescription(failureText);

            // O envio pode passar dos 15 minutos de vida do token da interacao
            // (3s por DM, ate 500 pessoas): sem este cuidado o resumo final
            // simplesmente nao aparecia.
            await InteractionReply.SafeEditAsync(ctx, summary.Build());
        }

        private static Task ReplyErrorAsync(InteractionContext ctx, string title, string description) =>
            ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(new DiscordEmbedBuilder()
                .WithTitle(title)
                .WithDescription(description)
                .WithColor(DiscordColor.IndianRed)));
    }
}
