using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using DSharpPlus.SlashCommands;
using DSharpPlus.Entities;

namespace CornwallSlashCommandsUtility
{
    internal class UtilitySlashCommands : ApplicationCommandModule
    {
        [SlashCommand("ping", "Responde com sua latência em ms")]
        public async Task PingCommand(InteractionContext ctx)
        {
            var latency = ctx.Client.Ping;
            await ctx.CreateResponseAsync(DSharpPlus.InteractionResponseType.ChannelMessageWithSource, new DiscordInteractionResponseBuilder()
                .WithContent($"Pong! Latência: {latency}ms"));
        }
    }
}