using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CornwallUtilities.config;
using DisCatSharp;
using DisCatSharp.Entities;
using DisCatSharp.Enums;

namespace CornwallUtilities.Services
{
    /// <summary>Mensagem pronta para envio, mais o resumo do que foi montado.</summary>
    internal sealed record DeploymentMessage(
        DiscordMessageBuilder Message,
        string Titulo,
        string VoiceChannel,
        int RolesPinged,
        string? ImageWarning);

    /// <summary>
    /// Montagem da mensagem de deployment.
    ///
    /// Estava inteira dentro do handler de /deployment e por isso era
    /// inalcancavel pelo dashboard. Extraida para ca, o comando de barra e o
    /// endpoint POST /api/deployment produzem exatamente a mesma mensagem - se
    /// estivesse duplicada, um ajuste no embed sairia so num dos dois.
    /// </summary>
    internal static class DeploymentBuilder
    {
        /// <summary>
        /// Remove os caracteres que quebrariam o markdown do embed: crases (fecham
        /// o code span), colchetes e parenteses (forjam um segundo hyperlink no
        /// rotulo do link), e os demais marcadores de enfase. O codigo e uma
        /// string curta de jogo, entao remove-los nao tira nada legitimo.
        /// </summary>
        private static string SanitizeInline(string? value) =>
            new string((value ?? string.Empty)
                .Where(c => c is not ('`' or '[' or ']' or '(' or ')' or '*' or '_' or '~' or '|' or '\\'))
                .ToArray());

