using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DisCatSharp;
using DisCatSharp.Entities;
using DisCatSharp.Enums;

namespace CornwallUtilities
{
    /// <summary>
    /// Interface de comandos em texto do bot (>channel, >exit, texto solto vira
    /// mensagem no canal escolhido).
    ///
    /// Historicamente ela so existia sobre o stdin. Em producao o bot roda como
    /// servico (`ccore-bot.service`), e servico do systemd recebe stdin ligado
    /// em /dev/null - ou seja, `Console.IsInputRedirected` sempre era true e a
    /// interface simplesmente nunca subia ("stdin nao interativo; interface de
    /// terminal desativada"). Nao havia como usa-la na maquina de verdade.
    ///
    /// Agora existem dois caminhos, e os dois usam o mesmo interpretador:
    ///
    /// 1. stdin, quando o processo tem terminal de verdade (`dotnet run` na mao);
    /// 2. um socket Unix, sempre - inclusive sob o systemd. Para abrir uma
    ///    sessao basta rodar o proprio binario com `--terminal`, que conecta no
    ///    socket do bot que ja esta rodando.
    ///
    /// O socket fica no diretorio de runtime do usuario (modo 0700), entao so o
    /// dono do processo consegue falar com ele.
    /// </summary>
    public class TerminalShenanigans
    {
        private static DiscordClient? client;

        // Guarda de idempotencia: Client.Ready dispara novamente a cada reconexao
        // do gateway, e cada chamada anterior criava mais um loop de leitura de
        // stdin. Isso vazava threads e acabava travando o heartbeat.
        private static int s_initialized;

        private const string HelpText =
            "Comandos:\n" +
            ">channel ID   - definir canal\n" +
            "Texto normal  - enviar mensagem ao canal definido\n" +
            ">help         - esta ajuda\n" +
            ">exit         - encerrar esta sessao";

        /// <summary>
        /// Estado de uma sessao. Cada conexao no socket tem o seu proprio canal
        /// e a sua propria saida - duas pessoas conectadas ao mesmo tempo nao
        /// pisam no canal uma da outra, o que aconteceria com um campo estatico.
        /// </summary>
        private sealed class TerminalSession
        {
            public TerminalSession(TextWriter output) => Output = output;

            public TextWriter Output { get; }

            public DiscordChannel? Channel { get; set; }
        }

        public static void Initialize(DiscordClient discordClient)
        {
            client = discordClient;

            if (Interlocked.Exchange(ref s_initialized, 1) == 1)
                return; // no maximo um listener, para sempre

            StartControlSocket();

            if (Console.IsInputRedirected)
            {
                Console.WriteLine($"[terminal] stdin nao interativo; use `{AttachHint()}` para abrir uma sessao.");
                return;
            }

            Console.WriteLine("Interface terminal ativada. Digite >help para comandos.");

            // Thread dedicada (nao do ThreadPool): Console.ReadLine bloqueia, e
            // bloquear uma thread do pool tira recursos do heartbeat do gateway.
            var thread = new Thread(RunStdinLoop)
            {
                IsBackground = true,
                Name = "terminal-input"
            };
            thread.Start();
        }

