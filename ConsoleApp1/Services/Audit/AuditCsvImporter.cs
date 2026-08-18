using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CornwallUtilities.config;

namespace CornwallUtilities.Services.Audit
{
    internal sealed class CsvImportResult
    {
        public List<AuditEntry> Rows { get; } = new();
        public int SkippedRows { get; set; }
    }

    /// <summary>
    /// Le a auditoria atual da planilha do Google (export CSV) para semear o
    /// arquivo local. A aba Roster ja traz nome, cargo, batalhas e K/D/A, entao a
    /// importacao preserva o historico inteiro que o regimento acumulou ate aqui.
    /// </summary>
    internal static class AuditCsvImporter
    {
        public static async Task<CsvImportResult> FetchAsync(AuditConfig cfg, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(cfg.auditCsvUrl) || cfg.auditCsvUrl!.Contains("GID_AQUI"))
                throw new AuditConfigException("`audit.auditCsvUrl` não está configurado (o gid da aba ainda é um placeholder).");

            var csv = await HttpClientProvider.Shared.GetStringAsync(cfg.auditCsvUrl, ct).ConfigureAwait(false);

            var columns = cfg.csvColumns ?? new AuditCsvColumns();
            var headerRows = Math.Max(0, cfg.csvHeaderRows);

            var result = new CsvImportResult();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // ParseCsv (e nao uma quebra por linha) porque a planilha tem campos
            // entre aspas com quebra de linha dentro.
            var records = CsvUtil.ParseCsv(csv).Skip(headerRows);

            foreach (var cells in records)
            {
                var username = (cells.ElementAtOrDefault(columns.username) ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(username))
                {
                    result.SkippedRows++;
                    continue;
                }

                // A planilha pode repetir o mesmo jogador em linhas diferentes;
                // a primeira ocorrencia vence.
                if (!seen.Add(username))
                {
                    result.SkippedRows++;
                    continue;
                }

                result.Rows.Add(new AuditEntry
                {
                    username = username,
                    kills = ReadInt(cells, columns.kills),
                    deaths = ReadInt(cells, columns.deaths),
                    assists = ReadInt(cells, columns.assists),
                    battles = ReadInt(cells, columns.battles),
                    rank = (cells.ElementAtOrDefault(columns.rank) ?? string.Empty).Trim()
                });
            }

            return result;
        }

        private static int ReadInt(List<string> cells, int index)
        {
            var raw = (cells.ElementAtOrDefault(index) ?? string.Empty).Trim();
            if (raw.Length == 0)
                return 0;

            // Tolera separadores de milhar e espacos vindos da planilha.
            raw = raw.Replace(".", string.Empty).Replace(",", string.Empty).Replace(" ", string.Empty);

            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0
                ? value
                : 0;
        }
    }
}