        public static DeploymentMessage Build(JSONReader config, DiscordGuild? guild, string codigo)
        {
            var gameLink = !string.IsNullOrWhiteSpace(config.deploymentGameLink)
                ? config.deploymentGameLink
                : (!string.IsNullOrWhiteSpace(config.defaultGameLink)
                    ? config.defaultGameLink
                    : "https://www.roblox.com/games/12068120918/Napoleonic-Wars");

            var tituloRaw = !string.IsNullOrWhiteSpace(config.deploymentTitle)
                ? config.deploymentTitle
                : "<:Resenha1490483982331023572> À BATALHA!!!!!";
            var voz = !string.IsNullOrWhiteSpace(config.deploymentVoiceChannel)
                ? config.deploymentVoiceChannel
                : ":beer: Taverna dos Faiões";
            var imagem = config.deploymentImageUrl;
            var voiceChatLink = config.deploymentVoiceChatLink;

            var titulo = ProcessarTituloComEmoji(tituloRaw, guild);

            // Menções de cargo, resolvidas contra o servidor.
            var rolesToPing = new List<DiscordRole>();
            var roleMentions = "";

            var roleIdsToPing = config.deploymentDefaultRoles?.ToList() ?? new List<ulong>();
            if (roleIdsToPing.Any())
            {
                var guildRoles = guild?.Roles?.Values?.ToList() ?? new List<DiscordRole>();

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

            var quickLaunchLink = ResolveQuickLaunchLink(config, codigo, gameLink);

            var embed = new DiscordEmbedBuilder()
                .WithColor(DiscordColor.Blurple);
            string? imageWarning = null;

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

            embed.WithTitle(titulo);

            var descriptionLines = new List<string>();
            if (!string.IsNullOrWhiteSpace(cleanedMentions))
            {
                descriptionLines.Add(cleanedMentions);
                descriptionLines.Add(string.Empty);
            }
            // O codigo vem cru do usuario (opcao do /deployment ou corpo do POST
            // /api/deployment). Sem sanitizar, uma crase fecha o code span e um
            // "](https://evil)" dentro do rotulo forja um segundo hyperlink na
            // mensagem de deployment - justamente a mensagem que o regimento
            // inteiro clica. A URL do link ja e validada; o rotulo nao era.
            var codigoSafe = SanitizeInline(codigo);
            descriptionLines.Add($"**Voice Channel:** {voiceChannelDisplay}");
            descriptionLines.Add($"**Code:** `{codigoSafe}`");
            descriptionLines.Add(string.Empty);
            descriptionLines.Add(string.IsNullOrWhiteSpace(quickLaunchLink)
                ? $"**Quick Launch Link:** Join & Enter {codigoSafe}"
                : $"**Quick Launch Link:** [Join & Enter {codigoSafe}]({quickLaunchLink})");

            if (!string.IsNullOrWhiteSpace(imageWarning))
            {
                descriptionLines.Add(string.Empty);
                descriptionLines.Add(imageWarning);
            }

            embed.WithDescription(string.Join("\n", descriptionLines));

            var messageBuilder = new DiscordMessageBuilder();

            // As mencoes vao no conteudo, fora do embed: dentro do embed elas
            // aparecem mas nao notificam ninguem.
            if (!string.IsNullOrWhiteSpace(cleanedMentions))
                messageBuilder.WithContent(cleanedMentions);

            messageBuilder.AddEmbed(embed.Build());
            messageBuilder.AddComponents(new DiscordLinkButtonComponent(quickLaunchLink ?? gameLink, "Quick Launch"));

            return new DeploymentMessage(messageBuilder, titulo, voz, rolesToPing.Count, imageWarning);
        }

        private static string? ResolveQuickLaunchLink(JSONReader config, string codigo, string gameLink)
        {
            var template = !string.IsNullOrWhiteSpace(config.deploymentQuickLaunchLink)
                ? config.deploymentQuickLaunchLink
                : (!string.IsNullOrWhiteSpace(config.deploymentPlaceId)
                    ? $"https://www.roblox.com/games/start?launchData=CODE_HERE&placeId={config.deploymentPlaceId}"
                    : gameLink);

            if (string.IsNullOrWhiteSpace(template))
                return null;

            var resolved = template
                .Replace("CODE_HERE", Uri.EscapeDataString(codigo))
                .Replace("{CODE}", Uri.EscapeDataString(codigo));

            return Uri.TryCreate(resolved, UriKind.Absolute, out var uri) ? uri.ToString() : null;
        }

        private static string ProcessarTituloComEmoji(string tituloRaw, DiscordGuild? guild)
        {
            // Tentar encontrar emoji customizado "Resenha" no servidor
            var customEmoji = guild?.Emojis?.Values?.FirstOrDefault(e =>
                e is not null && e.Name.Equals("Resenha", StringComparison.OrdinalIgnoreCase));

            return customEmoji is not null
                ? tituloRaw.Replace(":Resenha:", customEmoji.ToString())
                : tituloRaw.Replace(":Resenha:", "⚔️");
        }

        /// <summary>
        /// Resolve o canal de deployment e confere as permissoes do bot nele.
        /// Devolve o canal, ou a mensagem de erro pronta para exibir.
        ///
        /// Conferir SendMessages/EmbedLinks antes de tentar transforma um erro
        /// generico de API numa explicacao acionavel de qual permissao falta.
        /// </summary>
        public static async Task<(DiscordChannel? Channel, string? Error)> ResolveChannelAsync(
            DiscordClient client, JSONReader config, DiscordGuild guild)
        {
            if (!config.deploymentChannelId.HasValue)
                return (null, "O ID do canal para envio de deployments não está configurado (deploymentChannelId).");

            DiscordChannel? channel;
            try
            {
                channel = await client.GetChannelAsync(config.deploymentChannelId.Value);
            }
            catch (Exception)
            {
                channel = null;
            }

            if (channel is null || channel.GuildId != guild.Id)
                return (null, $"O canal configurado para deployments não foi encontrado neste servidor. ID: {config.deploymentChannelId.Value}");

            DiscordMember? botMember;
            try
            {
                botMember = await guild.GetMemberAsync(client.CurrentUser.Id);
            }
            catch (Exception)
            {
                botMember = null;
            }

            if (botMember is null)
                return (null, "Não foi possível obter as informações do membro do bot no servidor.");

            var permissions = channel.PermissionsFor(botMember);

            if (!permissions.HasFlag(Permissions.SendMessages))
                return (null, $"O bot não tem permissão para enviar mensagens no canal {channel.Name}.");

            if (!permissions.HasFlag(Permissions.EmbedLinks))
                return (null, $"O bot não tem permissão para enviar embeds no canal {channel.Name}.");

            return (channel, null);
        }
    }
}
