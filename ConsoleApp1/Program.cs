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
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
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

        private static Timer? s_threadPoolCanary;

        static async Task Main(string[] args)
        {
            InstallGlobalExceptionHandlers();

            var jsonReader = new JSONReader();
            await jsonReader.ReadJSON();

            var discordConfig = new DiscordConfiguration()
            {
                Intents = DiscordIntents.Guilds |
                          DiscordIntents.GuildMembers |
                          DiscordIntents.GuildMessages |
                          DiscordIntents.DirectMessages |
                          DiscordIntents.MessageContent,
                Token = jsonReader.token,
                TokenType = TokenType.Bot,
                AutoReconnect = true,
                MinimumLogLevel = LogLevel.Information,
                DisableUpdateCheck = true,
                EnableSentry = false,
                MessageCacheSize = 256
            };

            Client = new DiscordClient(discordConfig);

            HookGatewayDiagnostics(Client);

            // A interface de terminal e iniciada uma unica vez, aqui - e nao em
            // Client_Ready, que dispara de novo a cada reconexao do gateway.
            TerminalShenanigans.Initialize(Client);

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
            foreach (var gid in new[] { guildId, guildId2 })
            {
                slashCommands.RegisterGuildCommands<UtilitySlashCommands>(gid);
                slashCommands.RegisterGuildCommands<CheckSpreadsheetInfo>(gid);
                slashCommands.RegisterGuildCommands<DmRolesCertainRoles>(gid);
                slashCommands.RegisterGuildCommands<DmAnyMessage>(gid);
                slashCommands.RegisterGuildCommands<EnlistUser>(gid);
                slashCommands.RegisterGuildCommands<RobloxEnlist>(gid);
                slashCommands.RegisterGuildCommands<DeploymentsMessage>(gid);
                slashCommands.RegisterGuildCommands<MessageStorageStatus>(gid);
                slashCommands.RegisterGuildCommands<DashboardLink>(gid);

                // Auditoria
                slashCommands.RegisterGuildCommands<AuditAdd>(gid);
                slashCommands.RegisterGuildCommands<AuditPush>(gid);
                slashCommands.RegisterGuildCommands<AuditEdit>(gid);
                slashCommands.RegisterGuildCommands<AuditSetRanks>(gid);
                slashCommands.RegisterGuildCommands<AuditCheck>(gid);
                slashCommands.RegisterGuildCommands<AuditImport>(gid);
            }
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
                    Client,
                    jsonReader.messageBlacklist.blacklistedTerms ?? Array.Empty<string>(),
                    jsonReader.messageBlacklist.responseMessage ?? string.Empty,
                    jsonReader.messageBlacklist.responseMessage2Terms ?? (string.IsNullOrWhiteSpace(jsonReader.messageBlacklist.responseMessage2Term)
                        ? Array.Empty<string>()
                        : new[] { jsonReader.messageBlacklist.responseMessage2Term }),
                    jsonReader.messageBlacklist.responseMessage2,
                    jsonReader.messageBlacklist.notifyUserIds,
                    jsonReader.messageBlacklist.dmAlertCooldownMinutes
                );
                Console.WriteLine("Message blacklist service initialized.");
            }

            // Consumidor unico das mensagens que exigem chamadas REST.
            MessagePipeline.Start(ProcessMessageAsync);

            DashboardHttp = new DashboardHttpService(Client, new DashboardAuthService("config/dashboard_auth.json"));
            await DashboardHttp.StartAsync();

            StartThreadPoolCanary();

            await Client.ConnectAsync();
            await Task.Delay(-1);
        }

        /// <summary>
        /// Redes de seguranca globais: sem isso, uma excecao em `async void` ou em
        /// uma Task esquecida derruba o processo sem deixar rastro no log.
        /// </summary>
        private static void InstallGlobalExceptionHandlers()
        {
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Console.WriteLine($"[unobserved] {e.Exception}");
                e.SetObserved();
            };

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Console.WriteLine($"[fatal] {e.ExceptionObject}");
        }

        /// <summary>
        /// Eventos de diagnostico do gateway. Sao baratos e transformam
        /// "o bot morreu" em uma linha de log com hora e motivo.
        /// </summary>
        private static void HookGatewayDiagnostics(DiscordClient client)
        {
            client.SocketOpened += (s, e) =>
            {
                Console.WriteLine("[gw] socket aberto");
                return Task.CompletedTask;
            };

            client.SocketClosed += (s, e) =>
            {
                Console.WriteLine($"[gw] socket fechado {e.CloseCode}: {e.CloseMessage}");
                return Task.CompletedTask;
            };

            client.SocketErrored += (s, e) =>
            {
                Console.WriteLine($"[gw] erro de socket: {e.Exception}");
                return Task.CompletedTask;
            };

            client.Zombied += (s, e) =>
            {
                Console.WriteLine($"[gw] ZUMBI apos {e.Failures} heartbeats falhos (guildDownloadCompleted={e.GuildDownloadCompleted})");
                return Task.CompletedTask;
            };

            client.Resumed += (s, e) =>
            {
                Console.WriteLine("[gw] sessao retomada");
                return Task.CompletedTask;
            };

            client.ClientErrored += (s, e) =>
            {
                Console.WriteLine($"[gw] erro no evento {e.EventName}: {e.Exception}");
                return Task.CompletedTask;
            };

            client.Heartbeated += (s, e) =>
            {
                if (e.Ping > 1000)
                    Console.WriteLine($"[gw] heartbeat lento: {e.Ping}ms");
                return Task.CompletedTask;
            };
        }

        /// <summary>
        /// Canario de starvation do ThreadPool. Se PendingWorkItemCount so cresce,
        /// alguma coisa esta bloqueando threads do pool e o heartbeat vai falhar.
        /// </summary>
        private static void StartThreadPoolCanary()
        {
            s_threadPoolCanary = new Timer(static _ =>
            {
                try
                {
                    var pending = ThreadPool.PendingWorkItemCount;
                    var threads = ThreadPool.ThreadCount;
                    var dropped = MessagePipeline.DroppedCount;

                    if (pending > 50 || dropped > 0)
                        Console.WriteLine($"[pool] threads={threads} pendentes={pending} mensagensDescartadas={dropped}");
                }
                catch
                {
                    // diagnostico nunca pode derrubar o processo
                }
            }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        private static Task Client_Ready(DiscordClient sender, DisCatSharp.EventArgs.ReadyEventArgs e)
        {
            Console.WriteLine("Bot is ready!");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Handler apenas de diagnostico: NAO responde a interacao.
        ///
        /// No DisCatSharp as submissoes de modal e os cliques nos botoes de
        /// paginacao chegam por este mesmo evento e ja tem dono - os waiters da
        /// Interactivity. Como cada interacao aceita uma unica resposta inicial,
        /// responder aqui disputava essa resposta e quebrava modais e paginacao.
        /// Os ids dos botoes de paginacao sao gerados dinamicamente, entao nao da
        /// para filtrar por prefixo com seguranca; por isso este handler so
        /// observa. Um botao realmente orfao mostra o aviso nativo do Discord.
        /// </summary>
        private static Task HandleComponentInteraction(DiscordClient sender, ComponentInteractionCreateEventArgs e)
        {
            if (e.Interaction.Type != InteractionType.ModalSubmit)
                Console.WriteLine($"[component] interacao recebida: {e.Interaction.Data?.CustomId}");

            return Task.CompletedTask;
        }

        /// <summary>
        /// Handler do gateway: precisa retornar rapido. Trabalho que faz REST vai
        /// para a fila em segundo plano.
        /// </summary>
        private static Task HandleMessageCreated(DiscordClient sender, MessageCreateEventArgs e)
        {
            if (e.Author.IsBot || e.Author.IsSystem == true)
                return Task.CompletedTask;

            // Barato e em memoria - pode ficar no caminho do despacho.
            MessageStorage?.StoreMessage(e.Message);

            MessagePipeline.Enqueue(e.Message);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Roda no consumidor da fila, fora do caminho do heartbeat.
        /// </summary>
        private static async Task ProcessMessageAsync(DiscordMessage message)
        {
            if (string.Equals(message.Content?.Trim(), "oi voce quer ir pra LLL", StringComparison.OrdinalIgnoreCase))
            {
                await message.Channel.SendMessageAsync(new DiscordMessageBuilder()
                    .WithContent("sai maluco todo dia isso si fude")
                    .WithReply(message.Id));
            }

            if (MessageBlacklist != null)
            {
                await MessageBlacklist.HandleMessageAsync(message);
            }
        }
    }
}
