using System;
using System.Threading.Tasks;
using DisCatSharp;
using DisCatSharp.Entities;
using DisCatSharp.Enums;

namespace CornwallUtilities
{
    public class TerminalShenanigans
    {
        static DiscordChannel? currentChannel;
        static DiscordClient? client;

        public static void Initialize(DiscordClient discordClient)
        {
            client = discordClient;
            StartTerminalInterface();
        }

        private static void StartTerminalInterface()
        {
            Console.WriteLine("Interface terminal ativada. Digite >help para comandos.");

            _ = Task.Run(async () =>
            {
                while (true)
                {
                    var input = Console.ReadLine();

                    if (string.IsNullOrWhiteSpace(input))
                        continue;

                    if (input.StartsWith(">"))
                    {
                        var cmd = input.Substring(1).Trim();

                        if (cmd.StartsWith("channel "))
                        {
                            try
                            {
                                ulong id = ulong.Parse(cmd.Split(' ')[1]);
                                currentChannel = await client!.GetChannelAsync(id);
                                Console.WriteLine($"Canal definido: {currentChannel?.Name}");
                            }
                            catch
                            {
                                Console.WriteLine("ID de canal inválido.");
                            }
                        }

                        else if (cmd == "exit")
                        {
                            Console.WriteLine("Encerrando interface terminal...");
                            break;
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
                            continue;
                        }

                        await currentChannel.SendMessageAsync(input);
                    }
                }
            });
        }
    }
}