using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using DisCatSharp.CommandsNext;
using DisCatSharp.CommandsNext.Attributes;
using DisCatSharp.Entities;

namespace CornwallUtilities.commands
{
    internal class UtilityCommands : BaseCommandModule
    {
        public string userTag = "<@1072212634201505952>";

        [Command("osinfo")]
        [Description("Displays basic information about the system running the bot.")]
        public async Task OsInfo(CommandContext ctx)
        {
            var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
            var uptimeText = $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m";
            var memoryMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);

            var embed = new DiscordEmbedBuilder()
                .WithTitle("System Information")
                .WithColor(DiscordColor.Blurple)
                .AddField(new DiscordEmbedField("OS", RuntimeInformation.OSDescription, true))
                .AddField(new DiscordEmbedField("OS Architecture", RuntimeInformation.OSArchitecture.ToString(), true))
                .AddField(new DiscordEmbedField("Process Architecture", RuntimeInformation.ProcessArchitecture.ToString(), true))
                .AddField(new DiscordEmbedField(".NET Runtime", RuntimeInformation.FrameworkDescription, true))
                .AddField(new DiscordEmbedField("CPU Cores", Environment.ProcessorCount.ToString(), true))
                .AddField(new DiscordEmbedField("System Uptime", uptimeText, true))
                .AddField(new DiscordEmbedField("Bot Memory", $"{memoryMb:F1} MB", true))
                .WithTimestamp(DateTimeOffset.UtcNow);

            await ctx.Channel.SendMessageAsync(new DiscordMessageBuilder()
                .WithReply(ctx.Message.Id)
                .AddEmbed(embed));
        }

        [Command("vsfdliliane")]
        public async Task Vsfdliliane(CommandContext ctx)
        {
            const ulong allowedUserId = 1402344358199689348;
            if (ctx.User.Id != allowedUserId)
            {
                await ctx.Channel.SendMessageAsync(new DiscordMessageBuilder()
                .WithContent("Você não tem permissão para usar este comando.")
                .WithReply(ctx.Message.Id));
                return;
            }
            var responses = new[]
            {
                $"Vai se fuder {userTag}",
                $"Vai tomar no cu,{userTag} porra",
                $"Caralho, {userTag} burrão hein.",
                $"Vai pra casa do chapéu, {userTag} fdppppp!!!!1!!1.",
            };

            var rand = new Random();
            var response = responses[rand.Next(responses.Length)];
            await ctx.Channel.SendMessageAsync(new DiscordMessageBuilder()
                .WithContent(response)
                .WithReply(ctx.Message.Id));
        }
    }
}