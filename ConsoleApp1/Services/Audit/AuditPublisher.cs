using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CornwallUtilities.config;

namespace CornwallUtilities.Services.Audit
{
    /// <summary>Resultado de uma publicacao (ou da previa dela).</summary>
    internal sealed record PublishOutcome(
        bool Ok,
        bool DryRun,
        bool HasPending,
        bool NothingToDo,
        MergeReport Report,
        int TotalEntries,
        int PendingBatches,
        string? AuditSha,
        string? AuditCommitUrl,
        string? ArchiveCommitUrl,
        string? ArchivePath,
        string? FailedStep,
        string? Error)
    {
        /// <summary>Sha curto, do jeito que o GitHub mostra.</summary>
        public string ShortSha => AuditSha is null ? "-" : (AuditSha.Length <= 7 ? AuditSha : AuditSha[..7]);

        /// <summary>Frase pronta para o historico da auditoria.</summary>
        public string LogDetails => HasPending
            ? $"Consolidou {Report.BatchesApplied} lote(s) (+{Report.BattlesAdded} batalha(s), " +
              $"{Report.PlayersUpdated} jogador(es)) e publicou no GitHub — commit `{ShortSha}`."
            : $"Publicou o arquivo local no GitHub sem lotes pendentes " +
              $"({TotalEntries} jogador(es)) — commit `{ShortSha}`.";
    }

