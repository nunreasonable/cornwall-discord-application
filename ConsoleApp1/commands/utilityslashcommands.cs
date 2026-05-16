using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using DisCatSharp.ApplicationCommands;
using DisCatSharp.Entities;
using DisCatSharp.Enums;
using DisCatSharp.ApplicationCommands.Context;
using DisCatSharp.ApplicationCommands.Attributes;

namespace CornwallSlashCommandsUtility
{
    internal class UtilitySlashCommands : ApplicationCommandsModule
    {
        [SlashCommand("ping", "Responde com sua latência em ms")]
        public async Task PingCommand(InteractionContext ctx)
        {
            var latency = ctx.Client.Ping;
            await ctx.CreateResponseAsync(InteractionResponseType.ChannelMessageWithSource, new DiscordInteractionResponseBuilder()
                .WithContent($"Pong! Latência: {latency}ms"));
        }
    }
}