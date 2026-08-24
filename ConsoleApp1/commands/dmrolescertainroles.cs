using System;
using System.Linq;
using System.Threading.Tasks;
using CornwallUtilities.config;
using CornwallUtilities.Services;
using CornwallUtilities.Services.Audit;
using DisCatSharp.ApplicationCommands;
using DisCatSharp.ApplicationCommands.Attributes;
using DisCatSharp.ApplicationCommands.Context;
using DisCatSharp.Entities;
using DisCatSharp.Enums;

namespace CornwallUtilities.commands
{
    internal class DmRolesCertainRoles : ApplicationCommandsModule
    {
        private const string FallbackGameLink = "https://example.com";

        [SlashCommand("dmdeployment", "Envia uma DM para um usuário ou para todos os membros de um cargo.")]
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

            var dmEmbed = BuildDeploymentDm(targetName, code, messageBody);

            // Link só no content para o Discord mostrar o preview; não duplicar no embed.
            var content = string.IsNullOrWhiteSpace(gameLink) ? null : gameLink;

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

        /// <summary>Teto do Discord para o valor de um field de embed.</summary>
        public const int MaxDmFieldLength = 1024;

        /// <summary>
        /// Embed da DM de deployment. Publico porque o dashboard envia
        /// exatamente a mesma mensagem por POST /api/dm.
        ///
        /// Os dois campos sao cortados em 1024: pelo dashboard eles so eram
        /// limitados pelo teto de 64 KB do corpo da requisicao, e um texto maior
        /// fazia TODO SendMessageAsync do mass DM voltar 400 - o job entao
        /// reportava 100% de falha com um motivo que nao explicava nada.
        /// </summary>
        public static DiscordEmbed BuildDeploymentDm(string targetName, string code, string messageBody) =>
            new DiscordEmbedBuilder()
                .WithTitle($"Mensagem para {targetName}")
                .WithColor(DiscordColor.Blurple)
                .AddField(new DiscordEmbedField("Código",
                    string.IsNullOrWhiteSpace(code) ? "(nenhum)" : AuditEmbeds.Trim(code, MaxDmFieldLength), true))
                .AddField(new DiscordEmbedField("Mensagem",
                    string.IsNullOrWhiteSpace(messageBody) ? "(nenhuma mensagem extra)" : AuditEmbeds.Trim(messageBody, MaxDmFieldLength), false))
                .Build();

        private static Task ReplyErrorAsync(InteractionContext ctx, string title, string description) =>
            ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(new DiscordEmbedBuilder()
                .WithTitle(title)
                .WithDescription(description)
                .WithColor(DiscordColor.IndianRed)));
    }
}
