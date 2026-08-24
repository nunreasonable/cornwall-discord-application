using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DisCatSharp.Entities;

namespace CornwallUtilities.Services
{
    /// <summary>Resultado consolidado de um envio em massa.</summary>
    internal sealed record DmSendResult(int Total, int Sent, int Failed, List<string> FailureSamples);

    /// <summary>
    /// Lancada quando o circuit breaker interrompe um envio: falhas demais
    /// seguidas ou um sinal de rate limit do Discord (429 / 40003 "opening
    /// direct messages too fast"). Continuar martelando os destinatarios
    /// restantes so agrava o rate limit e e o padrao que faz bots serem
    /// sinalizados por spam de DM. Carrega o parcial ate o ponto do corte.
    /// </summary>
    internal sealed class MassDmAbortedException : Exception
    {
        public MassDmAbortedException(string message, DmSendResult partial) : base(message)
        {
            Partial = partial;
        }

        public DmSendResult Partial { get; }
    }

    /// <summary>Retrato de um job, seguro para serializar na API.</summary>
    internal sealed record DmJobSnapshot(
        string jobId,
        string state,
        string targetName,
        int total,
        int sent,
        int failed,
        DateTimeOffset startedAtUtc,
        DateTimeOffset? finishedAtUtc,
        string[] failureSamples,
        string? error);

    /// <summary>
    /// Envio de DM em massa, compartilhado por /dmreminder, /dmdeployment e o
    /// dashboard.
    ///
    /// Pelo dashboard o envio PRECISA rodar como job em segundo plano: cada
    /// destinatario custa 3s de DmRateLimiter mais 1,2s de espaçamento, entao
    /// os 500 do teto levam cerca de 35 minutos. Nenhum request HTTP fica de pe
    /// tanto tempo - o cliente receberia timeout enquanto o envio seguisse
    /// rodando as cegas, sem ninguem para ler o resultado.
    /// </summary>
    internal static class MassDmService
    {
        /// <summary>Teto por execucao, para nao tomar rate limit nem timeout.</summary>
        public const int MaxMembers = 500;

        /// <summary>
        /// Quantos jobs podem estar enviando ao mesmo tempo. /api/dm permite 3
        /// disparos/minuto e cada um vai a 500 pessoas; sem este teto, milhares de
        /// DMs se enfileiravam atras do DmRateLimiter em minutos, com horas de
        /// execucao e sem forma de parar.
        /// </summary>
        public const int MaxConcurrentJobs = 2;

        /// <summary>
        /// Falhas seguidas que derrubam o job. Uma DM recusada (403) e normal e
        /// nao conta como sinal de rate limit, mas uma sequencia longa de falhas
        /// indica que algo sistemico quebrou (token, permissao, rede) e insistir
        /// nos 500 restantes so queima cota.
        /// </summary>
        private const int MaxConsecutiveFailures = 12;

        private static readonly ConcurrentDictionary<string, DmJob> s_jobs = new();

