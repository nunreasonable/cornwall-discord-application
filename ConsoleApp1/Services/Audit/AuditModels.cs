using System;
using System.Collections.Generic;

namespace CornwallUtilities.Services.Audit
{
    /// <summary>Estatisticas acumuladas de um jogador.</summary>
    internal sealed class AuditEntry
    {
        public string username { get; set; } = string.Empty;
        public int kills { get; set; }
        public int deaths { get; set; }
        public int assists { get; set; }
        public int battles { get; set; }
        public string rank { get; set; } = string.Empty;

        public AuditEntry Clone() => new()
        {
            username = username,
            kills = kills,
            deaths = deaths,
            assists = assists,
            battles = battles,
            rank = rank
        };

        public string KdRatio => deaths == 0
            ? kills.ToString()
            : ((double)kills / deaths).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Arquivo consolidado - a fonte da verdade da auditoria.</summary>
    internal sealed class AuditFile
    {
        public int version { get; set; } = 1;
        public DateTimeOffset lastUpdatedUtc { get; set; }

        /// <summary>
        /// Ids dos lotes ja incorporados. Garante que um /audit-push repetido
        /// (por exemplo apos uma falha no meio do caminho) nao conte em dobro.
        /// </summary>
        public List<string> appliedBatchIds { get; set; } = new();

        public List<AuditEntry> entries { get; set; } = new();

        public AuditFile Clone() => new()
        {
            version = version,
            lastUpdatedUtc = lastUpdatedUtc,
            appliedBatchIds = new List<string>(appliedBatchIds),
            entries = entries.ConvertAll(e => e.Clone())
        };
    }

    /// <summary>
    /// Um /audit-add = um lote. A regra e "+1 batalha por jogador por lote", entao
    /// os lotes precisam continuar separados ate a consolidacao: um jogador que
    /// aparece em tres lotes tem de ganhar +3 batalhas.
    /// </summary>
    internal sealed class PendingBatch
    {
        public string batchId { get; set; } = string.Empty;
        public DateTimeOffset createdUtc { get; set; }
        public ulong submittedByUserId { get; set; }
        public string submittedByUsername { get; set; } = string.Empty;
        public List<AuditEntry> entries { get; set; } = new();
        public List<string> rawLines { get; set; } = new();
    }

    internal sealed class PendingFile
    {
        public List<PendingBatch> batches { get; set; } = new();
    }
}