        private static void RunStdinLoop()
        {
            var session = new TerminalSession(Console.Out);

            while (true)
            {
                var input = Console.ReadLine();

                if (input is null)
                    break; // EOF - nunca girar em loop vazio

                if (string.IsNullOrWhiteSpace(input))
                    continue;

                try
                {
                    // Thread dedicada, fora do ThreadPool: bloquear aqui e o unico
                    // lugar onde e aceitavel, entao GetResult nao afeta o gateway.
                    if (!HandleCommandAsync(session, input).GetAwaiter().GetResult())
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
        /// Executa um comando do terminal. Retorna false quando a sessao deve encerrar.
        /// As chamadas REST sao aguardadas de verdade: bloquear uma thread do pool
        /// com GetResult, como antes, tirava recursos do heartbeat do gateway.
        /// </summary>
        private static async Task<bool> HandleCommandAsync(TerminalSession session, string input)
        {
            var output = session.Output;

            if (input.StartsWith(">"))
            {
                var cmd = input.Substring(1).Trim();

                if (cmd.StartsWith("channel "))
                {
                    try
                    {
                        ulong id = ulong.Parse(cmd.Split(' ')[1]);
                        session.Channel = await client!.GetChannelAsync(id);
                        output.WriteLine($"Canal definido: {session.Channel?.Name}");
                    }
                    catch
                    {
                        output.WriteLine("ID de canal invalido.");
                    }
                }
                else if (cmd == "exit")
                {
                    output.WriteLine("Encerrando interface terminal...");
                    return false;
                }
                else if (cmd == "help")
                {
                    output.WriteLine(HelpText);
                }
                else
                {
                    output.WriteLine($"Comando desconhecido: >{cmd}. Use >help.");
                }
            }
            else
            {
                if (session.Channel is null)
                {
                    output.WriteLine("Defina um canal primeiro com >channel ID");
                    return true;
                }

                await session.Channel.SendMessageAsync(input);
            }

            return true;
        }

        // ---------------------------------------------------------------
        // Socket de controle (lado servidor)
        // ---------------------------------------------------------------

        private static void StartControlSocket()
        {
            string path;

            try
            {
                path = PrepareSocketPath();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[terminal] nao foi possivel preparar o socket: {ex.Message}");
                return;
            }

            Socket listener;

            try
            {
                listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                listener.Bind(new UnixDomainSocketEndPoint(path));
                listener.Listen(4);

                // Cinto e suspensorio: o diretorio ja e 0700, mas se alguem
                // apontar CCORE_TERMINAL_SOCKET para um lugar publico o socket
                // em si continua restrito ao dono.
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[terminal] socket de controle indisponivel: {ex.Message}");
                return;
            }

            // O arquivo do socket nao some sozinho quando o processo morre; sem
            // isto o proximo start encontraria um socket orfao (tratado em
            // PrepareSocketPath, mas melhor nao deixar lixo).
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try
                {
                    listener.Dispose();
                    File.Delete(path);
                }
                catch
                {
                    // encerrando o processo; nada util a fazer aqui
                }
            };

            Console.WriteLine($"[terminal] socket de controle em {path} (use `{AttachHint()}`)");

            _ = Task.Run(() => AcceptLoopAsync(listener));
        }

        private static async Task AcceptLoopAsync(Socket listener)
        {
            while (true)
            {
                Socket connection;

                try
                {
                    connection = await listener.AcceptAsync();
                }
                catch (ObjectDisposedException)
                {
                    return; // listener fechado no shutdown
                }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.OperationAborted or SocketError.Interrupted)
                {
                    return; // listener fechado embaixo do accept
                }
                catch (Exception ex)
                {
                    // Uma falha isolada de accept (descritor esgotado, cliente
                    // que desiste no meio do handshake) nao pode aposentar o
                    // socket de controle ate o proximo restart do bot - era o
                    // que o `return` daqui fazia. A pausa evita que um listener
                    // quebrado de verdade gire consumindo uma CPU inteira, do
                    // mesmo jeito que o laco de accept do dashboard.
                    Console.WriteLine($"[terminal] accept falhou: {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    continue;
                }

                _ = Task.Run(() => ServeSessionAsync(connection));
            }
        }

        private static async Task ServeSessionAsync(Socket connection)
        {
            Console.WriteLine("[terminal] sessao conectada pelo socket de controle.");

            try
            {
                using var stream = new NetworkStream(connection, ownsSocket: true);
                using var reader = new StreamReader(stream, new UTF8Encoding(false));
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

                var session = new TerminalSession(writer);

                await writer.WriteLineAsync("Interface terminal ativada. Digite >help para comandos.");

                while (true)
                {
                    var input = await reader.ReadLineAsync();

                    if (input is null)
                        break; // cliente desconectou

                    if (string.IsNullOrWhiteSpace(input))
                        continue;

                    try
                    {
                        // O interpretador agora e assincrono de verdade, entao basta
                        // await - nada bloqueia uma thread do pool.
                        var keepGoing = await HandleCommandAsync(session, input);

                        if (!keepGoing)
                            break;
                    }
                    catch (Exception ex)
                    {
                        await writer.WriteLineAsync($"[terminal] erro: {ex.Message}");
                    }
                }
            }
            catch (IOException)
            {
                // cliente sumiu no meio de uma escrita; sessao acabou
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[terminal] sessao terminou com erro: {ex.Message}");
            }

            Console.WriteLine("[terminal] sessao do socket encerrada.");
        }

        // ---------------------------------------------------------------
        // Cliente (`ConsoleApp1 --terminal`)
        // ---------------------------------------------------------------

        /// <summary>
        /// Conecta no socket do bot que ja esta rodando e liga o terminal atual
        /// nele. Retorna o codigo de saida do processo.
        /// </summary>
        public static async Task<int> AttachAsync()
        {
            var path = ResolveSocketPath();

            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"[terminal] socket nao encontrado em {path}. O bot esta rodando?");
                return 1;
            }

            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[terminal] nao foi possivel conectar em {path}: {ex.Message}");
                return 1;
            }

