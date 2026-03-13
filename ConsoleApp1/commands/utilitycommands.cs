using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DisCatSharp.CommandsNext;
using DisCatSharp.CommandsNext.Attributes;
using DisCatSharp.Entities;
using Microsoft.VisualBasic;

namespace CornwallUtilities.commands
{
    internal class UtilityCommands : BaseCommandModule
    {
        public string userTag = "<@1072212634201505952>";
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