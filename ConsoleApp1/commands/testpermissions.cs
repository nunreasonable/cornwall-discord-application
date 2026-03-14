using System.Threading.Tasks;
using CornwallUtilities.config;
using DisCatSharp;
using DisCatSharp.Entities;
using DisCatSharp.ApplicationCommands;
using DisCatSharp.Enums;
using DisCatSharp.ApplicationCommands.Context;
using DisCatSharp.ApplicationCommands.Attributes;

namespace CornwallUtilities.commands
{
    internal class TestPermissions : ApplicationCommandsModule
    {
        [SlashCommand("testperms", "Testa permissões do bot")]
        public async Task TestPermissionsCommand(InteractionContext ctx)
        {
            await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);

            var config = new JSONReader();
            await config.ReadJSON();

            var embed = new DiscordEmbedBuilder()
                .WithTitle("🔍 Teste de Permissões")
                .WithColor(DiscordColor.Blurple)
                .WithTimestamp(DateTimeOffset.UtcNow);

            // Verificar permissões no canal atual
            var currentChannelPerms = ctx.Channel.PermissionsFor(ctx.Member);
            embed.AddField(new DiscordEmbedField("📋 Permissões no Canal Atual", 
                $"Send Messages: {currentChannelPerms.HasPermission(Permissions.SendMessages)}"));
            embed.AddField(new DiscordEmbedField("🔗 Links & Emojis", 
                $"Embed Links: {currentChannelPerms.HasPermission(Permissions.EmbedLinks)}\n" +
                $"Use External Emojis: {currentChannelPerms.HasPermission(Permissions.UseExternalEmojis)}"));

            // Verificar canal de deployment
            if (config.deploymentChannelId.HasValue)
            {
                try
                {
                    var deploymentChannel = await ctx.Client.GetChannelAsync(config.deploymentChannelId.Value);
                    if (deploymentChannel != null)
                    {
                        var deploymentPerms = deploymentChannel.PermissionsFor(ctx.Member);
                        embed.AddField(new DiscordEmbedField("🎯 Canal de Deployment", deploymentChannel.Mention));
                        embed.AddField(new DiscordEmbedField("📨 Permissões no Canal", 
                            $"Send Messages: {deploymentPerms.HasPermission(Permissions.SendMessages)}\n" +
                            $"Embed Links: {deploymentPerms.HasPermission(Permissions.EmbedLinks)}"));
                    }
                    else
                    {
                        embed.AddField(new DiscordEmbedField("❌ Canal de Deployment", "Canal não encontrado!"));
                    }
                }
                catch (Exception ex)
                {
                    embed.AddField(new DiscordEmbedField("❌ Erro ao Verificar Canal", ex.Message));
                }
            }
            else
            {
                embed.AddField(new DiscordEmbedField("❌ Configuração", "deploymentChannelId não configurado!"));
            }

            // Verificar roles do usuário
            var userRoles = ctx.Member.Roles.Select(r => $"{r.Name} ({r.Id})").ToList();
            var rolesText = string.Join(", ", userRoles);
            
            // Limitar tamanho para não exceder 1024 caracteres
            if (rolesText.Length > 1000)
            {
                rolesText = rolesText.Substring(0, 997) + "...";
            }
            
            embed.AddField(new DiscordEmbedField("👤 Seus Roles", rolesText));

            await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(embed.Build()));
        }
    }
}
