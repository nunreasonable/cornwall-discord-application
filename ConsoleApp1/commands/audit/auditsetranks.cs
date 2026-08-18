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
        private const int PageSize = 25;      // teto de opções de um select
        private const int MaxPerModal = 5;    // teto de campos de um modal

        [SlashCommand("audit-setranks", "Lista os jogadores ativos e permite definir o cargo de cada um")]
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
            var page = 0;
            var pageCount = (roster.Count + PageSize - 1) / PageSize;

            // Estado da sessao vive nas variaveis locais deste metodo: nada de
            // dicionario global, nada para vazar.
            var changed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? notice = null;

            await ctx.EditResponseAsync(BuildView(roster, page, pageCount, sessionId, changed, notice));
            var message = await ctx.GetOriginalResponseAsync();

            var interactivity = ctx.Client.GetInteractivity();

            while (true)
            {
                // Um unico waiter para o select E para os botoes: intercalar
                // waiters separados e onde esse tipo de fluxo costuma quebrar.
                var result = await interactivity.WaitForEventArgsAsync<ComponentInteractionCreateEventArgs>(
                    e => e.Message.Id == message.Id
                         && e.User.Id == ctx.User.Id
                         && (e.Interaction.Data?.CustomId ?? string.Empty).EndsWith(sessionId, StringComparison.Ordinal),
                    TimeSpan.FromMinutes(3));

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

                if (customId.StartsWith("audit_rank_prev", StringComparison.Ordinal) ||
                    customId.StartsWith("audit_rank_next", StringComparison.Ordinal))
                {
                    page += customId.StartsWith("audit_rank_next", StringComparison.Ordinal) ? 1 : -1;
                    page = Math.Clamp(page, 0, pageCount - 1);
                    notice = null;

                    await interaction.CreateResponseAsync(InteractionResponseType.DeferredMessageUpdate);
                    await ctx.EditResponseAsync(BuildView(roster, page, pageCount, sessionId, changed, notice));
                    continue;
                }

                if (!customId.StartsWith("audit_rank_pick", StringComparison.Ordinal))
                    continue;

                var selected = (interaction.Data?.Values ?? Array.Empty<string>()).Take(MaxPerModal).ToList();
                if (selected.Count == 0)
                {
                    await interaction.CreateResponseAsync(InteractionResponseType.DeferredMessageUpdate);
                    continue;
                }

                var modalId = $"audit_rank_modal:{sessionId}:{Guid.NewGuid():N}";
                var modal = new DiscordInteractionModalBuilder()
                    .WithTitle("Definir cargos")
                    .WithCustomId(modalId);

                for (var i = 0; i < selected.Count; i++)
                {
                    var entry = roster.FirstOrDefault(e => string.Equals(e.username, selected[i], StringComparison.OrdinalIgnoreCase));
                    modal.AddLabelComponent(new DiscordLabelComponent(
                            AuditEmbeds.Trim(selected[i], 45),
                            "Deixe em branco para remover o cargo",
                            null)
                        .WithTextComponent(new DiscordTextInputComponent(
                            TextComponentStyle.Small,
                            customId: $"rank_{i}",
                            placeholder: "Ex.: Sargento",
                            minLength: 0,
                            maxLength: 40,
                            required: false,
                            defaultValue: entry?.rank ?? string.Empty)));
                }

                // Responder a interacao do select COM o modal evita um clique extra.
                var modalWaiter = interactivity.WaitForModalAsync(modalId, TimeSpan.FromMinutes(3));
                await interaction.CreateInteractionModalResponseAsync(modal);

                var modalResult = await modalWaiter;
                if (modalResult.TimedOut)
                {
                    notice = "⚠️ O modal expirou; nenhum cargo foi alterado.";
                    await ctx.EditResponseAsync(BuildView(roster, page, pageCount, sessionId, changed, notice));
                    continue;
                }

                var modalInteraction = modalResult.Result.Interaction;
                await modalInteraction.CreateResponseAsync(InteractionResponseType.DeferredMessageUpdate);

                var updates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < selected.Count; i++)
                {
                    // Valor em branco limpa o cargo (texto livre, sem lista fixa).
                    var value = (ModalUtil.ReadModalValue(modalInteraction, $"rank_{i}") ?? string.Empty).Trim();
                    updates[selected[i]] = value;
                }

                await AuditStore.Instance.UpdateAsync((storedAudit, _) =>
                {
                    var index = AuditMerger.BuildIndex(storedAudit);
                    foreach (var (username, rank) in updates)
                    {
                        if (!index.TryGetValue(username, out var target))
                            continue;

                        target.rank = rank;

                        var local = roster.FirstOrDefault(e => string.Equals(e.username, username, StringComparison.OrdinalIgnoreCase));
                        if (local is not null)
                            local.rank = rank;

                        changed[target.username] = rank;
                    }

                    return true;
                });

                notice = $"✅ {updates.Count} cargo(s) atualizado(s).";
                await ctx.EditResponseAsync(BuildView(roster, page, pageCount, sessionId, changed, notice));
            }
        }

        private static DiscordWebhookBuilder BuildView(
            List<AuditEntry> roster,
            int page,
            int pageCount,
            string sessionId,
            Dictionary<string, string> changed,
            string? notice)
        {
            var slice = roster.Skip(page * PageSize).Take(PageSize).ToList();

            var body = string.Join("\n", slice.Select(e =>
                $"{AuditEmbeds.Trim(e.username, 20).PadRight(20)} {(string.IsNullOrWhiteSpace(e.rank) ? "— sem cargo" : e.rank)}"));

            var embed = new DiscordEmbedBuilder()
                .WithTitle("Definir cargos")
                .WithDescription(
                    (notice is null ? string.Empty : notice + "\n\n") +
                    $"Selecione até {MaxPerModal} jogadores para editar de uma vez.\n```\n{body}\n```")
                .WithColor(DiscordColor.Blurple)
                .WithFooter($"Página {page + 1}/{pageCount} — {roster.Count} jogadores — {changed.Count} alterado(s) nesta sessão");

            var options = slice.Select(e => new DiscordStringSelectComponentOption(
                AuditEmbeds.Trim(e.username, 100),
                e.username,
                AuditEmbeds.Trim($"K{e.kills} D{e.deaths} A{e.assists} B{e.battles} — {(string.IsNullOrWhiteSpace(e.rank) ? "sem cargo" : e.rank)}", 100)));

            var select = new DiscordStringSelectComponent(
                $"audit_rank_pick:{sessionId}",
                options,
                "Selecione os jogadores",
                1,
                Math.Min(MaxPerModal, slice.Count));

            return new DiscordWebhookBuilder()
                .AddEmbed(embed)
                .AddComponents(select)
                .AddComponents(
                    new DiscordButtonComponent(ButtonStyle.Secondary, $"audit_rank_prev:{sessionId}", "◀ Anterior", page == 0),
                    new DiscordButtonComponent(ButtonStyle.Secondary, $"audit_rank_next:{sessionId}", "Próxima ▶", page >= pageCount - 1),
                    new DiscordButtonComponent(ButtonStyle.Success, $"audit_rank_done:{sessionId}", "Concluir"));
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
