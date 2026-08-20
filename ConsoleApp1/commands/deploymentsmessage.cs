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
    internal class DeploymentsMessage : ApplicationCommandsModule
    {
        [SlashCommand("deployment", "Envia uma mensagem de deployment com embed, roles ping e botões.")]
        public async Task DeploymentCommand(
            InteractionContext ctx,
            [Option("codigo", "Código para acesso ao jogo")] string codigo)
        {
            await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);

            var config = new JSONReader();
            await config.ReadJSON();

            var guild = ctx.Guild;
            if (guild is null)
            {
                await ReplyErrorAsync(ctx, "Erro", "Este comando só pode ser usado em servidores (guilds).");
                return;
            }

            // Este comando tem portao proprio (deploymentAllowedRoleIds), e nao
            // o enlistPermissionRoleId usado pelos demais.
            var hasPermission = config.deploymentAllowedRoleIds is { Length: > 0 }
                && (ctx.Member?.Roles.Any(r => config.deploymentAllowedRoleIds.Contains(r.Id)) ?? false);

            if (!hasPermission)
            {
                await ReplyErrorAsync(ctx, "Permissão negada",
                    "Você não possui um cargo com permissão para usar este comando. Verifique os deploymentAllowedRoleIds no config.json.");
                return;
            }

            var (channel, channelError) = await DeploymentBuilder.ResolveChannelAsync(ctx.Client, config, guild);
            if (channel is null)
            {
                await ReplyErrorAsync(ctx, "Configuração inválida", channelError ?? "Canal indisponível.");
                return;
            }

            var deployment = DeploymentBuilder.Build(config, guild, codigo);

            try
            {
                await channel.SendMessageAsync(deployment.Message);

                var successEmbed = new DiscordEmbedBuilder()
                    .WithTitle("✅ Mensagem de deployment enviada!")
                    .WithDescription(
                        $"Título: {deployment.Titulo}\nCódigo: {codigo}\nCanal de voz: {deployment.VoiceChannel}\n" +
                        $"Roles pingados: {deployment.RolesPinged}\nCanal: {channel.Mention}")
                    .WithColor(DiscordColor.Green)
                    .WithTimestamp(DateTimeOffset.UtcNow);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(successEmbed));
            }
            catch (Exception ex)
            {
                await ReplyErrorAsync(ctx, "❌ Erro ao enviar mensagem", $"Ocorreu um erro ao enviar a mensagem: {ex.Message}");
            }
        }

        private static Task ReplyErrorAsync(InteractionContext ctx, string title, string description) =>
            ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(new DiscordEmbedBuilder()
                .WithTitle(title)
                .WithDescription(description)
                .WithColor(DiscordColor.IndianRed)));
    }
}
