using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CornwallUtilities.config;
using CornwallUtilities.Services.Audit;
using DisCatSharp.ApplicationCommands;
using DisCatSharp.ApplicationCommands.Attributes;
using DisCatSharp.ApplicationCommands.Context;
using DisCatSharp.Entities;
using DisCatSharp.Enums;
using DisCatSharp.Interactivity.Extensions;

namespace CornwallUtilities.commands
{
    internal class AuditEdit : ApplicationCommandsModule
    {
        [SlashCommand("audit-edit", "Edita ou remove um jogador da auditoria")]
        public async Task AuditEditCommand(
            InteractionContext ctx,
            [Autocomplete(typeof(AuditUsernameAutocompleteProvider))]
            [Option("jogador", "Nome do jogador na auditoria", true)] string jogador,
            [Choice("Auditoria consolidada", "audit")]
            [Choice("Lotes pendentes", "pending")]
            [Option("escopo", "Onde editar")] string escopo = "audit",
            [Option("remover", "Remover o jogador em vez de editar")] bool remover = false)
        {
            var config = new JSONReader();
            await config.ReadJSON();

            var denied = AuditPermissions.CheckStaff(ctx, config);
            if (denied is not null)
            {
                await ctx.CreateResponseAsync(InteractionResponseType.ChannelMessageWithSource,
                    new DiscordInteractionResponseBuilder().AddEmbed(denied).AsEphemeral());
                return;
            }

            var editingPending = string.Equals(escopo, "pending", StringComparison.OrdinalIgnoreCase);
            var (audit, pending) = await AuditStore.Instance.ReadBothAsync();

            var current = editingPending
                ? pending.batches.OrderByDescending(b => b.createdUtc)
                    .SelectMany(b => b.entries)
                    .FirstOrDefault(e => string.Equals(e.username, jogador, StringComparison.OrdinalIgnoreCase))
                : audit.entries.FirstOrDefault(e => string.Equals(e.username, jogador, StringComparison.OrdinalIgnoreCase));

            if (current is null)
            {
                var pool = editingPending
                    ? pending.batches.SelectMany(b => b.entries).Select(e => e.username)
                    : audit.entries.Select(e => e.username);

                var suggestions = pool
                    .Where(u => u.Contains(jogador, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(10)
                    .ToList();

                var notFound = new DiscordEmbedBuilder()
                    .WithTitle("Jogador não encontrado")
                    .WithDescription($"**{AuditEmbeds.Trim(jogador, 60)}** não existe em `{escopo}`.")
                    .WithColor(DiscordColor.IndianRed);

                if (suggestions.Count > 0)
                    notFound.AddField(new DiscordEmbedField("Você quis dizer", AuditEmbeds.FieldValue(suggestions), false));

                await ctx.CreateResponseAsync(InteractionResponseType.ChannelMessageWithSource,
                    new DiscordInteractionResponseBuilder().AddEmbed(notFound).AsEphemeral());
                return;
            }

            if (remover)
            {
                await ConfirmDeleteAsync(ctx, current, editingPending);
                return;
            }

            await ShowEditModalAsync(ctx, current, editingPending);
        }

        /// <summary>Remocao e destrutiva: sempre passa por uma confirmacao.</summary>
        private static async Task ConfirmDeleteAsync(InteractionContext ctx, AuditEntry current, bool editingPending)
        {
            var token = Guid.NewGuid().ToString("N")[..8];
            var confirmId = $"audit_edit_del_confirm:{token}";
            var cancelId = $"audit_edit_del_cancel:{token}";

            var prompt = new DiscordEmbedBuilder()
                .WithTitle("Confirmar remoção")
                .WithDescription($"Remover **{current.username}** de `{(editingPending ? "pendente" : "auditoria")}`?\n" +
                                 $"```\n{AuditEmbeds.Header()}\n{AuditEmbeds.FormatEntry(current)}\n```")
                .WithColor(DiscordColor.Orange);

            await ctx.CreateResponseAsync(InteractionResponseType.ChannelMessageWithSource,
                new DiscordInteractionResponseBuilder()
                    .AddEmbed(prompt)
                    .AddComponents(
                        new DiscordButtonComponent(ButtonStyle.Danger, confirmId, "Remover"),
                        new DiscordButtonComponent(ButtonStyle.Secondary, cancelId, "Cancelar"))
                    .AsEphemeral());

            var message = await ctx.GetOriginalResponseAsync();
            var result = await message.WaitForButtonAsync(ctx.User, TimeSpan.FromMinutes(1));

            if (result.TimedOut || result.Result.Interaction.Data.CustomId == cancelId)
            {
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(new DiscordEmbedBuilder()
                    .WithTitle("Remoção cancelada")
                    .WithColor(DiscordColor.Blurple)));
                return;
            }

            await result.Result.Interaction.CreateResponseAsync(InteractionResponseType.DeferredMessageUpdate);

            var removed = await AuditStore.Instance.UpdateAsync((storedAudit, storedPending) =>
            {
                if (editingPending)
                {
                    var count = 0;
                    foreach (var batch in storedPending.batches)
                        count += batch.entries.RemoveAll(e => string.Equals(e.username, current.username, StringComparison.OrdinalIgnoreCase));
                    return count;
                }

                return storedAudit.entries.RemoveAll(e => string.Equals(e.username, current.username, StringComparison.OrdinalIgnoreCase));
            });

            await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(new DiscordEmbedBuilder()
                .WithTitle("Jogador removido")
                .WithDescription($"**{current.username}** removido ({removed} registro(s)).")
                .WithColor(DiscordColor.Green)));
        }

