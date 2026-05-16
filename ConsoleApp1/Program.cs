using CornwallSlashCommandsUtility;
using CornwallUtilities.commands;
using CornwallUtilities.config;
using CornwallUtilities.Services;
using DisCatSharp;
using DisCatSharp.Enums;
using DisCatSharp.Entities;
using DisCatSharp.CommandsNext;
using DisCatSharp.Interactivity;
using DisCatSharp.Interactivity.Extensions;
using DisCatSharp.ApplicationCommands;
using DisCatSharp.ApplicationCommands.Context;
using DisCatSharp.ApplicationCommands.Attributes;
using DisCatSharp.EventArgs;
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
        public static MessageStorageService? MessageStorage { get; private set; }
        public static MessageBlacklistService? MessageBlacklist { get; private set; }
        public static DashboardHttpService? DashboardHttp { get; private set; }

        static async Task Main(string[] args)
        {
            var jsonReader = new JSONReader();
            await jsonReader.ReadJSON();

            var discordConfig = new DiscordConfiguration()
            {
                Intents = DiscordIntents.Guilds | DiscordIntents.GuildMembers | DiscordIntents.GuildMessages | DiscordIntents.MessageContent,
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
            Client.ComponentInteractionCreated += HandleComponentInteraction;
            Client.MessageCreated += HandleMessageCreated;

            var commandsConfig = new CommandsNextConfiguration()
            {
                StringPrefixes = new List<string> { jsonReader.prefix ?? "-" },
                EnableMentionPrefix = true,
                EnableDms = true,
                EnableDefaultHelp = false  
            };
            
            var slashCommandsConfig = new ApplicationCommandsConfiguration();

            Commands = Client.UseCommandsNext(commandsConfig);

            Commands.RegisterCommands<UtilityCommands>();

            var slashCommands = Client.UseApplicationCommands(slashCommandsConfig);
            
            // Register global commands (these have stricter rate limits, so register fewer)
            Console.WriteLine("Registering global commands...");
            slashCommands.RegisterGlobalCommands<RepostMessage>();
            Console.WriteLine("Global commands registered.");
            
            // Register guild commands for faster registration (no rate limits for guild commands)
            var guildId = (ulong)1487938282200236224;
            var guildId2 = (ulong)1397973799105855570; // Your guild ID from config
            Console.WriteLine("Registering guild commands...");
            slashCommands.RegisterGuildCommands<UtilitySlashCommands>(guildId);
            slashCommands.RegisterGuildCommands<CheckSpreadsheetInfo>(guildId);
            slashCommands.RegisterGuildCommands<DmRolesCertainRoles>(guildId);
            slashCommands.RegisterGuildCommands<DmAnyMessage>(guildId);
            slashCommands.RegisterGuildCommands<EnlistUser>(guildId);
            slashCommands.RegisterGuildCommands<RobloxEnlist>(guildId);
            slashCommands.RegisterGuildCommands<DeploymentsMessage>(guildId);
            slashCommands.RegisterGuildCommands<RepostMessage>(guildId);
            slashCommands.RegisterGuildCommands<MessageStorageStatus>(guildId);
            slashCommands.RegisterGuildCommands<DashboardLink>(guildId);
            slashCommands.RegisterGuildCommands<UtilitySlashCommands>(guildId2);
            slashCommands.RegisterGuildCommands<CheckSpreadsheetInfo>(guildId2);
            slashCommands.RegisterGuildCommands<DmRolesCertainRoles>(guildId2);
            slashCommands.RegisterGuildCommands<DmAnyMessage>(guildId2);
            slashCommands.RegisterGuildCommands<EnlistUser>(guildId2);
            slashCommands.RegisterGuildCommands<RobloxEnlist>(guildId2);
            slashCommands.RegisterGuildCommands<DeploymentsMessage>(guildId2);
            slashCommands.RegisterGuildCommands<RepostMessage>(guildId2);
            slashCommands.RegisterGuildCommands<MessageStorageStatus>(guildId2);
            slashCommands.RegisterGuildCommands<DashboardLink>(guildId2);
            Console.WriteLine("Guild commands registered.");
        
            // Initialize message storage service if enabled
            if (jsonReader.messageRepostingEnabled == true && jsonReader.messageRepostingTargetChannelId.HasValue)
            {
                MessageStorage = new MessageStorageService(
                    Client,
                    jsonReader.messageRepostingTargetChannelId.Value,
                    jsonReader.messageRepostingIntervalMinutes ?? 60,
                    jsonReader.messageRepostingRetentionHours ?? 24,
                    jsonReader.messageRepostingMinimumMessages ?? 50
                );
                Console.WriteLine("Message reposting service initialized.");
            }

            if (jsonReader.messageBlacklist?.enabled == true)
            {
                MessageBlacklist = new MessageBlacklistService(
                    jsonReader.messageBlacklist.blacklistedTerms ?? Array.Empty<string>(),
                    jsonReader.messageBlacklist.responseMessage ?? string.Empty,
                    jsonReader.messageBlacklist.responseMessage2Terms ?? (string.IsNullOrWhiteSpace(jsonReader.messageBlacklist.responseMessage2Term)
                        ? Array.Empty<string>()
                        : new[] { jsonReader.messageBlacklist.responseMessage2Term }),
                    jsonReader.messageBlacklist.responseMessage2
                );
                Console.WriteLine("Message blacklist service initialized.");
            }

            DashboardHttp = new DashboardHttpService(Client, new DashboardAuthService("config/dashboard_auth.json"));
            await DashboardHttp.StartAsync();

            // Get the message storage service
            var messageStorage = Program.MessageStorage;

            await Client.ConnectAsync();
            await Task.Delay(-1);
        }

        private static Task Client_Ready(DiscordClient sender, DisCatSharp.EventArgs.ReadyEventArgs e)
        {
            Console.WriteLine("Bot is ready!");
            
            // Initialize terminal interface
            TerminalShenanigans.Initialize(sender);
            
            return Task.CompletedTask;
        }

        private static async Task HandleComponentInteraction(DiscordClient sender, ComponentInteractionCreateEventArgs e)
        {
            try
            {
                // Link buttons don't send interactions, so this handler is only for other buttons
                await e.Interaction.CreateResponseAsync(InteractionResponseType.ChannelMessageWithSource, new DiscordInteractionResponseBuilder()
                    .WithContent("❌ Botão não reconhecido.")
                    .AsEphemeral());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling button interaction: {ex.Message}");
                try
                {
                    await e.Interaction.CreateResponseAsync(InteractionResponseType.ChannelMessageWithSource, new DiscordInteractionResponseBuilder()
                        .WithContent("❌ Ocorreu um erro ao processar esta interação.")
                        .AsEphemeral());
                }
                catch
                {
                    // If we can't respond, the interaction may have expired
                    Console.WriteLine("Failed to respond to interaction - may have expired");
                }
            }
        }

        private static async Task HandleMessageCreated(DiscordClient sender, MessageCreateEventArgs e)
        {
            // Store message if the service is enabled
            if (MessageStorage != null)
            {
                MessageStorage.StoreMessage(e.Message);
            }

            if (MessageBlacklist != null)
            {
                await MessageBlacklist.HandleMessageAsync(e.Message);
            }
        }

    }
}
