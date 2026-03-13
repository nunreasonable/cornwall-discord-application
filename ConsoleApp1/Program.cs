using CornwallSlashCommandsUtility;
using CornwallUtilities.commands;
using CornwallUtilities.config;
using DisCatSharp;
using DisCatSharp.CommandsNext;
using DisCatSharp.Interactivity;
using DisCatSharp.Interactivity.Extensions;
using DisCatSharp.ApplicationCommands;
using Microsoft.VisualBasic;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CornwallUtilities
{
    internal class Program
    {
        private static DiscordClient? Client { get; set; }
        private static CommandsNextExtension? Commands { get; set; }

        static async Task Main(string[] args)
        {
            var jsonReader = new JSONReader();
            await jsonReader.ReadJSON();

            var discordConfig = new DiscordConfiguration()
            {
                Intents = DiscordIntents.Guilds | DiscordIntents.GuildMembers | DiscordIntents.GuildMessages,
                Token = jsonReader.token,
                TokenType = TokenType.Bot,
                AutoReconnect = true
            };

            Client = new DiscordClient(discordConfig);

            // Habilita a extensão de interatividade para aguardar cliques em botões
            Client.UseInteractivity(new InteractivityConfiguration
            {
                Timeout = TimeSpan.FromMinutes(3)
            });

            Client.Ready += Client_Ready;

            var commandsConfig = new CommandsNextConfiguration()
            {
              StringPrefixes = new string[] { jsonReader.prefix ?? "-"},
              EnableMentionPrefix = true,
              EnableDms = true,
              EnableDefaultHelp = false,  
            };

            var slashCommandsConfig = new ApplicationCommandsConfiguration()
            {
               Services = null,
            };

            Commands = Client.UseCommandsNext(commandsConfig);

            Commands.RegisterCommands<UtilityCommands>();

            var slashCommands = Client.UseApplicationCommands(slashCommandsConfig);
            slashCommands.RegisterCommands<UtilitySlashCommands>();
            slashCommands.RegisterCommands<CheckSpreadsheetInfo>();
            slashCommands.RegisterCommands<DmRolesCertainRoles>();
            slashCommands.RegisterCommands<DmAnyMessage>();
            slashCommands.RegisterCommands<EnlistUser>();
            slashCommands.RegisterCommands<RobloxEnlist>();

            await Client.ConnectAsync();
            await Task.Delay(-1);
        }

        private static Task Client_Ready(DiscordClient sender, DisCatSharp.EventArgs.ReadyEventArgs e)
        {
            Console.WriteLine("Bot is ready!");
            return Task.CompletedTask;
        }

    }
}