            using var stream = new NetworkStream(socket, ownsSocket: false);

            // Ao acabar o stdin (Ctrl-D, ou um `printf ... | ... --terminal`),
            // fecha so o lado de escrita: o bot ve EOF, responde o que faltava e
            // encerra a sessao. Se em vez disso saissemos na hora, a resposta do
            // ultimo comando se perderia.
            _ = Task.Run(async () =>
            {
                try
                {
                    await Console.OpenStandardInput().CopyToAsync(stream);
                    socket.Shutdown(SocketShutdown.Send);
                }
                catch
                {
                    // a sessao ja esta caindo; o CopyToAsync abaixo termina junto
                }
            });

            // Termina quando o bot fecha a conexao - por >exit, por EOF acima ou
            // porque o proprio bot saiu.
            await stream.CopyToAsync(Console.OpenStandardOutput());

            return 0;
        }

        // ---------------------------------------------------------------
        // Caminho do socket
        // ---------------------------------------------------------------

        private static string ResolveSocketPath()
        {
            var custom = Environment.GetEnvironmentVariable("CCORE_TERMINAL_SOCKET");

            if (!string.IsNullOrWhiteSpace(custom))
                return custom;

            // XDG_RUNTIME_DIR (/run/user/UID) ja e 0700 e e limpo no logout, que
            // e exatamente a vida util que um socket de controle deve ter.
            var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");

            var baseDir = string.IsNullOrWhiteSpace(runtimeDir)
                ? Path.Combine(Path.GetTempPath(), $"ccore-bot-{Environment.UserName}")
                : Path.Combine(runtimeDir, "ccore-bot");

            return Path.Combine(baseDir, "terminal.sock");
        }

        /// <summary>
        /// Garante o diretorio e remove um socket orfao de um processo anterior.
        /// Um bind por cima de arquivo existente falha com AddressInUse, mesmo
        /// que ninguem esteja escutando.
        /// </summary>
        private static string PrepareSocketPath()
        {
            var path = ResolveSocketPath();
            var dir = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);

                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            if (File.Exists(path))
            {
                if (IsSocketAlive(path))
                    throw new IOException($"ja existe um bot escutando em {path}");

                File.Delete(path);
            }

            return path;
        }

        /// <summary>
        /// Distingue "socket orfao de um processo morto" de "outro bot vivo": so
        /// o segundo aceita conexao.
        /// </summary>
        private static bool IsSocketAlive(string path)
        {
            try
            {
                using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                probe.Connect(new UnixDomainSocketEndPoint(path));
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        private static string AttachHint()
        {
            var exe = Environment.ProcessPath;

            return string.IsNullOrEmpty(exe)
                ? "dotnet run -- --terminal"
                : $"{exe} --terminal";
        }
    }
}
