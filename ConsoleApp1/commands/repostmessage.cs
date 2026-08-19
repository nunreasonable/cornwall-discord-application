using DisCatSharp.ApplicationCommands;
using DisCatSharp.ApplicationCommands.Attributes;
using DisCatSharp.ApplicationCommands.Context;
using DisCatSharp.Entities;
using DisCatSharp.Enums;
using CornwallUtilities.commands;
using CornwallUtilities.config;
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
            // Este e um comando GLOBAL: sem esta checagem, qualquer pessoa de
            // qualquer servidor onde o bot esteja podia disparar um repost no
            // canal configurado.
            var config = new JSONReader();
            await config.ReadJSON();

            var denied = AuditPermissions.CheckStaff(ctx, config);
            if (denied is not null)
            {
                await ctx.CreateResponseAsync(InteractionResponseType.ChannelMessageWithSource,
                    new DiscordInteractionResponseBuilder().AddEmbed(denied).AsEphemeral());
                return;
            }

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

            // Defer as ephemeral so the final confirmation is visible only to the command user.
            await ctx.CreateResponseAsync(
                InteractionResponseType.DeferredChannelMessageWithSource,
                new DiscordInteractionResponseBuilder().AsEphemeral());

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