        /// <summary>Reconhece 429 e o 40003 ("opening direct messages too fast").</summary>
        private static bool IsRateLimitSignal(Exception ex)
        {
            var msg = ex.Message ?? "";
            return msg.Contains("429")
                || msg.Contains("40003")
                || msg.Contains("opening direct messages too fast", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("rate limit", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Por quanto tempo um job concluido continua consultavel.</summary>
        private static readonly TimeSpan JobRetention = TimeSpan.FromHours(1);

        /// <summary>Mensagem curta em PT-BR para falha de DM (ex.: 403 = usuário não aceita DMs).</summary>
        public static string DmFailureReason(Exception ex)
        {
            var msg = ex.Message ?? "";
            if (msg.Contains("403") || msg.Contains("50007") || msg.Contains("Cannot send messages", StringComparison.OrdinalIgnoreCase))
                return "DMs desativadas ou bot bloqueado";
            return msg.Length > 80 ? msg[..77] + "..." : msg;
        }

        /// <summary>
        /// Resolve quem vai receber. Devolve a mensagem de erro pronta quando o
        /// alvo e invalido, vazio ou grande demais.
        /// </summary>
        public static async Task<(List<DiscordMember>? Members, string TargetName, string? Error)> ResolveRecipientsAsync(
            DiscordGuild guild, DiscordRole? role, DiscordUser? user)
        {
            if (role is null && user is null)
                return (null, string.Empty, "Você deve fornecer **um cargo** ou **um usuário** para enviar a mensagem.");

            if (user is not null)
            {
                DiscordMember member;
                try
                {
                    member = await guild.GetMemberAsync(user.Id);
                }
                catch
                {
                    return (null, string.Empty, "O usuário fornecido não é um membro deste servidor.");
                }

                return (new List<DiscordMember> { member }, member.DisplayName ?? member.Username, null);
            }

            // GetAllMembersAsync busca todos os membros na API do Discord; o
            // cache (Members) só tem uma parte.
            var allMembers = await guild.GetAllMembersAsync();
            var members = allMembers
                .Where(m => role is not null && m.Roles.Contains(role) && !m.IsBot)
                .ToList();

            var targetName = role?.Name ?? "destinatários";

            if (members.Count == 0)
                return (null, targetName, $"Não foi encontrado nenhum membro com o cargo **{targetName}** (excluindo bots).");

            if (members.Count > MaxMembers)
                return (null, targetName, $"O cargo **{targetName}** possui {members.Count} membros. Para evitar timeouts ou rate limits, limite o envio a {MaxMembers} membros por execução.");

            return (members, targetName, null);
        }

        /// <summary>
        /// Percorre os destinatarios respeitando o limitador global de DM.
        /// <paramref name="onProgress"/> e chamado depois de cada tentativa.
        /// </summary>
        public static async Task<DmSendResult> SendAsync(
            IReadOnlyList<DiscordMember> members,
            string? content,
            DiscordEmbed embed,
            Action<int, int>? onProgress = null,
            CancellationToken ct = default)
        {
            var sent = 0;
            var failed = 0;
            var failedUsers = new List<string>();
            var index = 0;
            var consecutiveFailures = 0;

            foreach (var member in members)
            {
                ct.ThrowIfCancellationRequested();

                await DmRateLimiter.WaitForSlotAsync();

                try
                {
                    var dmChannel = await member.CreateDmChannelAsync();
                    var builder = new DiscordMessageBuilder().AddEmbed(embed);
                    if (!string.IsNullOrEmpty(content))
                        builder.WithContent(content);

                    await dmChannel.SendMessageAsync(builder);
                    sent++;
                    consecutiveFailures = 0;
                }
                catch (Exception ex)
                {
                    failed++;
                    if (failedUsers.Count < 25)
                        failedUsers.Add($"{member.DisplayName ?? member.Username} ({DmFailureReason(ex)})");

                    // Um sinal de rate limit derruba na hora: continuar so piora o
                    // 429 e e o que faz o Discord marcar o bot como spam. Falhas
                    // comuns (DM fechada) so derrubam quando se acumulam em serie.
                    if (IsRateLimitSignal(ex))
                    {
                        var partial = new DmSendResult(members.Count, sent, failed, failedUsers);
                        onProgress?.Invoke(sent, failed);
                        throw new MassDmAbortedException(
                            $"Interrompido por sinal de rate limit do Discord após {sent} envio(s): {DmFailureReason(ex)}.",
                            partial);
                    }

                    if (++consecutiveFailures >= MaxConsecutiveFailures)
                    {
                        var partial = new DmSendResult(members.Count, sent, failed, failedUsers);
                        onProgress?.Invoke(sent, failed);
                        throw new MassDmAbortedException(
                            $"Interrompido após {consecutiveFailures} falhas seguidas ({sent} envio(s) concluídos).",
                            partial);
                    }
                }

                onProgress?.Invoke(sent, failed);

                // Espaca os envios para nao estourar em rajada sob o limite de
                // 10/3min. So ENTRE envios: dormir depois do ultimo destinatario
                // atrasava o resumo final em 1,2s sem espacar coisa nenhuma.
                if (++index < members.Count)
                    await Task.Delay(1200, ct);
            }

            return new DmSendResult(members.Count, sent, failed, failedUsers);
        }

        /// <summary>Frase de ajuda quando ninguem, ou quase ninguem, recebeu.</summary>
        public static string? DescribeFailures(DmSendResult result)
        {
            if (result.Failed == 0)
                return null;

            var sample = string.Join(", ", result.FailureSamples.Take(10));
            var hint = result.Failed == result.Total
                ? " Ninguém recebeu: verifique se os destinatários permitem DMs de membros do servidor (Configurações do usuário > Privacidade)."
                : "";

            return $"Falha ao enviar para {result.Failed} usuário(s). Exemplo: {sample}{(result.Failed > 10 ? "..." : "")}.{hint}";
        }

        /// <summary>
        /// Dispara o envio em segundo plano e devolve o job para acompanhamento.
        /// Os destinatarios ja vem resolvidos, entao um alvo invalido falha na
        /// hora do request em vez de virar um job que so quebra depois.
        /// </summary>
        /// <summary>
        /// Dispara o job. Devolve <c>error</c> preenchido (e <c>job</c> nulo)
        /// quando ja ha jobs demais em andamento, para o chamador recusar o
        /// request em vez de empilhar mais horas de envio.
        /// </summary>
        public static (DmJob? Job, string? Error) StartJob(IReadOnlyList<DiscordMember> members, string targetName, string? content, DiscordEmbed embed)
        {
            PruneJobs();

            var active = s_jobs.Values.Count(j => j.Snapshot().state == "enviando");
            if (active >= MaxConcurrentJobs)
                return (null, $"Já há {active} envio(s) em andamento (limite {MaxConcurrentJobs}). Aguarde ou cancele antes de iniciar outro.");

            var job = new DmJob(Guid.NewGuid().ToString("N"), targetName, members.Count);
            s_jobs[job.JobId] = job;

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await SendAsync(members, content, embed, job.ReportProgress, job.CancellationToken);
                    job.Complete(result);
                }
                catch (MassDmAbortedException ex)
                {
                    Console.WriteLine($"[dm] job {job.JobId} abortado: {ex.Message}");
                    job.Abort(ex.Message, ex.Partial);
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine($"[dm] job {job.JobId} cancelado pelo operador.");
                    job.MarkCancelled();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[dm] job {job.JobId} falhou: {ex}");
                    job.Fail(ex.Message);
                }
            });

            return (job, null);
        }