    /// <summary>
    /// Consolidacao e publicacao da auditoria no GitHub.
    ///
    /// Vive fora do comando porque o dashboard publica pela mesma porta
    /// (POST /api/audit/push). A ordem dos passos aqui NAO e arbitraria e nao
    /// deve ser reorganizada sem ler os comentarios de cada etapa: ela existe
    /// para que uma falha no meio deixe o sistema num estado do qual um novo
    /// push se recupera sozinho, sem contar batalha em dobro.
    /// </summary>
    internal static class AuditPublisher
    {
        public static async Task<PublishOutcome> PublishAsync(JSONReader config, bool dryRun)
        {
            var (audit, pending) = await AuditStore.Instance.ReadBothAsync();

            // Sem lote pendente ainda ha trabalho: /audit-import, /audit-edit e
            // /audit-setranks mexem direto no arquivo consolidado, e essas
            // mudancas so chegam ao GitHub por aqui.
            var hasPending = pending.batches.Count > 0;

            // A consolidacao acontece primeiro em memoria, sobre uma copia: nada
            // e gravado nem publicado enquanto o resultado nao estiver pronto.
            var merged = audit.Clone();
            var report = hasPending
                ? AuditMerger.MergePendingIntoAudit(merged, pending)
                : new MergeReport();

            if (dryRun)
            {
                return new PublishOutcome(true, true, hasPending, false, report,
                    merged.entries.Count, pending.batches.Count, null, null, null, null, null, null);
            }

            var step = "inicialização";
            try
            {
                step = "resolução das credenciais";
                var github = await AuditGitHubService.CreateAsync(config.audit);
                var auditPath = string.IsNullOrWhiteSpace(config.audit?.auditFilePath) ? "audit.json" : config.audit!.auditFilePath!;
                var archiveDir = string.IsNullOrWhiteSpace(config.audit?.archiveDirectory) ? "audits" : config.audit!.archiveDirectory!;

                step = "verificação da branch";
                await github.EnsureBranchExistsAsync();

                string? archivePath = null;
                string? archiveSha = null;

                if (hasPending)
                {
                    // O arquivamento vai PRIMEIRO: e o registro bruto e imutavel
                    // do lote. Se o passo seguinte falhar, o lote ja esta salvo
                    // na branch e pode ser reprocessado.
                    step = "resolução do arquivo de arquivamento";
                    archivePath = await github.ResolveArchivePathAsync(archiveDir, DateTimeOffset.UtcNow);

                    step = "publicação do arquivamento";
                    archiveSha = await github.PutFileAsync(
                        archivePath,
                        AuditStore.Serialize(pending),
                        $"audit: arquivo de lote {DateTimeOffset.UtcNow:yyyy-MM-dd} ({pending.batches.Count} lote(s), {report.PlayersUpdated} jogador(es))");
                }
                else
                {
                    // Sem lote para arquivar, publicar so faz sentido se o
                    // GitHub estiver mesmo atrasado - senao geraria um commit
                    // vazio a cada execucao.
                    step = "comparação com o GitHub";
                    var publishedJson = await github.TryGetFileContentAsync(auditPath);
                    var published = publishedJson is null ? null : AuditStore.Deserialize<AuditFile>(publishedJson);

                    if (published is not null && SameData(published, merged))
                    {
                        return new PublishOutcome(true, false, false, true, report,
                            merged.entries.Count, 0, null, null, null, null, null, null);
                    }
                }

                step = "publicação da auditoria consolidada";
                var auditSha = await github.PutFileAsync(
                    auditPath,
                    AuditStore.Serialize(merged),
                    hasPending
                        ? $"audit: consolidação {DateTimeOffset.UtcNow:yyyy-MM-dd} (+{report.BattlesAdded} batalha(s))"
                        : $"audit: sincronização do arquivo local {DateTimeOffset.UtcNow:yyyy-MM-dd} ({merged.entries.Count} jogador(es))");

                if (hasPending)
                {
                    // So depois dos dois envios: grava o consolidado localmente e
                    // limpa o pendente, na mesma operacao, para nunca ficarem
                    // divergentes.
                    step = "gravação local";
                    var consumed = pending.batches.Select(b => b.batchId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    await AuditStore.Instance.UpdateAsync((storedAudit, storedPending) =>
                    {
                        // Reaplica a consolidacao sobre o que esta gravado AGORA,
                        // em vez de sobrescrever com a copia de memoria. Entre a
                        // leitura la em cima e este ponto houve uma ida ao GitHub
                        // (segundos): um /audit-edit ou /audit-setranks que tenha
                        // rodado nesse intervalo era descartado sem aviso. O
                        // appliedBatchIds garante que reaplicar aqui nao conta
                        // batalha em dobro.
                        //
                        // Se houve mesmo alteracao concorrente, o arquivo local
                        // fica a frente do que acabou de ir para o GitHub - o
                        // proximo push (sem lote pendente) detecta a diferenca e
                        // publica, entao isso se resolve sozinho.
                        var toApply = new PendingFile
                        {
                            batches = storedPending.batches.Where(b => consumed.Contains(b.batchId)).ToList()
                        };

                        AuditMerger.MergePendingIntoAudit(storedAudit, toApply);
                        storedAudit.version = merged.version;
                        storedPending.batches.RemoveAll(b => consumed.Contains(b.batchId));
                        return true;
                    });
                }

                return new PublishOutcome(true, false, hasPending, false, report,
                    merged.entries.Count, pending.batches.Count,
                    auditSha, github.CommitUrl(auditSha),
                    archiveSha is null ? null : github.CommitUrl(archiveSha),
                    archivePath, null, null);
            }
            catch (Exception ex)
            {
                return new PublishOutcome(false, false, hasPending, false, report,
                    merged.entries.Count, pending.batches.Count,
                    null, null, null, null, step, AuditGitHubService.DescribeError(ex));
            }
        }

        /// <summary>
        /// Compara os dados de duas versoes do arquivo consolidado ignorando
        /// lastUpdatedUtc: esse carimbo muda a cada gravacao local e sozinho nao
        /// justifica um commit.
        /// </summary>
        private static bool SameData(AuditFile a, AuditFile b)
        {
            var left = a.Clone();
            var right = b.Clone();
            left.lastUpdatedUtc = default;
            right.lastUpdatedUtc = default;

            return string.Equals(AuditStore.Serialize(left), AuditStore.Serialize(right), StringComparison.Ordinal);
        }
    }
}
