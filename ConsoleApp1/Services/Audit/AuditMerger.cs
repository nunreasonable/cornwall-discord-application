using System;
using System.Collections.Generic;
using System.Linq;

namespace CornwallUtilities.Services.Audit
{
    internal sealed class MergeReport
    {
        public int BatchesApplied { get; set; }
        public int BatchesSkipped { get; set; }
        public int PlayersUpdated { get; set; }
        public int BattlesAdded { get; set; }
        public List<string> NewPlayers { get; } = new();
    }

    internal sealed class ImportReport
    {
        public List<string> Added { get; } = new();
        public List<string> Conflicts { get; } = new();
        public int SkippedRows { get; set; }
        public int Overwritten { get; set; }
    }

    internal static class AuditMerger
    {
        private const int MaxTrackedBatchIds = 500;

        /// <summary>
        /// Incorpora os lotes pendentes no arquivo consolidado. Cada lote da +1
        /// batalha a cada jogador que aparece nele.
        /// </summary>
        public static MergeReport MergePendingIntoAudit(AuditFile audit, PendingFile pending)
        {
            var report = new MergeReport();
            var index = BuildIndex(audit);
            var applied = new HashSet<string>(audit.appliedBatchIds, StringComparer.OrdinalIgnoreCase);
            var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var batch in pending.batches.OrderBy(b => b.createdUtc))
            {
                // Se o processo caiu entre o push e a limpeza do pendente, o lote
                // ja pode estar contabilizado. Sem isso, um /audit-push repetido
                // contaria em dobro.
                if (!string.IsNullOrEmpty(batch.batchId) && applied.Contains(batch.batchId))
                {
                    report.BatchesSkipped++;
                    continue;
                }

                // Um lote e uma batalha: se o mesmo nome aparecer duas vezes
                // dentro dele, o K/D/A soma mas a batalha continua sendo uma so.
                // O /audit-add ja funde as linhas repetidas na leitura, mas
                // nada garantia isso aqui - e um lote editado pelo painel ou
                // vindo de um arquivamento antigo entrava contando em dobro.
                var countedInBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var entry in batch.entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.username))
                        continue;

                    var firstInBatch = countedInBatch.Add(entry.username);

                    if (index.TryGetValue(entry.username, out var target))
                    {
                        target.kills += entry.kills;
                        target.deaths += entry.deaths;
                        target.assists += entry.assists;
                        if (firstInBatch)
                            target.battles += 1;
                    }
                    else
                    {
                        target = new AuditEntry
                        {
                            username = entry.username,
                            kills = entry.kills,
                            deaths = entry.deaths,
                            assists = entry.assists,
                            battles = 1,
                            rank = string.Empty
                        };
                        audit.entries.Add(target);
                        index[target.username] = target;
                        report.NewPlayers.Add(target.username);
                    }

                    touched.Add(target.username);
                    if (firstInBatch)
                        report.BattlesAdded++;
                }

                report.BatchesApplied++;
                if (!string.IsNullOrEmpty(batch.batchId))
                {
                    audit.appliedBatchIds.Add(batch.batchId);
                    applied.Add(batch.batchId);
                }
            }

            if (audit.appliedBatchIds.Count > MaxTrackedBatchIds)
                audit.appliedBatchIds.RemoveRange(0, audit.appliedBatchIds.Count - MaxTrackedBatchIds);

            report.PlayersUpdated = touched.Count;
            return report;
        }

        /// <summary>
        /// Semeia o arquivo a partir da planilha. Nao destrutivo por padrao.
        ///
        /// Depois da primeira importacao o audit.json passa a ser a fonte da
        /// verdade: seus K/D/A ja sao totais acumulados que incluem a planilha
        /// MAIS tudo que foi consolidado depois. Sobrescrever a partir da
        /// planilha faria os totais andarem para tras em silencio.
        ///
        /// `includeKda` diz se a planilha tem as colunas de kills/deaths/assists.
        /// Quando nao tem, esses campos ficam intocados: compara-los apontaria
        /// conflito em todo mundo (0 da planilha contra o total real) e
        /// sobrescreve-los zeraria o historico de quem ja esta registrado.
        /// </summary>
        public static ImportReport MergeImport(AuditFile audit, IEnumerable<AuditEntry> imported, bool overwrite, bool includeKda = true)
        {
            var report = new ImportReport();
            var index = BuildIndex(audit);

            foreach (var row in imported)
            {
                if (string.IsNullOrWhiteSpace(row.username))
                {
                    report.SkippedRows++;
                    continue;
                }

                if (index.TryGetValue(row.username, out var existing))
                {
                    if (overwrite)
                    {
                        if (includeKda)
                        {
                            existing.kills = row.kills;
                            existing.deaths = row.deaths;
                            existing.assists = row.assists;
                        }

                        existing.battles = row.battles;
                        if (!string.IsNullOrWhiteSpace(row.rank))
                            existing.rank = row.rank;
                        report.Overwritten++;
                    }
                    else if (existing.battles != row.battles ||
                             (includeKda && (existing.kills != row.kills || existing.deaths != row.deaths ||
                                             existing.assists != row.assists)))
                    {
                        report.Conflicts.Add(includeKda
                            ? $"{existing.username}: planilha {row.kills}/{row.deaths}/{row.assists} ({row.battles} bat.) " +
                              $"vs armazenado {existing.kills}/{existing.deaths}/{existing.assists} ({existing.battles} bat.)"
                            : $"{existing.username}: planilha {row.battles} bat. vs armazenado {existing.battles} bat.");
                    }

                    continue;
                }

                // Preserva batalhas e cargo vindos da planilha: sao justamente o
                // historico que nao pode ser perdido na migracao.
                var entry = new AuditEntry
                {
                    username = row.username,
                    kills = row.kills,
                    deaths = row.deaths,
                    assists = row.assists,
                    battles = row.battles,
                    rank = row.rank
                };

                audit.entries.Add(entry);
                index[entry.username] = entry;
                report.Added.Add(entry.username);
            }

            return report;
        }

        public static Dictionary<string, AuditEntry> BuildIndex(AuditFile audit)
        {
            var index = new Dictionary<string, AuditEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in audit.entries)
            {
                if (!string.IsNullOrWhiteSpace(entry.username))
                    index[entry.username] = entry;
            }

            return index;
        }
    }
}