        /// <summary>
        /// Pede o cancelamento de um job em andamento. Devolve false quando o job
        /// nao existe ou ja terminou.
        /// </summary>
        public static bool CancelJob(string? jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId) || !s_jobs.TryGetValue(jobId, out var job))
                return false;

            return job.RequestCancel();
        }

        public static DmJobSnapshot? GetJob(string? jobId)
        {
            PruneJobs();

            if (string.IsNullOrWhiteSpace(jobId) || !s_jobs.TryGetValue(jobId, out var job))
                return null;

            return job.Snapshot();
        }

        /// <summary>Jobs em andamento ou concluidos ha pouco, do mais novo ao mais velho.</summary>
        public static List<DmJobSnapshot> RecentJobs()
        {
            PruneJobs();

            return s_jobs.Values
                .Select(j => j.Snapshot())
                .OrderByDescending(j => j.startedAtUtc)
                .Take(10)
                .ToList();
        }

        private static void PruneJobs()
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var kv in s_jobs)
            {
                var finished = kv.Value.Snapshot().finishedAtUtc;
                if (finished.HasValue && now - finished.Value >= JobRetention)
                    s_jobs.TryRemove(kv.Key, out _);
            }
        }
    }

    /// <summary>
    /// Estado mutavel de um envio. Escrito pela task do job e lido pelas threads
    /// do HttpListener, entao todo acesso passa pelo mesmo lock.
    /// </summary>
    internal sealed class DmJob
    {
        private readonly object _lock = new();
        private readonly CancellationTokenSource _cts = new();

        private string _state = "enviando";
        private int _sent;
        private int _failed;
        private DateTimeOffset? _finishedAtUtc;
        private string[] _failureSamples = Array.Empty<string>();
        private string? _error;

        public DmJob(string jobId, string targetName, int total)
        {
            JobId = jobId;
            TargetName = targetName;
            Total = total;
            StartedAtUtc = DateTimeOffset.UtcNow;
        }

        public string JobId { get; }
        public string TargetName { get; }
        public int Total { get; }
        public DateTimeOffset StartedAtUtc { get; }

        /// <summary>Token que o laco de envio observa entre destinatarios.</summary>
        public CancellationToken CancellationToken => _cts.Token;

        public void ReportProgress(int sent, int failed)
        {
            lock (_lock)
            {
                _sent = sent;
                _failed = failed;
            }
        }

        public void Complete(DmSendResult result)
        {
            lock (_lock)
            {
                _sent = result.Sent;
                _failed = result.Failed;
                _failureSamples = result.FailureSamples.ToArray();
                _state = "concluido";
                _finishedAtUtc = DateTimeOffset.UtcNow;
            }
            _cts.Dispose();
        }

        public void Fail(string error)
        {
            lock (_lock)
            {
                _state = "falhou";
                _error = error;
                _finishedAtUtc = DateTimeOffset.UtcNow;
            }
            _cts.Dispose();
        }

        /// <summary>Encerramento por circuit breaker, preservando o parcial enviado.</summary>
        public void Abort(string reason, DmSendResult partial)
        {
            lock (_lock)
            {
                _sent = partial.Sent;
                _failed = partial.Failed;
                _failureSamples = partial.FailureSamples.ToArray();
                _state = "abortado";
                _error = reason;
                _finishedAtUtc = DateTimeOffset.UtcNow;
            }
            _cts.Dispose();
        }

        public void MarkCancelled()
        {
            lock (_lock)
            {
                _state = "cancelado";
                _error ??= "Cancelado pelo operador.";
                _finishedAtUtc = DateTimeOffset.UtcNow;
            }
            _cts.Dispose();
        }

        /// <summary>
        /// Sinaliza o token. Devolve false se o job ja terminou (nada a cancelar).
        /// </summary>
        public bool RequestCancel()
        {
            lock (_lock)
            {
                if (_finishedAtUtc.HasValue)
                    return false;
            }

            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // O job terminou entre a checagem e o Cancel; nada a fazer.
                return false;
            }

            return true;
        }

        public DmJobSnapshot Snapshot()
        {
            lock (_lock)
            {
                return new DmJobSnapshot(JobId, _state, TargetName, Total, _sent, _failed,
                    StartedAtUtc, _finishedAtUtc, _failureSamples, _error);
            }
        }
    }
}
