using DisCatSharp.ApplicationCommands;
using DisCatSharp.ApplicationCommands.Attributes;
using DisCatSharp.ApplicationCommands.Context;
using DisCatSharp.Entities;
using DisCatSharp.Enums;
using CornwallUtilities.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CornwallUtilities.commands
{
    public class RepostMessage : ApplicationCommandsModule
    {
        [SlashCommand("repost", "Reposts a random stored message")]
        public async Task ExecuteRepost(InteractionContext ctx)
        {
            // Get the message storage service
            var messageStorage = Program.MessageStorage;

            if (messageStorage == null)
            {
                await ctx.CreateResponseAsync(InteractionResponseType.ChannelMessageWithSource, new DiscordInteractionResponseBuilder()
                    .WithContent("❌ Message reposting service is not available.")
                    .AsEphemeral());
                return;
            }

            var messageCount = messageStorage.GetMessageCount();
            if (messageCount == 0)
            {
                await ctx.CreateResponseAsync(InteractionResponseType.ChannelMessageWithSource, new DiscordInteractionResponseBuilder()
                    .WithContent("📭 No messages are available to repost.")
                    .AsEphemeral());
                return;
            }

            // Defer the response since we might need time to process
            await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);

            try
            {
                var success = await messageStorage.ManualRepostAsync();
                if (success)
                {
                    await ctx.EditResponseAsync(new DiscordWebhookBuilder()
                        .WithContent("✅ Message reposted successfully! The automatic reposting service is now on cooldown for 2 hours."));
                }
                else
                {
                    var currentMessageCount = messageStorage.GetMessageCount();
                    await ctx.EditResponseAsync(new DiscordWebhookBuilder()
                        .WithContent($"📭 Cannot repost message. Need at least {Program.MessageStorage?.GetMinimumMessages() ?? 50} messages stored, but only have {currentMessageCount}."));
                }
            }
            catch (Exception ex)
            {
                await ctx.EditResponseAsync(new DiscordWebhookBuilder()
                    .WithContent("❌ An error occurred while reposting the message."));
                Console.WriteLine($"Error in manual repost: {ex.Message}");
            }
        }
    }
}
