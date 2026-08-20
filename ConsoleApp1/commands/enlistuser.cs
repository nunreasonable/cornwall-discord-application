using System;
using System.Linq;
using System.Threading.Tasks;
using CornwallUtilities.config;
using CornwallUtilities.Services;
using DisCatSharp;
using DisCatSharp.ApplicationCommands;
using DisCatSharp.ApplicationCommands.Attributes;
using DisCatSharp.ApplicationCommands.Context;
using DisCatSharp.Entities;
using DisCatSharp.Enums;

namespace CornwallUtilities.commands
{
    internal class EnlistUser : ApplicationCommandsModule
    {
        [SlashCommand("enlistuser", "Alista um usuário (verificação ROBLOX automática + cargos + nickname + log).")]
        public async Task EnlistUserCommand(
            InteractionContext ctx,
            [Option("user", "Usuário a ser alistado")] DiscordUser user,
            [Option("roblox_username", "Username do ROBLOX do usuário")] string robloxUsername,
            [Option("socialrole", "Adicionar cargo social?")] bool socialRole = false)
        {
            await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);

            var config = new JSONReader();
            await config.ReadJSON();

            if (ctx.Guild is null)
            {
                await ReplyErrorAsync(ctx, "Comando inválido", "Este comando só pode ser executado em um servidor (guild).");
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

            DiscordMember targetMember;
            try
            {
                targetMember = await ctx.Guild.GetMemberAsync(user.Id);
            }
            catch
            {
                await ReplyErrorAsync(ctx, "Usuário não encontrado", "O usuário fornecido não é membro deste servidor.");
                return;
            }

            var robloxName = robloxUsername?.Trim() ?? string.Empty;
            var check = await EnlistmentService.VerifyRobloxAsync(robloxName);

            if (!check.Ok)
            {
                await ReplyErrorAsync(ctx, "Erro ao consultar ROBLOX", check.Error ?? "Falha desconhecida.");
                return;
            }

            if (!check.IsLikelyMain)
            {
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(new DiscordEmbedBuilder()
                    .WithTitle("Alistamento negado - Conta provavelmente ALT")
                    .WithDescription("A conta ROBLOX fornecida não atende aos critérios mínimos de confiabilidade.")
                    .WithColor(DiscordColor.IndianRed)
                    .AddField(new DiscordEmbedField("Idade da conta", $"{check.AccountAge.Days} dias", true))
                    .AddField(new DiscordEmbedField("Amigos", check.FriendsCount.ToString(), true))
                    .AddField(new DiscordEmbedField("Badges (bônus)", check.BadgesDisplay, true))));
                return;
            }

            var applied = await EnlistmentService.ApplyAsync(ctx.Client, ctx.Guild, targetMember, config, socialRole, ctx.Channel);

            var logEmbed = EnlistmentService.BuildLogEmbed(
                ctx.Client, targetMember, ctx.User.Mention, robloxName, check, applied, socialRole);

            var announceWarnings = await EnlistmentService.AnnounceAsync(ctx.Client, ctx.Guild, config, logEmbed, targetMember);

            // Os avisos continuam indo para o canal, como antes: quem alista
            // precisa ver que faltou uma permissao mesmo tendo dado certo no
            // geral. A diferenca e que agora eles chegam como lista, e nao
            // escritos de dentro da logica.
            foreach (var warning in applied.Warnings.Concat(announceWarnings))
                await ctx.Channel.SendMessageAsync($"⚠️ **Aviso**: {warning}");

            var successDescription = check.BadgesAvailable
                ? "Bem vindo ao 12°!"
                : "Bem vindo ao 12°! (Badges indisponíveis no momento.)";

            await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(new DiscordEmbedBuilder()
                .WithTitle("12° - Usuário alistado com sucesso")
                .WithDescription(successDescription)
                .WithColor(DiscordColor.Green)
                .WithThumbnail(targetMember.GetAvatarUrl(MediaFormat.Auto))
                .WithFooter("Confirmação de alistamento ROBLOX", ctx.Client.CurrentUser.AvatarUrl)
                .WithTimestamp(DateTimeOffset.UtcNow)));
        }

        private static Task ReplyErrorAsync(InteractionContext ctx, string title, string description) =>
            ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(new DiscordEmbedBuilder()
                .WithTitle(title)
                .WithDescription(description)
                .WithColor(DiscordColor.IndianRed)));
    }
}
