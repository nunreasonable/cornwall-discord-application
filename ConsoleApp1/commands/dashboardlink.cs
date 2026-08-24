using System;
using System.Threading.Tasks;
using CornwallUtilities.Services;
using DisCatSharp.ApplicationCommands;
using DisCatSharp.ApplicationCommands.Context;
using DisCatSharp.Enums;
using DisCatSharp.ApplicationCommands.Attributes;
using DisCatSharp.Entities;

namespace CornwallUtilities.commands
{
    internal class DashboardLink : ApplicationCommandsModule
    {
        [SlashCommand("dashboardlink", "Gera um código de login único para o dashboard administrativo.")]
        public async Task DashboardLinkCommand(InteractionContext ctx)
        {
            if (Program.DashboardHttp is null)
            {
                await ctx.CreateResponseAsync(InteractionResponseType.ChannelMessageWithSource, new DiscordInteractionResponseBuilder()
                    .WithContent("❌ Dashboard indisponível no momento.")
                    .AsEphemeral());
                return;
            }

            // Recusa de cara quem nao teria nivel nenhum no painel.
            //
            // O codigo sozinho ja era inofensivo - o login so passa com
            // ResolvePermissionLevelAsync > 0 - mas emiti-lo para qualquer membro
            // gastava uma ida ao Discord e devolvia um codigo que so podia falhar,
            // sem dizer por que.
            if (Program.DashboardAuth is not null)
            {
                var level = await Program.DashboardAuth.ResolvePermissionLevelAsync(ctx.Client, ctx.User.Id);
                // HadLookupFailure: se o Discord falhou, nao da para afirmar que a
                // pessoa NAO tem acesso - emite o codigo e deixa o login decidir.
                if (!level.HadLookupFailure && level.PermissionLevel <= 0)
                {
                    await ctx.CreateResponseAsync(InteractionResponseType.ChannelMessageWithSource, new DiscordInteractionResponseBuilder()
                        .WithContent("Você não tem acesso ao dashboard administrativo.")
                        .AsEphemeral());
                    return;
                }
            }

            var (code, expiresAt) = await Program.DashboardHttp.GenerateLinkCodeAsync(ctx.User);
            await ctx.CreateResponseAsync(InteractionResponseType.ChannelMessageWithSource, new DiscordInteractionResponseBuilder()
                .WithContent($"Seu código de login: **{code}**\nExpira em: <t:{expiresAt.ToUnixTimeSeconds()}:R>\nUse no site do dashboard.")
                .AsEphemeral());
        }
    }
}
