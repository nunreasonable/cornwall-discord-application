using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
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

            var csv = await DownloadAsync(cfg.auditCsvUrl!, ct).ConfigureAwait(false);

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

        /// <summary>
        /// Baixa o CSV com algumas tentativas: a exportacao do Google as vezes
        /// engasga (redirect para googleusercontent + planilha grande) e uma
        /// falha isolada nao deveria derrubar a importacao inteira.
        ///
        /// Usa LongRunning, e nao Shared: o teto de 20s do cliente compartilhado
        /// ignorava o prazo que o comando pedia e abortava a importacao.
        /// </summary>
        private static async Task<string> DownloadAsync(string url, CancellationToken ct)
        {
            const int maxAttempts = 3;
            Exception? lastError = null;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    return await HttpClientProvider.LongRunning.GetStringAsync(url, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    // Cancelamento vindo do chamador (prazo do comando esgotado)
                    // nao e transitorio: nao adianta insistir.
                    ct.ThrowIfCancellationRequested();

                    lastError = ex;

                    if (attempt < maxAttempts)
                        await Task.Delay(500 * attempt, ct).ConfigureAwait(false);
                }
            }

            throw lastError!;
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
