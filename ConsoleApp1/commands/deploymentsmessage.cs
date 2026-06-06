using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
    internal class DeploymentsMessage : ApplicationCommandsModule
    {
        private string ProcessarTituloComEmoji(string tituloRaw, DiscordGuild? guild)
        {
            var titulo = tituloRaw;
            
            // Tentar encontrar emoji customizado "Resenha" no servidor
            if (guild?.Emojis?.Values is not null)
            {
                var customEmoji = guild.Emojis.Values.FirstOrDefault(e => 
                    e is not null && (
                        e.Name.Equals("Resenha", StringComparison.OrdinalIgnoreCase) ||
                        e.Name.Equals("resenha", StringComparison.OrdinalIgnoreCase)));
                    
                if (customEmoji is not null)
                {
                    // Usar emoji customizado encontrado
                    titulo = tituloRaw.Replace(":Resenha:", customEmoji.ToString());
                    return titulo;
                }
            }
            
            // Fallback para emoji padrão se não encontrar o customizado
            titulo = tituloRaw.Replace(":Resenha:", "⚔️");
            return titulo;
        }

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
                var noGuildEmbed = new DiscordEmbedBuilder()
                    .WithTitle("Erro")
                    .WithDescription("Este comando só pode ser usado em servidores (guilds).")
                    .WithColor(DiscordColor.IndianRed);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(noGuildEmbed));
                return;
            }

            var gameLink = !string.IsNullOrWhiteSpace(config.deploymentGameLink)
                ? config.deploymentGameLink
                : (!string.IsNullOrWhiteSpace(config.defaultGameLink)
                    ? config.defaultGameLink
                    : "https://www.roblox.com/games/12068120918/Napoleonic-Wars");

            if (!config.deploymentChannelId.HasValue)
            {
                var missingChannelConfig = new DiscordEmbedBuilder()
                    .WithTitle("Configuração inválida")
                    .WithDescription("O ID do canal para envio de deployments não está configurado. Verifique o arquivo config.json (deploymentChannelId).")
                    .WithColor(DiscordColor.IndianRed);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(missingChannelConfig));
                return;
            }

            // Check if user has permission based on deploymentAllowedRoleIds only
            var hasPermission = false;
            
            // Check deploymentAllowedRoleIds if configured
            if (config.deploymentAllowedRoleIds != null && config.deploymentAllowedRoleIds.Length > 0)
            {
                hasPermission = ctx.Member?.Roles.Any(r => config.deploymentAllowedRoleIds.Contains(r.Id)) ?? false;
            }
            
            if (!hasPermission)
            {
                var permEmbed = new DiscordEmbedBuilder()
                    .WithTitle("Permissão negada")
                    .WithDescription("Você não possui um cargo com permissão para usar este comando. Verifique os deploymentAllowedRoleIds no config.json.")
                    .WithColor(DiscordColor.IndianRed);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(permEmbed));
                return;
            }

            // Obter configurações do JSON
            var tituloRaw = !string.IsNullOrWhiteSpace(config.deploymentTitle) 
                ? config.deploymentTitle 
                : "<:Resenha1490483982331023572> À BATALHA!!!!!";
            var voz = !string.IsNullOrWhiteSpace(config.deploymentVoiceChannel) 
                ? config.deploymentVoiceChannel 
                : ":beer: Taverna dos Faiões";
            var imagem = config.deploymentImageUrl;
            var voiceChatLink = config.deploymentVoiceChatLink;
            
            // Processar o título para substituir emojis customizados
            var titulo = ProcessarTituloComEmoji(tituloRaw, ctx.Guild);
            
            // Processar roles para ping das configurações
            List<DiscordRole> rolesToPing = new List<DiscordRole>();
            string roleMentions = "";

            var roleIdsToPing = config.deploymentDefaultRoles?.ToList() ?? new List<ulong>();
            if (roleIdsToPing.Any())
            {
                var guildRoles = ctx.Guild?.Roles?.Values?.ToList() ?? new List<DiscordRole>();

                foreach (var roleId in roleIdsToPing)
                {
                    var foundRole = guildRoles.FirstOrDefault(r => r?.Id == roleId);
                    if (foundRole is not null)
                    {
                        rolesToPing.Add(foundRole);
                        roleMentions += foundRole.Mention + " ";
                    }
                }
            }

            var cleanedMentions = "";
            if (!string.IsNullOrWhiteSpace(roleMentions))
            {
                cleanedMentions = string.Join(" ", roleMentions.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Distinct());
            }

            var voiceChannelDisplay = voz;
            if (!string.IsNullOrWhiteSpace(voiceChatLink) && Uri.TryCreate(voiceChatLink, UriKind.Absolute, out var voiceChatUri))
            {
                voiceChannelDisplay = $"[{voz}]({voiceChatUri})";
            }

            var quickLaunchTemplate = !string.IsNullOrWhiteSpace(config.deploymentQuickLaunchLink)
                ? config.deploymentQuickLaunchLink
                : (!string.IsNullOrWhiteSpace(config.deploymentPlaceId)
                    ? $"https://www.roblox.com/games/start?launchData=CODE_HERE&placeId={config.deploymentPlaceId}"
                    : gameLink);

            string? quickLaunchLink = null;
            if (!string.IsNullOrWhiteSpace(quickLaunchTemplate))
            {
                var resolvedTemplate = quickLaunchTemplate;
                if (resolvedTemplate.Contains("CODE_HERE"))
                {
                    resolvedTemplate = resolvedTemplate.Replace("CODE_HERE", Uri.EscapeDataString(codigo));
                }
                if (resolvedTemplate.Contains("{CODE}"))
                {
                    resolvedTemplate = resolvedTemplate.Replace("{CODE}", Uri.EscapeDataString(codigo));
                }
                if (Uri.TryCreate(resolvedTemplate, UriKind.Absolute, out var quickLaunchUri))
                {
                    quickLaunchLink = quickLaunchUri.ToString();
                }
            }

            // Criar embed
            var embed = new DiscordEmbedBuilder()
                .WithColor(DiscordColor.Blurple);
            var imageWarning = "";

            // Adicionar imagem como imagem principal no topo
            if (!string.IsNullOrWhiteSpace(imagem))
            {
                if (Uri.TryCreate(imagem, UriKind.Absolute, out var imageUri))
                {
                    embed.WithImageUrl(imageUri);
                }
                else
                {
                    imageWarning = "⚠️ URL da imagem inválida. A imagem não será exibida.";
                }
            }

            // Adicionar título após a imagem
            embed.WithTitle(titulo);

            var descriptionLines = new List<string>();
            if (!string.IsNullOrWhiteSpace(cleanedMentions))
            {
                descriptionLines.Add(cleanedMentions);
                descriptionLines.Add(string.Empty);
            }
            descriptionLines.Add($"**Voice Channel:** {voiceChannelDisplay}");
            descriptionLines.Add($"**Code:** `{codigo}`");
            descriptionLines.Add(string.Empty);
            if (!string.IsNullOrWhiteSpace(quickLaunchLink))
            {
                descriptionLines.Add($"**Quick Launch Link:** [Join & Enter {codigo}]({quickLaunchLink})");
            }
            else
            {
                descriptionLines.Add($"**Quick Launch Link:** Join & Enter {codigo}");
            }

            if (!string.IsNullOrWhiteSpace(imageWarning))
            {
                descriptionLines.Add(string.Empty);
                descriptionLines.Add(imageWarning);
            }

            embed.WithDescription(string.Join("\n", descriptionLines));

            // Criar botões
            var buttons = new List<DiscordComponent>();

            // Botão de Quick Launch com link direto
            var quickLaunchButton = new DiscordLinkButtonComponent(
                quickLaunchLink ?? gameLink, 
                "Quick Launch");

            buttons.Add(quickLaunchButton);

            // Enviar mensagem com menções de cargo fora do embed para notificação
            var messageBuilder = new DiscordMessageBuilder();
            
            // Adicionar menções de cargo no conteúdo da mensagem para notificar
            if (!string.IsNullOrWhiteSpace(cleanedMentions))
            {
                messageBuilder.WithContent(cleanedMentions);
            }
            
            messageBuilder.AddEmbed(embed.Build());

            if (buttons.Any())
            {
                messageBuilder.AddComponents(buttons);
            }

            try
            {
                // Obter o canal configurado
                var deploymentChannel = await ctx.Client.GetChannelAsync(config.deploymentChannelId.Value);
                if (deploymentChannel is null || deploymentChannel.GuildId != guild.Id)
                {
                    var channelNotFoundEmbed = new DiscordEmbedBuilder()
                        .WithTitle("Canal não encontrado")
                        .WithDescription($"O canal configurado para deployments não foi encontrado neste servidor. ID: {config.deploymentChannelId.Value}")
                        .WithColor(DiscordColor.IndianRed);

                    await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(channelNotFoundEmbed));
                    return;
                }

                // Verificar se o bot tem permissão para enviar mensagens no canal
                var botMember = await guild.GetMemberAsync(ctx.Client.CurrentUser.Id);
                if (botMember is not null)
                {
                    var botPermissions = deploymentChannel.PermissionsFor(botMember);
                    
                    if (!botPermissions.HasFlag(Permissions.SendMessages))
                    {
                        var noPermissionEmbed = new DiscordEmbedBuilder()
                            .WithTitle("Sem permissão para enviar mensagens")
                            .WithDescription($"O bot não tem permissão para enviar mensagens no canal {deploymentChannel.Mention}. Verifique as permissões do bot neste canal.")
                            .WithColor(DiscordColor.IndianRed);

                        await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(noPermissionEmbed));
                        return;
                    }

                    if (!botPermissions.HasFlag(Permissions.EmbedLinks))
                    {
                        var noEmbedPermissionEmbed = new DiscordEmbedBuilder()
                            .WithTitle("Sem permissão para embeds")
                            .WithDescription($"O bot não tem permissão para enviar embeds no canal {deploymentChannel.Mention}. Verifique as permissões do bot neste canal.")
                            .WithColor(DiscordColor.IndianRed);

                        await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(noEmbedPermissionEmbed));
                        return;
                    }
                }
                else
                {
                    var botMemberErrorEmbed = new DiscordEmbedBuilder()
                        .WithTitle("Erro ao obter informações do bot")
                        .WithDescription("Não foi possível obter as informações do membro do bot no servidor.")
                        .WithColor(DiscordColor.IndianRed);

                    await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(botMemberErrorEmbed));
                    return;
                }

                await deploymentChannel.SendMessageAsync(messageBuilder);

                var successEmbed = new DiscordEmbedBuilder()
                    .WithTitle("✅ Mensagem de deployment enviada!")
                    .WithDescription($"Título: {titulo}\nCódigo: {codigo}\nCanal de voz: {voz}\nRoles pingados: {rolesToPing.Count}\nCanal: {deploymentChannel.Mention}")
                    .WithColor(DiscordColor.Green)
                    .WithTimestamp(DateTimeOffset.UtcNow);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(successEmbed));
            }
            catch (Exception ex)
            {
                var errorEmbed = new DiscordEmbedBuilder()
                    .WithTitle("❌ Erro ao enviar mensagem")
                    .WithDescription($"Ocorreu um erro ao enviar a mensagem: {ex.Message}")
                    .WithColor(DiscordColor.Red);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(errorEmbed));
            }
        }
    }
}