        private static async Task ShowEditModalAsync(InteractionContext ctx, AuditEntry current, bool editingPending)
        {
            var modalId = $"audit_edit:{ctx.User.Id}:{Guid.NewGuid():N}";

            // Exatamente 5 campos - o teto do Discord para modais. O cargo fica de
            // fora de proposito: ele pertence ao /audit-setranks, e e justamente
            // essa exclusao que mantem o modal dentro do limite.
            var modal = new DiscordInteractionModalBuilder()
                .WithTitle($"Editar {AuditEmbeds.Trim(current.username, 30)}")
                .WithCustomId(modalId)
                .AddLabelComponent(Field("Nome de usuário (Roblox)", "audit_edit_username", current.username))
                .AddLabelComponent(Field("Kills", "audit_edit_kills", current.kills.ToString()))
                .AddLabelComponent(Field("Deaths", "audit_edit_deaths", current.deaths.ToString()))
                .AddLabelComponent(Field("Assists", "audit_edit_assists", current.assists.ToString()))
                .AddLabelComponent(Field("Batalhas", "audit_edit_battles", current.battles.ToString()));

            var interactivity = ctx.Client.GetInteractivity();
            var waiter = interactivity.WaitForModalAsync(modalId, TimeSpan.FromMinutes(10));
            await ctx.CreateModalResponseAsync(modal);

            var response = await waiter;
            if (response.TimedOut)
                return;

            var modalInteraction = response.Result.Interaction;
            await modalInteraction.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource,
                new DiscordInteractionResponseBuilder().AsEphemeral());

            var newName = (ModalUtil.ReadModalValue(modalInteraction, "audit_edit_username") ?? string.Empty).Trim();
            var rawKills = ModalUtil.ReadModalValue(modalInteraction, "audit_edit_kills");
            var rawDeaths = ModalUtil.ReadModalValue(modalInteraction, "audit_edit_deaths");
            var rawAssists = ModalUtil.ReadModalValue(modalInteraction, "audit_edit_assists");
            var rawBattles = ModalUtil.ReadModalValue(modalInteraction, "audit_edit_battles");

            if (newName.Length == 0 || newName.Length > 32 || newName.Contains(','))
            {
                await Fail(modalInteraction, "O nome precisa ter entre 1 e 32 caracteres e não pode conter vírgula.");
                return;
            }

