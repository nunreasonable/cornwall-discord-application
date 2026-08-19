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
using DisCatSharp.Interactivity.Extensions;

namespace CornwallUtilities.commands
{
    internal class AuditAdd : ApplicationCommandsModule
    {
        [SlashCommand("audit-add", "Cola uma auditoria de batalha (nome k d a n) na fila pendente")]
        public async Task AuditAddCommand(InteractionContext ctx)
        {
            var config = new JSONReader();
            await config.ReadJSON();

            // Um modal precisa ser a PRIMEIRA resposta da interacao, entao este
            // comando nao pode dar defer: a checagem de permissao responde
            // diretamente quando nega.
            var denied = AuditPermissions.CheckStaff(ctx, config);
            if (denied is not null)
            {
                await ctx.CreateResponseAsync(InteractionResponseType.ChannelMessageWithSource,
                    new DiscordInteractionResponseBuilder().AddEmbed(denied).AsEphemeral());
                return;
            }

            var modalId = $"audit_add:{ctx.User.Id}:{Guid.NewGuid():N}";

            var modal = new DiscordInteractionModalBuilder()
                .WithTitle("Nova auditoria de batalha")
                .WithCustomId(modalId)
                .AddLabelComponent(new DiscordLabelComponent(
                        "Bloco da auditoria",
                        "Uma linha por jogador: nome k d a n",
                        null)
                    .WithTextComponent(new DiscordTextInputComponent(
                        TextComponentStyle.Paragraph,
                        customId: "audit_block",
                        placeholder: "RafaOdebrecht 12 3 5 0",
                        minLength: 1,
                        maxLength: 4000,
                        required: true,
                        defaultValue: null)));

            var interactivity = ctx.Client.GetInteractivity();

            // Registrar o waiter ANTES de exibir o modal fecha a janela de corrida.
            var waiter = interactivity.WaitForModalAsync(modalId, TimeSpan.FromMinutes(10));
            await ctx.CreateModalResponseAsync(modal);

            var response = await waiter;
            if (response.TimedOut)
                return;

            var modalInteraction = response.Result.Interaction;
            await modalInteraction.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource,
                new DiscordInteractionResponseBuilder().AsEphemeral());

            var block = ModalUtil.ReadModalValue(modalInteraction, "audit_block");
            var parsed = AuditParser.Parse(block);

            if (parsed.Entries.Count == 0)
            {
                var empty = new DiscordEmbedBuilder()
                    .WithTitle("Nada foi registrado")
                    .WithDescription("Nenhuma linha válida foi encontrada. O formato esperado é `nome k d a n`, uma linha por jogador.")
                    .WithColor(DiscordColor.IndianRed);

                if (parsed.Failures.Count > 0)
                    empty.AddField(new DiscordEmbedField("Linhas não reconhecidas",
                        AuditEmbeds.FieldValue(parsed.Failures.Select(f => $"L{f.LineNumber}: `{AuditEmbeds.Trim(f.Raw, 60)}` — {f.Reason}")), false));

                await modalInteraction.EditOriginalResponseAsync(new DiscordWebhookBuilder().AddEmbed(empty));
                return;
            }

            var batch = new PendingBatch
            {
                batchId = Guid.NewGuid().ToString("N")[..12],
                createdUtc = DateTimeOffset.UtcNow,
                submittedByUserId = ctx.User.Id,
                submittedByUsername = ctx.User.Username,
                rawLines = (block ?? string.Empty)
                    .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0)
                    .ToList(),
                entries = parsed.Entries.Select(p => new AuditEntry
                {
                    username = p.Username,
                    kills = p.Kills,
                    deaths = p.Deaths,
                    assists = p.Assists,
                    battles = 1,
                    rank = string.Empty
                }).ToList()
            };

            // Grava apenas no pendente. O audit.json so muda no /audit-push.
            var (knownNames, pendingCount) = await AuditStore.Instance.UpdateAsync((audit, pending) =>
            {
                var index = AuditMerger.BuildIndex(audit);
                pending.batches.Add(batch);
                return (new HashSet<string>(index.Keys, StringComparer.OrdinalIgnoreCase), pending.batches.Count);
            });

            var newPlayers = batch.entries.Where(e => !knownNames.Contains(e.username)).Select(e => e.username).ToList();
            var existing = batch.entries.Where(e => knownNames.Contains(e.username)).Select(e => e.username).ToList();

            var preview = string.Join("\n", batch.entries.Select(e =>
                $"{AuditEmbeds.Trim(e.username, 18).PadRight(18)} {e.kills,-5} {e.deaths,-5} {e.assists,-5}"));

            if (preview.Length > 3500)
                preview = preview.Substring(0, 3500) + "\n… (truncado)";

            var hasProblems = parsed.Failures.Count > 0 || parsed.Warnings.Count > 0 || parsed.Duplicates.Count > 0;

            var embed = new DiscordEmbedBuilder()
                .WithTitle("Auditoria registrada na fila")
                .WithDescription($"```\n{"Nome".PadRight(18)} {"K",-5} {"D",-5} {"A",-5}\n{preview}\n```")
                .WithColor(hasProblems ? DiscordColor.Orange : DiscordColor.Green)
                .WithTimestamp(DateTimeOffset.UtcNow)
                .AddField(new DiscordEmbedField("Jogadores", batch.entries.Count.ToString(), true))
                .AddField(new DiscordEmbedField("Batalhas somadas", $"+1 para cada jogador", true));

            if (newPlayers.Count > 0)
                embed.AddField(new DiscordEmbedField($"Novos jogadores ({newPlayers.Count})", AuditEmbeds.FieldValue(newPlayers.Take(30)), false));

            if (existing.Count > 0)
                embed.AddField(new DiscordEmbedField($"Já existentes ({existing.Count})", AuditEmbeds.FieldValue(existing.Take(30)), false));

            if (parsed.Duplicates.Count > 0)
                embed.AddField(new DiscordEmbedField("Repetidos no mesmo lote (K/D/A somados)", AuditEmbeds.FieldValue(parsed.Duplicates), false));

            if (parsed.Warnings.Count > 0)
                embed.AddField(new DiscordEmbedField("Avisos", AuditEmbeds.FieldValue(parsed.Warnings.Take(10)), false));

            if (parsed.Failures.Count > 0)
                embed.AddField(new DiscordEmbedField($"Linhas não reconhecidas ({parsed.Failures.Count})",
                    AuditEmbeds.FieldValue(parsed.Failures.Take(10).Select(f => $"L{f.LineNumber}: `{AuditEmbeds.Trim(f.Raw, 50)}` — {f.Reason}")), false));

            embed.WithFooter($"Lote {batch.batchId} — {pendingCount} lote(s) pendente(s). Use /audit-push para consolidar.");

            await AuditLog.RecordAsync(ctx, AuditLog.ActionAdd,
                $"Lote `{batch.batchId}` com {batch.entries.Count} jogador(es): " +
                string.Join(", ", batch.entries.Take(15).Select(e => e.username)) +
                (batch.entries.Count > 15 ? $" e mais {batch.entries.Count - 15}" : string.Empty));

            await modalInteraction.EditOriginalResponseAsync(new DiscordWebhookBuilder().AddEmbed(AuditEmbeds.Fit(embed)));
        }
    }
}
