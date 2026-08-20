using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DisCatSharp.ApplicationCommands.Attributes;
using DisCatSharp.ApplicationCommands.Context;
using DisCatSharp.Entities;

namespace CornwallUtilities.Services.Audit
{
    /// <summary>
    /// Sugere jogadores ja registrados na auditoria. Evita paginacao nos comandos
    /// que precisam escolher um jogador, por maior que fique o efetivo.
    /// </summary>
    internal sealed class AuditUsernameAutocompleteProvider : IAutocompleteProvider
    {
        public async Task<IEnumerable<DiscordApplicationCommandAutocompleteChoice>> Provider(AutocompleteContext ctx)
        {
            var typed = ctx.FocusedOption?.Value?.ToString() ?? string.Empty;

            try
            {
                var audit = await AuditStore.Instance.ReadAuditAsync().ConfigureAwait(false);

                return Search(audit.entries, typed, 25)
                    .Select(e => new DiscordApplicationCommandAutocompleteChoice(
                        AuditEmbeds.Trim($"{e.username} ({e.kills}/{e.deaths}/{e.assists} — {e.battles} bat.)", 100),
                        e.username))
                    .ToList();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[audit] autocomplete falhou: {ex.Message}");
                return Array.Empty<DiscordApplicationCommandAutocompleteChoice>();
            }
        }

        /// <summary>
        /// Filtra o efetivo pelo texto digitado.
        ///
        /// Extraido de dentro do autocomplete para o /audit-setranks usar o mesmo
        /// criterio: quem digita "oda" no comando e no painel de cargos espera a
        /// mesma lista de volta. Texto vazio devolve o efetivo em ordem
        /// alfabetica, cortado em <paramref name="take" />.
        /// </summary>
        public static List<AuditEntry> Search(IEnumerable<AuditEntry> entries, string? typed, int take)
        {
            var query = (typed ?? string.Empty).Trim();

            return entries
                .Where(e => !string.IsNullOrWhiteSpace(e.username))
                .Where(e => query.Length == 0 || e.username.Contains(query, StringComparison.OrdinalIgnoreCase))
                // Quem comeca com o texto digitado vem primeiro.
                .OrderByDescending(e => e.username.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                .ThenBy(e => e.username, StringComparer.OrdinalIgnoreCase)
                .Take(take)
                .ToList();
        }
    }
}