            if (!TryReadCount(rawKills, out var kills) ||
                !TryReadCount(rawDeaths, out var deaths) ||
                !TryReadCount(rawAssists, out var assists) ||
                !TryReadCount(rawBattles, out var battles))
            {
                await Fail(modalInteraction, "Kills, deaths, assists e batalhas precisam ser números inteiros não negativos.");
                return;
            }

            var before = current.Clone();

            var outcome = await AuditStore.Instance.UpdateAsync<string?>((storedAudit, storedPending) =>
            {
                var renamed = !string.Equals(before.username, newName, StringComparison.OrdinalIgnoreCase);

                if (editingPending)
                {
                    if (renamed && storedPending.batches.SelectMany(b => b.entries)
                            .Any(e => string.Equals(e.username, newName, StringComparison.OrdinalIgnoreCase)))
                        return $"Já existe **{newName}** nos lotes pendentes. Renomeie para outro nome ou remova a duplicata.";

                    var found = false;
                    foreach (var entry in storedPending.batches.SelectMany(b => b.entries)
                                 .Where(e => string.Equals(e.username, before.username, StringComparison.OrdinalIgnoreCase)))
                    {
                        entry.username = newName;
                        entry.kills = kills;
                        entry.deaths = deaths;
                        entry.assists = assists;
                        found = true;
                    }

                    return found ? null : "O jogador não está mais nos lotes pendentes.";
                }

                if (renamed && storedAudit.entries.Any(e => string.Equals(e.username, newName, StringComparison.OrdinalIgnoreCase)))
                    return $"Já existe **{newName}** na auditoria. Renomear aqui juntaria dois jogadores sem aviso, então a operação foi cancelada.";

                var target = storedAudit.entries.FirstOrDefault(e => string.Equals(e.username, before.username, StringComparison.OrdinalIgnoreCase));
                if (target is null)
                    return "O jogador não está mais na auditoria.";

                target.username = newName;
                target.kills = kills;
                target.deaths = deaths;
                target.assists = assists;
                target.battles = battles;
                return null;
            });

            if (outcome is not null)
            {
                await Fail(modalInteraction, outcome);
                return;
            }

            var after = new AuditEntry
            {
                username = newName,
                kills = kills,
                deaths = deaths,
                assists = assists,
                battles = editingPending ? before.battles : battles,
                rank = before.rank
            };

            var embed = new DiscordEmbedBuilder()
                .WithTitle("Registro atualizado")
                .WithDescription($"Escopo: `{(editingPending ? "pendente" : "auditoria")}`")
                .WithColor(DiscordColor.Green)
                .WithTimestamp(DateTimeOffset.UtcNow)
                .AddField(new DiscordEmbedField("Antes", $"```\n{AuditEmbeds.Header()}\n{AuditEmbeds.FormatEntry(before)}\n```", false))
                .AddField(new DiscordEmbedField("Depois", $"```\n{AuditEmbeds.Header()}\n{AuditEmbeds.FormatEntry(after)}\n```", false));

            if (editingPending)
                embed.WithFooter("Batalhas não são editáveis no escopo pendente: elas são contadas por lote no /audit-push.");

            await modalInteraction.EditOriginalResponseAsync(new DiscordWebhookBuilder().AddEmbed(embed));
        }

        private static DiscordLabelComponent Field(string label, string customId, string value) =>
            new DiscordLabelComponent(label, null, null)
                .WithTextComponent(new DiscordTextInputComponent(
                    TextComponentStyle.Small,
                    customId: customId,
                    placeholder: null,
                    minLength: 0,
                    maxLength: 32,
                    required: true,
                    defaultValue: value));

        private static bool TryReadCount(string? raw, out int value) =>
            int.TryParse((raw ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= 0;

        private static Task Fail(DiscordInteraction interaction, string reason) =>
            interaction.EditOriginalResponseAsync(new DiscordWebhookBuilder()
                .AddEmbed(AuditEmbeds.Error("Edição não aplicada", reason)));
    }
}
