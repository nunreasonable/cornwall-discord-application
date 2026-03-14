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
    internal class DebugDeployment : ApplicationCommandsModule
    {
        [SlashCommand("debugdeployment", "Debug do comando deployment")]
        public async Task DebugDeploymentCommand(InteractionContext ctx)
        {
            await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);

            var config = new JSONReader();
            await config.ReadJSON();

            var embed = new DiscordEmbedBuilder()
                .WithTitle("🔍 Debug do Comando Deployment")
                .WithColor(DiscordColor.Blurple)
                .WithTimestamp(DateTimeOffset.UtcNow);

            // Verificar configurações básicas
            embed.AddField(new DiscordEmbedField("⚙️ Configurações", 
                $"enlistPermissionRoleId: {config.enlistPermissionRoleId}\n" +
                $"deploymentAllowedRoleIds: [{string.Join(", ", config.deploymentAllowedRoleIds ?? new ulong[0])}]\n" +
                $"deploymentChannelId: {config.deploymentChannelId}"));

            // Verificar roles do usuário
            var userRoleIds = ctx.Member.Roles.Select(r => r.Id).ToList();
            embed.AddField(new DiscordEmbedField("👤 Seus Role IDs", string.Join(", ", userRoleIds)));

            // Verificar permissão - enlistPermissionRoleId
            var hasEnlistPermission = ctx.Member?.Roles.Any(r => r.Id == config.enlistPermissionRoleId.Value) ?? false;
            embed.AddField(new DiscordEmbedField("🔑 Permissão Enlist", 
                $"enlistPermissionRoleId: {config.enlistPermissionRoleId.Value}\n" +
                $"Você tem este role: {hasEnlistPermission}"));

            // Verificar permissão - deploymentAllowedRoleIds
            var hasDeploymentPermission = false;
            if (config.deploymentAllowedRoleIds != null && config.deploymentAllowedRoleIds.Length > 0)
            {
                hasDeploymentPermission = ctx.Member?.Roles.Any(r => config.deploymentAllowedRoleIds.Contains(r.Id)) ?? false;
                var field = new DiscordEmbedField("🎯 Permissão Deployment", 
                    $"deploymentAllowedRoleIds: [{string.Join(", ", config.deploymentAllowedRoleIds)}]\n" +
                    $"Você tem algum destes roles: {hasDeploymentPermission}");
                embed.AddField(field);
            }

            // Verificação final
            var finalHasPermission = hasEnlistPermission || hasDeploymentPermission;
            var finalField = new DiscordEmbedField("✅ Permissão Final", 
                $"Permissão combinada: {finalHasPermission}\n" +
                $"Resultado: {(finalHasPermission ? "✅ PODE usar o comando" : "❌ NÃO pode usar o comando")}");
            embed.AddField(finalField);

            // Tentar acessar o canal de deployment
            if (config.deploymentChannelId.HasValue)
            {
                try
                {
                    var deploymentChannel = await ctx.Client.GetChannelAsync(config.deploymentChannelId.Value);
                    if (deploymentChannel != null)
                    {
                        embed.AddField(new DiscordEmbedField("📢 Canal de Deployment", 
                            $"Canal encontrado: {deploymentChannel.Mention}\n" +
                            $"Nome: {deploymentChannel.Name}\n" +
                            $"Tipo: {deploymentChannel.Type}"));
                    }
                    else
                    {
                        embed.AddField(new DiscordEmbedField("❌ Canal de Deployment", "Canal não encontrado (null)"));
                    }
                }
                catch (Exception ex)
                {
                    embed.AddField(new DiscordEmbedField("❌ Erro ao Acessar Canal", ex.Message));
                }
            }
            else
            {
                embed.AddField(new DiscordEmbedField("❌ Configuração", "deploymentChannelId não configurado"));
            }

            await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(embed.Build()));
        }
    }
}
