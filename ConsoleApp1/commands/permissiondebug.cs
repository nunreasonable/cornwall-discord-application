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
    internal class PermissionDebug : ApplicationCommandsModule
    {
        [SlashCommand("permissiondebug", "Debug detalhado das permissões do deployment")]
        public async Task PermissionDebugCommand(InteractionContext ctx)
        {
            await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);

            var config = new JSONReader();
            await config.ReadJSON();

            var embed = new DiscordEmbedBuilder()
                .WithTitle("🔍 Debug Detalhado de Permissões")
                .WithColor(DiscordColor.Blurple)
                .WithTimestamp(DateTimeOffset.UtcNow);

            // Passo 1: Verificar se deploymentAllowedRoleIds existe
            var hasDeploymentRoles = config.deploymentAllowedRoleIds != null && config.deploymentAllowedRoleIds.Length > 0;
            embed.AddField(new DiscordEmbedField("📋 Passo 1 - Config Deployment", 
                $"deploymentAllowedRoleIds existe: {hasDeploymentRoles}\n" +
                $"Quantidade: {(config.deploymentAllowedRoleIds?.Length ?? 0)}\n" +
                $"Valores: [{string.Join(", ", config.deploymentAllowedRoleIds ?? new ulong[0])}]"));

            // Passo 2: Verificar roles do usuário
            var userRoleIds = ctx.Member.Roles.Select(r => r.Id).ToList();
            embed.AddField(new DiscordEmbedField("👤 Passo 2 - Seus Roles", 
                $"Total de roles: {userRoleIds.Count}\n" +
                $"IDs: [{string.Join(", ", userRoleIds)}]"));

            // Passo 3: Simular verificação exata do deployment
            var hasPermission = false;
            
            // Simulação exata do código do deployment
            if (hasDeploymentRoles)
            {
                hasPermission = ctx.Member?.Roles.Any(r => config.deploymentAllowedRoleIds.Contains(r.Id)) ?? false;
                embed.AddField(new DiscordEmbedField("🎯 Passo 3 - Deployment Check", 
                    $"Roles permitidos: [{string.Join(", ", config.deploymentAllowedRoleIds)}]\n" +
                    $"Você tem algum destes: {hasPermission}\n" +
                    $"hasPermission final: {hasPermission}"));
            }
            else
            {
                embed.AddField(new DiscordEmbedField("🎯 Passo 3 - Deployment Check", 
                    "deploymentAllowedRoleIds não configurado - ninguém pode usar o comando"));
            }

            // Passo 4: Verificar permissões do bot no canal
            if (config.deploymentChannelId.HasValue)
            {
                try
                {
                    var deploymentChannel = await ctx.Client.GetChannelAsync(config.deploymentChannelId.Value);
                    if (deploymentChannel is not null)
                    {
                        var botMember = await ctx.Guild?.GetMemberAsync(ctx.Client.CurrentUser.Id);
                        if (botMember is not null)
                        {
                            var botPermissions = deploymentChannel.PermissionsFor(botMember);
                            
                            embed.AddField(new DiscordEmbedField("🤖 Passo 5 - Permissões do Bot", 
                                $"Canal: {deploymentChannel.Mention}\n" +
                                $"SendMessages: {botPermissions.HasFlag(Permissions.SendMessages)}\n" +
                                $"EmbedLinks: {botPermissions.HasFlag(Permissions.EmbedLinks)}\n" +
                                $"ViewChannel: {botPermissions.HasFlag(Permissions.AccessChannels)}\n" +
                                $"Permissions totais: {botPermissions}"));
                        }
                        else
                        {
                            embed.AddField(new DiscordEmbedField("🤖 Passo 5 - Permissões do Bot", 
                                "❌ Bot member não encontrado"));
                        }
                    }
                    else
                    {
                        embed.AddField(new DiscordEmbedField("🤖 Passo 5 - Permissões do Bot", 
                            "❌ Canal de deployment não encontrado"));
                    }
                }
                catch (Exception ex)
                {
                    embed.AddField(new DiscordEmbedField("🤖 Passo 5 - Permissões do Bot", 
                        $"❌ Erro ao verificar permissões: {ex.Message}"));
                }
            }
            else
            {
                embed.AddField(new DiscordEmbedField("🤖 Passo 5 - Permissões do Bot", 
                    "❌ deploymentChannelId não configurado"));
            }

            // Passo 6: Resultado final
            embed.AddField(new DiscordEmbedField("✅ Passo 6 - Resultado Final", 
                $"Permissão final: {hasPermission}\n" +
                $"Pode usar comando: {(hasPermission ? "✅ SIM" : "❌ NÃO")}\n" +
                $"Próximo passo: {(hasPermission ? "Executar comando" : "Mostrar erro de permissão")}"));

            await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(embed.Build()));
        }
    }
}
