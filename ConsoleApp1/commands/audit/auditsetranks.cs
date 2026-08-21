using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CornwallUtilities.config;
using CornwallUtilities.Services.Audit;
using DisCatSharp.ApplicationCommands;
using DisCatSharp.ApplicationCommands.Attributes;
using DisCatSharp.ApplicationCommands.Context;
using DisCatSharp.Entities;
using DisCatSharp.Enums;
using DisCatSharp.EventArgs;
using DisCatSharp.Interactivity.Extensions;

namespace CornwallUtilities.commands
{
    internal class AuditSetRanks : ApplicationCommandsModule
    {
        /// <summary>Teto de opcoes de um select da Discord.</summary>
        private const int SelectSize = 25;

        /// <summary>
        /// Quantos jogadores cabem numa leva.
        ///
        /// Antes eram 5, e o teto vinha do modal: um campo por jogador, e o modal
        /// da Discord aceita no maximo cinco componentes - o
        /// DiscordInteractionModalBuilder lanca ArgumentException acima disso.
        /// Com o bloco "nome = cargo" num campo unico de paragrafo, o modal deixa
        /// de ser o limite e o numero passa a ser so uma escolha de usabilidade.
        /// </summary>
        private const int MaxPerBatch = 10;

        [SlashCommand("audit-setranks", "Busca jogadores e define o cargo de até 10 de uma vez")]
        public async Task AuditSetRanksCommand(InteractionContext ctx)
        {
            await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource,
                new DiscordInteractionResponseBuilder().AsEphemeral());

            var config = new JSONReader();
            await config.ReadJSON();

            var denied = AuditPermissions.CheckStaff(ctx, config);
            if (denied is not null)
            {
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(denied));
                return;
            }

            var audit = await AuditStore.Instance.ReadAuditAsync();

