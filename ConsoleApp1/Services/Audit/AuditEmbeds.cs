using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DisCatSharp.Entities;

namespace CornwallUtilities.Services.Audit
{
    internal static class AuditEmbeds
    {
        public static DiscordEmbed Error(string title, string description) =>
            new DiscordEmbedBuilder()
                .WithTitle(title)
                .WithDescription(description)
                .WithColor(DiscordColor.IndianRed)
                .Build();

        public static DiscordEmbed Denied(string description) =>
            Error("Permissão negada", description);

        /// <summary>Uma linha do relatorio: "Nome K D A Batalhas Cargo".</summary>
        public static string FormatEntry(AuditEntry e) =>
            $"{Trim(e.username, 18).PadRight(18)} {e.kills,-5} {e.deaths,-5} {e.assists,-5} {e.battles,-5} {Trim(string.IsNullOrWhiteSpace(e.rank) ? "-" : e.rank, 18)}";

        public static string Header() =>
            $"{"Nome".PadRight(18)} {"K",-5} {"D",-5} {"A",-5} {"Bat",-5} Cargo";

        /// <summary>
        /// Monta as paginas do relatorio.
        ///
        /// O conteudo vai num bloco de codigo na DESCRICAO, nao em fields: assim o
        /// limite de 25 fields fica inalcancavel por construcao e as colunas ficam
        /// alinhadas. Cada pagina para em ~3500 caracteres, bem dentro dos limites
        /// de 4096 (descricao) e 6000 (total) do Discord.
        /// </summary>
        public static List<DiscordEmbedBuilder> BuildAuditPages(AuditFile audit, IEnumerable<AuditEntry> ordered, string subtitle, int maxCharsPerPage = 3500)
        {
            var pages = new List<DiscordEmbedBuilder>();
            var rows = ordered.ToList();

            var totalBattles = audit.entries.Sum(e => (long)e.battles);
            var totalKills = audit.entries.Sum(e => (long)e.kills);

            if (rows.Count == 0)
            {
                pages.Add(new DiscordEmbedBuilder()
                    .WithTitle("Auditoria")
                    .WithDescription("Nenhum jogador registrado ainda. Use `/audit-add` para começar.")
                    .WithColor(DiscordColor.Orange));
                return pages;
            }

            var header = Header();
            var sb = new StringBuilder();
            var chunks = new List<string>();

            foreach (var row in rows)
            {
                var line = FormatEntry(row);
                if (sb.Length + line.Length + 1 > maxCharsPerPage)
                {
                    chunks.Add(sb.ToString());
                    sb.Clear();
                }

                sb.Append(line).Append('\n');
            }

            if (sb.Length > 0)
                chunks.Add(sb.ToString());

            for (var i = 0; i < chunks.Count; i++)
            {
                pages.Add(new DiscordEmbedBuilder()
                    .WithTitle("Auditoria do regimento")
                    .WithDescription($"{subtitle}\n```\n{header}\n{chunks[i]}```")
                    .WithColor(DiscordColor.Blurple)
                    .WithFooter($"Página {i + 1}/{chunks.Count} — {audit.entries.Count} jogadores, {totalBattles} batalhas, {totalKills} kills")
                    .WithTimestamp(audit.lastUpdatedUtc == default ? DateTimeOffset.UtcNow : audit.lastUpdatedUtc));
            }

            return pages;
        }

        public static string Trim(string? value, int max)
        {
            value ??= string.Empty;
            return value.Length <= max ? value : value.Substring(0, Math.Max(0, max - 1)) + "…";
        }

        /// <summary>Corta um valor para caber num field de embed (1024 caracteres).</summary>
        public static string FieldValue(IEnumerable<string> items, string emptyText = "—")
        {
            var text = string.Join("\n", items);
            if (string.IsNullOrWhiteSpace(text))
                return emptyText;

            return text.Length <= 1024 ? text : text.Substring(0, 1000) + "\n… (truncado)";
        }
    }
}
