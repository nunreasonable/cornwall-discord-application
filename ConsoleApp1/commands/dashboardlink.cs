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

            var (code, expiresAt) = await Program.DashboardHttp.GenerateLinkCodeAsync(ctx.User);
            await ctx.CreateResponseAsync(InteractionResponseType.ChannelMessageWithSource, new DiscordInteractionResponseBuilder()
                .WithContent($"Seu código de login: **{code}**\nExpira em: <t:{expiresAt.ToUnixTimeSeconds()}:R>\nUse no site do dashboard.")
                .AsEphemeral());
        }
    }
}
