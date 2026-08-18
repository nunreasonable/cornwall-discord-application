using System;
using System.Threading;
using System.Threading.Tasks;
using DisCatSharp;
using DisCatSharp.Entities;
using DisCatSharp.Enums;

namespace CornwallUtilities
{
    public class TerminalShenanigans
    {
        private static DiscordChannel? currentChannel;
        private static DiscordClient? client;

        // Guarda de idempotencia: Client.Ready dispara novamente a cada reconexao
        // do gateway, e cada chamada anterior criava mais um loop de leitura de
        // stdin. Isso vazava threads e acabava travando o heartbeat.
        private static int s_initialized;

        public static void Initialize(DiscordClient discordClient)
        {
            client = discordClient;

            if (Interlocked.Exchange(ref s_initialized, 1) == 1)
                return; // no maximo um loop, para sempre

            if (Console.IsInputRedirected)
            {
                Console.WriteLine("[terminal] stdin nao interativo; interface de terminal desativada.");
                return;
            }

            Console.WriteLine("Interface terminal ativada. Digite >help para comandos.");

            // Thread dedicada (nao do ThreadPool): Console.ReadLine bloqueia, e
            // bloquear uma thread do pool tira recursos do heartbeat do gateway.
            var thread = new Thread(RunLoop)
            {
                IsBackground = true,
                Name = "terminal-input"
            };
            thread.Start();
        }

        private static void RunLoop()
        {
            while (true)
            {
                var input = Console.ReadLine();

                if (input is null)
                    break; // EOF - nunca girar em loop vazio

                if (string.IsNullOrWhiteSpace(input))
                    continue;

                try
                {
                    if (!HandleCommand(input))
                        break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[terminal] erro: {ex.Message}");
                }
            }

            Console.WriteLine("[terminal] interface encerrada.");
        }

        /// <summary>
        /// Executa um comando do terminal. Retorna false quando a interface deve encerrar.
        /// </summary>
        private static bool HandleCommand(string input)
        {
            if (input.StartsWith(">"))
            {
                var cmd = input.Substring(1).Trim();

                if (cmd.StartsWith("channel "))
                {
                    try
                    {
                        ulong id = ulong.Parse(cmd.Split(' ')[1]);
                        currentChannel = client!.GetChannelAsync(id).GetAwaiter().GetResult();
                        Console.WriteLine($"Canal definido: {currentChannel?.Name}");
                    }
                    catch
                    {
                        Console.WriteLine("ID de canal invalido.");
                    }
                }
                else if (cmd == "exit")
                {
                    Console.WriteLine("Encerrando interface terminal...");
                    return false;
                }
                else if (cmd == "help")
                {
                    Console.WriteLine("Comandos:");
                    Console.WriteLine(">channel ID   - definir canal");
                    Console.WriteLine("Texto normal  - enviar mensagem ao canal definido");
                    Console.WriteLine(">exit         - sair da interface");
                }
            }
            else
            {
                if (currentChannel is null)
                {
                    Console.WriteLine("Defina um canal primeiro com >channel ID");
                    return true;
                }

                currentChannel.SendMessageAsync(input).GetAwaiter().GetResult();
            }

            return true;
        }
    }
}