            if (audit.entries.Count == 0)
            {
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(new DiscordEmbedBuilder()
                    .WithTitle("Auditoria vazia")
                    .WithDescription("Nenhum jogador registrado ainda. Use `/audit-add` primeiro.")
                    .WithColor(DiscordColor.Orange)));
                return;
            }

            var sessionId = Guid.NewGuid().ToString("N")[..8];
            var roster = audit.entries.OrderBy(e => e.username, StringComparer.OrdinalIgnoreCase).ToList();

            // Estado da sessao vive nas variaveis locais deste metodo: nada de
            // dicionario global, nada para vazar.
            var query = string.Empty;
            var selection = new List<string>();
            var changed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? notice = null;

            await ctx.EditResponseAsync(BuildView(roster, query, selection, changed, sessionId, notice));
            var message = await ctx.GetOriginalResponseAsync();

            var interactivity = ctx.Client.GetInteractivity();

            /*
             * Submissao de modal chega por ComponentInteractionCreated tambem: o
             * despachante monta o MESMO ComponentInteractionCreateEventArgs para
             * Component e para ModalSubmit, e nesse segundo caso o Message vem
             * nulo - e por isso que o handler global do Program.cs ja abre com
             * `e.Message is null`.
             *
             * O EventWaiter da Interactivity chama o predicado direto, sem
             * try/catch: um `e.Message.Id` cru derrubava o evento inteiro com
             * NullReferenceException ("erro no evento COMPONENT_INTERACTED")
             * sempre que um modal era enviado enquanto esta sessao esperava um
             * clique - o modal desta propria tela ou o de outra sessao do
             * comando. Filtrar por tipo antes de tocar no Message resolve.
             */
            bool Owned(ComponentInteractionCreateEventArgs e) =>
                e.Interaction.Type == InteractionType.Component
                && e.Message is not null
                && e.Message.Id == message.Id
                && e.User.Id == ctx.User.Id
                && (e.Interaction.Data?.CustomId ?? string.Empty).EndsWith(sessionId, StringComparison.Ordinal);

            DiscordWebhookBuilder? pending = null;

            while (true)
            {
                /*
                 * O waiter e registrado ANTES da edicao que desenha os botoes.
                 *
                 * Na ordem antiga - editar e so entao voltar a escutar - existia
                 * uma janela do tamanho de um round-trip HTTP em que nenhum waiter
                 * estava inscrito. Clique caido ali nunca era confirmado, e tres
                 * segundos depois a Discord mostrava "interacao falhou": era o
                 * erro que aparecia ao clicar as setas em sequencia. E a mesma
                 * armadilha que o /audit-add ja documenta ao registrar o waiter do
                 * modal antes de exibi-lo.
                 *
                 * Um unico waiter para o select E para os botoes: intercalar
                 * waiters separados e onde esse tipo de fluxo costuma quebrar.
                 */
                var waiter = interactivity.WaitForEventArgsAsync<ComponentInteractionCreateEventArgs>(
                    Owned, TimeSpan.FromMinutes(3));

                if (pending is not null)
                {
                    await ctx.EditResponseAsync(pending);
                    pending = null;
                }

                var result = await waiter;

                if (result.TimedOut)
                {
                    await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(new DiscordEmbedBuilder()
                        .WithTitle("Sessão expirada")
                        .WithDescription(Summary(changed))
                        .WithColor(DiscordColor.Orange)));
                    return;
                }

                var interaction = result.Result.Interaction;
                var customId = interaction.Data?.CustomId ?? string.Empty;

                if (customId.StartsWith("audit_rank_done", StringComparison.Ordinal))
                {
                    await interaction.CreateResponseAsync(InteractionResponseType.DeferredMessageUpdate);
                    await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(new DiscordEmbedBuilder()
                        .WithTitle("Cargos atualizados")
                        .WithDescription(Summary(changed))
                        .WithColor(DiscordColor.Green)));
                    return;
                }

                if (customId.StartsWith("audit_rank_clear", StringComparison.Ordinal))
                {
                    selection.Clear();
                    notice = null;
                    await interaction.CreateResponseAsync(InteractionResponseType.DeferredMessageUpdate);
                    pending = BuildView(roster, query, selection, changed, sessionId, notice);
                    continue;
                }

                if (customId.StartsWith("audit_rank_pick", StringComparison.Ordinal))
                {
                    // Acrescenta a cesta em vez de substitui-la: e isso que permite
                    // juntar gente de buscas diferentes sem paginar atras dela.
                    foreach (var username in interaction.Data?.Values ?? Array.Empty<string>())
                    {
                        if (selection.Count >= MaxPerBatch)
                            break;

                        if (!selection.Contains(username, StringComparer.OrdinalIgnoreCase))
                            selection.Add(username);
                    }

                    notice = selection.Count >= MaxPerBatch
                        ? $"ℹ️ Cesta cheia ({MaxPerBatch}). Defina os cargos ou limpe a seleção."
                        : null;

                    await interaction.CreateResponseAsync(InteractionResponseType.DeferredMessageUpdate);
                    pending = BuildView(roster, query, selection, changed, sessionId, notice);
                    continue;
                }

                if (customId.StartsWith("audit_rank_search", StringComparison.Ordinal))
                {
                    var (typed, cancelled) = await AskAsync(interactivity, interaction, sessionId,
                        "Buscar jogador",
                        "Parte do nome; vazio lista o efetivo inteiro",
                        "audit_rank_query",
                        new DiscordTextInputComponent(
                            TextComponentStyle.Small,
                            customId: "audit_rank_query",
                            placeholder: "Ex.: oda",
                            minLength: 0,
                            maxLength: 32,
                            required: false,
                            defaultValue: query));

                    if (cancelled)
                    {
                        notice = "⚠️ A busca expirou.";
                    }
                    else
                    {
                        query = (typed ?? string.Empty).Trim();
                        notice = null;
                    }

                    pending = BuildView(roster, query, selection, changed, sessionId, notice);
                    continue;
                }

                if (!customId.StartsWith("audit_rank_apply", StringComparison.Ordinal))
                    continue;

                if (selection.Count == 0)
                {
                    await interaction.CreateResponseAsync(InteractionResponseType.DeferredMessageUpdate);
                    continue;
                }

                var prefill = RankBlockParser.Build(selection.Select(username =>
                    (username, roster.FirstOrDefault(e =>
                        string.Equals(e.username, username, StringComparison.OrdinalIgnoreCase))?.rank)));

                var (block, blockCancelled) = await AskAsync(interactivity, interaction, sessionId,
                    "Definir cargos",
                    "Uma linha por jogador: nome = cargo",
                    "audit_rank_block",
                    new DiscordTextInputComponent(
                        TextComponentStyle.Paragraph,
                        customId: "audit_rank_block",
                        placeholder: "RafaOdebrecht = Serjeant Major",
                        minLength: 0,
                        maxLength: 4000,
                        required: false,
                        defaultValue: prefill));

                if (blockCancelled)
                {
                    notice = "⚠️ O modal expirou; nenhum cargo foi alterado.";
                    pending = BuildView(roster, query, selection, changed, sessionId, notice);
                    continue;
                }

                var parsed = RankBlockParser.Parse(block);

                var index = roster.ToDictionary(e => e.username, e => e, StringComparer.OrdinalIgnoreCase);
                var updates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var unknown = new List<string>();

                foreach (var (username, rank) in parsed.Entries)
                {
                    // Nome que nao existe na auditoria e reportado, nao engolido:
                    // um erro de digitacao viraria "0 cargos atualizados" sem dizer
                    // por que.
                    if (index.TryGetValue(username, out var entry))
                        updates[entry.username] = rank;
                    else
                        unknown.Add(username);
                }

                if (updates.Count > 0)
                {
                    await AuditStore.Instance.UpdateAsync((storedAudit, _) =>
                    {
                        var stored = AuditMerger.BuildIndex(storedAudit);
                        foreach (var (username, rank) in updates)
                        {
                            if (!stored.TryGetValue(username, out var target))
                                continue;

                            target.rank = rank;

                            var local = roster.FirstOrDefault(e =>
                                string.Equals(e.username, username, StringComparison.OrdinalIgnoreCase));
                            if (local is not null)
                                local.rank = rank;

                            changed[target.username] = rank;
                        }

                        return true;
                    });

                    await AuditLog.RecordAsync(ctx, AuditLog.ActionSetRanks,
                        string.Join(", ", updates.Select(kv =>
                            $"{kv.Key} → {(string.IsNullOrWhiteSpace(kv.Value) ? "sem cargo" : kv.Value)}")));

                    // Cesta esvaziada so quando algo foi de fato gravado: se o
                    // bloco veio todo errado, a selecao continua ali para corrigir.
                    selection.Clear();
                }

                notice = BuildNotice(updates.Count, unknown, parsed);
                pending = BuildView(roster, query, selection, changed, sessionId, notice);
            }
        }

        /// <summary>
        /// Abre um modal de campo unico e devolve o que foi digitado.
        ///
        /// O waiter e registrado antes de exibir o modal - mesma razao do waiter de
        /// componente la em cima. Responder a interacao do botao COM o modal evita
        /// um clique extra.
        /// </summary>
        private static async Task<(string? Value, bool Cancelled)> AskAsync(
            DisCatSharp.Interactivity.InteractivityExtension interactivity,
            DiscordInteraction interaction,
            string sessionId,
            string title,
            string label,
            string fieldId,
            DiscordTextInputComponent input)
        {
            var modalId = $"audit_rank_modal:{sessionId}:{Guid.NewGuid():N}";

            var modal = new DiscordInteractionModalBuilder()
                .WithTitle(title)
                .WithCustomId(modalId)
                .AddLabelComponent(new DiscordLabelComponent(label, null, null)
                    .WithTextComponent(input));

            var waiter = interactivity.WaitForModalAsync(modalId, TimeSpan.FromMinutes(3));
            await interaction.CreateInteractionModalResponseAsync(modal);

            var result = await waiter;
            if (result.TimedOut)
                return (null, true);

            var modalInteraction = result.Result.Interaction;
            await modalInteraction.CreateResponseAsync(InteractionResponseType.DeferredMessageUpdate);

            return (ModalUtil.ReadModalValue(modalInteraction, fieldId), false);
        }

        private static string BuildNotice(int applied, List<string> unknown, RankParseResult parsed)
        {
            var parts = new List<string>();

            if (applied > 0)
                parts.Add($"✅ {applied} cargo(s) atualizado(s).");

            if (unknown.Count > 0)
                parts.Add($"⚠️ Fora da auditoria: {AuditEmbeds.Trim(string.Join(", ", unknown), 200)}");

            foreach (var (lineNumber, raw, reason) in parsed.Failures.Take(3))
                parts.Add($"⚠️ Linha {lineNumber} (`{AuditEmbeds.Trim(raw, 40)}`): {reason}");

            if (parsed.Duplicates.Count > 0)
                parts.Add($"ℹ️ Repetidos no bloco (valeu a última linha): {AuditEmbeds.Trim(string.Join(", ", parsed.Duplicates.Distinct()), 150)}");

            if (parts.Count == 0)
                parts.Add("⚠️ Nada foi alterado: o bloco veio vazio.");

            return string.Join("\n", parts);
        }

        private static DiscordWebhookBuilder BuildView(
            List<AuditEntry> roster,
            string query,
            List<string> selection,
            Dictionary<string, string> changed,
            string sessionId,
            string? notice)
        {
            // Quem ja esta na cesta sai das opcoes: reaparecer so ocuparia uma das
            // 25 vagas do select para nao fazer nada.
            var matches = AuditUsernameAutocompleteProvider
                .Search(roster, query, SelectSize + selection.Count)
                .Where(e => !selection.Contains(e.username, StringComparer.OrdinalIgnoreCase))
                .Take(SelectSize)
                .ToList();

            var full = selection.Count >= MaxPerBatch;
            var livres = MaxPerBatch - selection.Count;

            var description = new List<string>();

            if (notice is not null)
                description.Add(notice + "\n");

            description.Add(query.Length == 0
                ? "🔍 Sem filtro — mostrando o começo do efetivo em ordem alfabética."
                : $"🔍 Busca: **{AuditEmbeds.Trim(query, 60)}** — {matches.Count} resultado(s) na lista.");

            if (selection.Count == 0)
            {
                description.Add($"\nCesta vazia. Selecione até {MaxPerBatch} jogadores; buscar de novo **não** perde a seleção.");
            }
            else
            {
                var cesta = string.Join("\n", selection.Select(username =>
                {
                    var entry = roster.FirstOrDefault(e =>
                        string.Equals(e.username, username, StringComparison.OrdinalIgnoreCase));
                    var rank = string.IsNullOrWhiteSpace(entry?.rank) ? "— sem cargo" : entry!.rank;
                    return $"{AuditEmbeds.Trim(username, 22).PadRight(22)} {rank}";
                }));

                description.Add($"\n**Na cesta ({selection.Count}/{MaxPerBatch}):**\n```\n{cesta}\n```");
            }

            var embed = new DiscordEmbedBuilder()
                .WithTitle("Definir cargos")
                .WithDescription(string.Join("\n", description))
                .WithColor(full ? DiscordColor.Orange : DiscordColor.Blurple)
                .WithFooter($"{roster.Count} jogadores na auditoria — {changed.Count} alterado(s) nesta sessão");

            var builder = new DiscordWebhookBuilder().AddEmbed(embed);

            // Sem match ou cesta cheia nao ha o que selecionar, e um select vazio e
            // recusado pela Discord - nesse caso ele simplesmente nao vai.
            if (matches.Count > 0 && !full)
            {
                var options = matches.Select(e => new DiscordStringSelectComponentOption(
                    AuditEmbeds.Trim(e.username, 100),
                    e.username,
                    AuditEmbeds.Trim($"K{e.kills} D{e.deaths} A{e.assists} B{e.battles} — {(string.IsNullOrWhiteSpace(e.rank) ? "sem cargo" : e.rank)}", 100)));

                // Argumentos nomeados de proposito: o construtor recebe
                // (placeholder, options, customId, ...) - o placeholder vem
                // PRIMEIRO. Passando posicionalmente, o texto do menu virava o
                // customId, o clique no select nunca casava com o sessionId e a
                // Discord respondia "interacao falhou".
                builder.AddComponents(new DiscordStringSelectComponent(
                    placeholder: $"Selecione jogadores (cabem mais {livres})",
                    options: options,
                    customId: $"audit_rank_pick:{sessionId}",
                    minOptions: 1,
                    maxOptions: Math.Min(livres, matches.Count)));
            }

            return builder.AddComponents(
                new DiscordButtonComponent(ButtonStyle.Primary, $"audit_rank_search:{sessionId}", "🔍 Buscar"),
                new DiscordButtonComponent(ButtonStyle.Success, $"audit_rank_apply:{sessionId}", "Definir cargos", selection.Count == 0),
                new DiscordButtonComponent(ButtonStyle.Secondary, $"audit_rank_clear:{sessionId}", "Limpar seleção", selection.Count == 0),
                new DiscordButtonComponent(ButtonStyle.Secondary, $"audit_rank_done:{sessionId}", "Concluir"));
        }

        private static string Summary(Dictionary<string, string> changed)
        {
            if (changed.Count == 0)
                return "Nenhum cargo foi alterado.";

            var lines = changed
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Take(40)
                .Select(kv => $"**{kv.Key}** → {(string.IsNullOrWhiteSpace(kv.Value) ? "*sem cargo*" : kv.Value)}");

            var text = string.Join("\n", lines);
            return changed.Count > 40 ? text + $"\n… e mais {changed.Count - 40}" : text;
        }
    }
}
