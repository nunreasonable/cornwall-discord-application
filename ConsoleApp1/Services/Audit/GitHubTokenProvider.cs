using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CornwallUtilities.config;

namespace CornwallUtilities.Services.Audit
{
    /// <summary>
    /// Descobre o token do GitHub usado para publicar a auditoria.
    ///
    /// A ideia e nao exigir um PAT novo: a maquina onde o bot roda ja esta
    /// autenticada no `gh` CLI, entao o token sai de la. As outras fontes ficam
    /// como saida para um deploy onde o `gh` nao esteja logado.
    ///
    /// O valor do token NUNCA e escrito em log - so a fonte de onde ele veio.
    /// </summary>
    internal static class GitHubTokenProvider
    {
        private static readonly SemaphoreSlim s_lock = new(1, 1);
        private static string? s_cached;

        /// <summary>
        /// Esquece o token em cache. Chamado quando o GitHub responde 401, para
        /// que a proxima tentativa releia um token rotacionado em vez de ficar
        /// presa para sempre num valor vencido.
        /// </summary>
        public static void Invalidate()
        {
            // Sem tomar o semaforo: esta chamada nasce do tratador de erro de um
            // comando, e o dono do semaforo pode estar exatamente dentro do
            // `gh auth token` (ate 10s). Esperar ali bloquearia uma thread do
            // ThreadPool - o mesmo tipo de bloqueio que este codebase evita em
            // todo lugar por causa do heartbeat do gateway. Escrever null e
            // atomico, e no pior caso uma resolucao concorrente que ja estava em
            // andamento regrava o token vencido; a proxima falha invalida de
            // novo, que e o comportamento que ja existia.
            Volatile.Write(ref s_cached, null);
        }

        public static async Task<string> ResolveAsync(AuditConfig? cfg)
        {
            await s_lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var cached = Volatile.Read(ref s_cached);
                if (!string.IsNullOrWhiteSpace(cached))
                    return cached;

                // 1) Override explicito no config.jsonc.
                if (!string.IsNullOrWhiteSpace(cfg?.githubToken))
                {
                    Console.WriteLine("[audit] token do GitHub obtido do config.jsonc.");
                    s_cached = cfg!.githubToken!.Trim();
                    return s_cached;
                }

                // 2) Variaveis de ambiente (convencao de CI e de servicos).
                foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN" })
                {
                    var fromEnv = Environment.GetEnvironmentVariable(name);
                    if (!string.IsNullOrWhiteSpace(fromEnv))
                    {
                        Console.WriteLine($"[audit] token do GitHub obtido da variavel {name}.");
                        s_cached = fromEnv.Trim();
                        return s_cached;
                    }
                }

                // 3) Login que ja existe no `gh` CLI - o caminho normal aqui.
                var fromCli = await TryReadFromGhCliAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(fromCli))
                {
                    Console.WriteLine("[audit] token do GitHub obtido via gh CLI.");
                    s_cached = fromCli;
                    return s_cached;
                }

                throw new AuditConfigException(
                    "Nenhuma credencial do GitHub disponível. Resolva de uma destas formas:\n" +
                    "• rode `gh auth login` na máquina do bot (recomendado, não precisa de token novo);\n" +
                    "• defina a variável de ambiente `GH_TOKEN`;\n" +
                    "• preencha `audit.githubToken` no config.jsonc.");
            }
            finally
            {
                s_lock.Release();
            }
        }

        private static async Task<string?> TryReadFromGhCliAsync()
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "gh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("auth");
            startInfo.ArgumentList.Add("token");

            try
            {
                using var process = Process.Start(startInfo);
                if (process is null)
                    return null;

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    TryKill(process);
                    Console.WriteLine("[audit] `gh auth token` demorou demais e foi encerrado.");
                    return null;
                }

                var stdout = await stdoutTask.ConfigureAwait(false);
                var stderr = await stderrTask.ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    // Apenas o stderr - a saida padrao conteria o token.
                    Console.WriteLine($"[audit] `gh auth token` falhou (código {process.ExitCode}): {stderr.Trim()}");
                    return null;
                }

                var token = stdout.Trim();
                return token.Length == 0 ? null : token;
            }
            catch (Exception ex)
            {
                // gh ausente do PATH, sem permissao de execucao, etc.
                Console.WriteLine($"[audit] não foi possível executar o gh CLI: {ex.Message}");
                return null;
            }
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // encerramento best-effort
            }
        }
    }
}
