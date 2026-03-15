using DisCatSharp.ApplicationCommands;
using DisCatSharp.ApplicationCommands.Attributes;
using DisCatSharp.ApplicationCommands.Context;
using DisCatSharp.Entities;
using DisCatSharp.Enums;
using CornwallUtilities.Services;
using System.Threading.Tasks;

namespace CornwallUtilities.commands
{
    public class MessageStorageStatus : ApplicationCommandsModule
    {
        [SlashCommand("messages", "Check message storage status and statistics")]
        public async Task ExecuteStatus(InteractionContext ctx)
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
            var minimumRequired = messageStorage.GetMinimumMessages();
            var canRepost = messageCount >= minimumRequired;

            // Create status embed
            var embed = new DiscordEmbedBuilder
            {
                Title = "📊 Message Storage Status",
                Color = canRepost ? DiscordColor.Green : DiscordColor.Orange,
                Timestamp = System.DateTime.UtcNow
            };

            embed.AddField(new DiscordEmbedField("📝 Stored Messages", messageCount.ToString(), true));
            embed.AddField(new DiscordEmbedField("⚠️ Minimum Required", minimumRequired.ToString(), true));
            embed.AddField(new DiscordEmbedField("✅ Can Repost", canRepost ? "Yes" : "No", true));

            // Add progress bar
            var percentage = messageCount >= minimumRequired ? 100 : (messageCount * 100) / minimumRequired;
            var progressBar = new string('█', percentage / 10) + new string('░', 10 - (percentage / 10));
            embed.AddField(new DiscordEmbedField("📈 Progress", $"`{progressBar}` {percentage}%", false));

            embed.AddField(new DiscordEmbedField("ℹ️ Information", 
                canRepost 
                    ? "✅ Ready to repost messages! Use `/repost` to manually trigger a repost." 
                    : $"⏳ Need {minimumRequired - messageCount} more messages before reposting is available.", false));

            await ctx.CreateResponseAsync(InteractionResponseType.ChannelMessageWithSource, new DiscordInteractionResponseBuilder()
                .AddEmbed(embed)
                .AsEphemeral());
        }
    }
}
