using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CornwallUtilities.config;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.Interactivity.Extensions;
using DSharpPlus.SlashCommands;

namespace CornwallUtilities.commands
{
    internal class EnlistUser : ApplicationCommandModule
    {
        [SlashCommand("enlistuser", "Alista um usuário (confirmação de alt + cargos + nickname + log).")]
        public async Task EnlistUserCommand(
            InteractionContext ctx,
            [Option("user", "Usuário a ser alistado")] DiscordUser user)
        {
            await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);

            var config = new JSONReader();
            await config.ReadJSON();

            if (ctx.Guild == null)
            {
                var guildEmbed = new DiscordEmbedBuilder()
                    .WithTitle("Comando inválido")
                    .WithDescription("Este comando só pode ser executado em um servidor (guild).")
                    .WithColor(DiscordColor.IndianRed);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(guildEmbed));
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

            DiscordMember? targetMember;
            try
            {
                targetMember = await ctx.Guild.GetMemberAsync(user.Id);
            }
            catch
            {
                var notFoundEmbed = new DiscordEmbedBuilder()
                    .WithTitle("Usuário não encontrado")
                    .WithDescription("O usuário fornecido não é membro deste servidor.")
                    .WithColor(DiscordColor.IndianRed);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(notFoundEmbed));
                return;
            }

            // Confirmação manual de alt (sim/não)
            var confirmEmbed = new DiscordEmbedBuilder()
                .WithTitle("Confirmação de Alt")
                .WithDescription($"Você verificou que {user.Mention} é uma conta alternativa (alt)?")
                .WithColor(DiscordColor.Orange);

            var buttons = new DiscordComponent[]
            {
                new DiscordButtonComponent(ButtonStyle.Primary, "enlist_not_alt", "Não, não é alt"),
                new DiscordButtonComponent(ButtonStyle.Danger, "enlist_is_alt", "Sim, é alt")
            };

            var promptMessage = await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(confirmEmbed).AddComponents(buttons));

            var interactivity = ctx.Client.GetInteractivity();
            var result = await interactivity.WaitForButtonAsync(
                promptMessage,
                x => x.User.Id == ctx.User.Id &&
                     (x.Id == "enlist_not_alt" || x.Id == "enlist_is_alt"),
                TimeSpan.FromMinutes(3));

            if (result.TimedOut)
            {
                var timeoutEmbed = new DiscordEmbedBuilder()
                    .WithTitle("Tempo esgotado")
                    .WithDescription("Nenhuma confirmação foi recebida. Execute o comando novamente quando estiver pronto.")
                    .WithColor(DiscordColor.IndianRed);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(timeoutEmbed));
                return;
            }

            await result.Result.Interaction.CreateResponseAsync(InteractionResponseType.UpdateMessage, new DiscordInteractionResponseBuilder()
                .AddEmbed(new DiscordEmbedBuilder()
                    .WithTitle("Confirmação recebida")
                    .WithDescription(result.Result.Id == "enlist_is_alt"
                        ? "Será tratado como alt e o alistamento será cancelado."
                        : "Confirmado que não é alt. O alistamento seguirá.")
                    .WithColor(result.Result.Id == "enlist_is_alt" ? DiscordColor.IndianRed : DiscordColor.Green)));

            if (result.Result.Id == "enlist_is_alt")
            {
                var altEmbed = new DiscordEmbedBuilder()
                    .WithTitle("Alistamento bloqueado")
                    .WithDescription("O usuário não pôde ser alistado pois foi marcado como conta alternativa (alt).")
                    .WithColor(DiscordColor.IndianRed);

                await ctx.FollowUpAsync(new DiscordFollowupMessageBuilder().AddEmbed(altEmbed));
                return;
            }

            // Adiciona cargos ao membro (ignorando cargos que ele já possui)
            var addedRoles = new List<DiscordRole>();
            if (config.enlistTargetRoleIds != null && config.enlistTargetRoleIds.Length > 0)
            {
                foreach (var roleId in config.enlistTargetRoleIds)
                {
                    if (!ctx.Guild.Roles.TryGetValue(roleId, out var role))
                        continue;

                    // Ignora cargos que o membro já possui
                    if (targetMember.Roles.Any(r => r.Id == roleId))
                        continue;

                    try
                    {
                        await targetMember.GrantRoleAsync(role, "Alistamento via comando");
                        addedRoles.Add(role);
                    }
                    catch
                    {
                        // Ignore failures for specific roles to continue with others
                    }
                }
            }

            // Atualiza nickname adicionando o prefixo [32nd] se ainda não existir
            var currentNick = targetMember.Nickname ?? targetMember.Username;
            const string prefix = "[32nd]";
            if (!currentNick.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var newNick = $"{prefix} {currentNick}";
                try
                {
                    await targetMember.ModifyAsync(m => m.Nickname = newNick);
                }
                catch
                {
                    // Caso não seja possível alterar o nickname, ignore.
                }
            }

            // Envia log para canal específico
            if (config.enlistLogChannelId.HasValue)
            {
                try
                {
                    var logChannel = ctx.Guild.GetChannel(config.enlistLogChannelId.Value);
                    if (logChannel != null)
                    {
                        var logEmbed = new DiscordEmbedBuilder()
                            .WithTitle("32nd Regiment - Recruit Log")
                            .WithDescription("Registro de alistamento realizado com sucesso.")
                            .WithColor(DiscordColor.Blurple)
                            .WithThumbnail(targetMember.GetAvatarUrl(ImageFormat.Auto))
                            .WithFooter("Recruit log gerado por CornwallBot", ctx.Client.CurrentUser.AvatarUrl)
                            .WithTimestamp(DateTimeOffset.UtcNow)
                            .AddField("Executor", ctx.User.Mention, true)
                            .AddField("Alistado", user.Mention, true)
                            .AddField("Cargos adicionados", addedRoles.Count > 0 ? string.Join(", ", addedRoles.Select(r => r.Mention)) : "Nenhum", false)
                            .AddField("Nickname atualizado", currentNick.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? "Já possuía" : $"{prefix} {currentNick}", true)
                            .AddField("Verificação de alt", "Não", true);

                        await logChannel.SendMessageAsync(new DiscordMessageBuilder().AddEmbed(logEmbed));
                    }
                    else
                    {
                        await ctx.Channel.SendMessageAsync("Não consegui encontrar o canal de logs configurado. Verifique o `enlistLogChannelId` no config.");
                    }
                }
                catch
                {
                    await ctx.Channel.SendMessageAsync("Ocorreu um erro ao tentar enviar a mensagem no canal de logs. Verifique se o ID está correto e se o bot tem permissão para enviar mensagens lá.");
                }
            }

            var successEmbed = new DiscordEmbedBuilder()
                .WithTitle("32nd Regiment - Alistamento bem-sucedido")
                .WithDescription($"O usuário **{user.Username}** foi alistado com sucesso.")
                .WithColor(DiscordColor.Green)
                .WithThumbnail(targetMember.GetAvatarUrl(ImageFormat.Auto))
                .WithFooter("Confirmação de alistamento", ctx.Client.CurrentUser.AvatarUrl)
                .WithTimestamp(DateTimeOffset.UtcNow)
                .AddField("Cargos adicionados", addedRoles.Count > 0 ? string.Join(", ", addedRoles.Select(r => r.Name)) : "Nenhum", true)
                .AddField("Nickname atualizado", currentNick.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? "Já possuía" : $"{prefix} {currentNick}", true);

            await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(successEmbed));
        }
    }
}
