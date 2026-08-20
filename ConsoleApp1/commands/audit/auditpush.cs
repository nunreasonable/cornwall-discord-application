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

namespace CornwallUtilities.commands
{
    internal class AuditPush : ApplicationCommandsModule
    {
        [SlashCommand("audit-push", "Publica a auditoria no GitHub, consolidando os lotes pendentes se houver")]
        public async Task AuditPushCommand(
            InteractionContext ctx,
            [Option("dry_run", "Só mostra o que seria consolidado, sem tocar no GitHub")] bool dryRun = false)
        {
            await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);

            var config = new JSONReader();
            await config.ReadJSON();

            var denied = AuditPermissions.CheckStaff(ctx, config);
            if (denied is not null)
            {
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(denied));
                return;
            }

            // Toda a mecanica de consolidacao e publicacao vive no
            // AuditPublisher, porque o dashboard publica pela mesma porta.
            var outcome = await AuditPublisher.PublishAsync(config, dryRun);

            if (!outcome.Ok)
            {
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(new DiscordEmbedBuilder()
                    .WithTitle("Falha ao publicar a auditoria")
                    .WithDescription($"A operação falhou durante: **{outcome.FailedStep}**.")
                    .WithColor(DiscordColor.IndianRed)
                    .AddField(new DiscordEmbedField("Motivo", AuditEmbeds.Trim(outcome.Error, 1000), false))
                    .AddField(new DiscordEmbedField("O que acontece agora",
                        "O arquivo pendente **NÃO** foi limpo; corrija o problema e execute `/audit-push` novamente. " +
                        "Lotes já publicados não são contados duas vezes.", false))));
                return;
            }

            if (outcome.NothingToDo)
            {
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(new DiscordEmbedBuilder()
                    .WithTitle("Nada a publicar")
                    .WithDescription("Não há lote pendente e o GitHub já está igual ao arquivo local. " +
                                     "Use `/audit-add` para registrar uma batalha.")
                    .WithColor(DiscordColor.Orange)
                    .AddField(new DiscordEmbedField("Total no efetivo", outcome.TotalEntries.ToString(), true))));
                return;
            }

            if (!outcome.DryRun)
                await AuditLog.RecordAsync(ctx, AuditLog.ActionPush, outcome.LogDetails);

            await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(BuildReportEmbed(outcome)));
        }

        private static DiscordEmbed BuildReportEmbed(PublishOutcome outcome)
        {
            var description = (outcome.DryRun, outcome.HasPending) switch
            {
                (true, true) => "Nada foi gravado nem enviado ao GitHub. Rode sem `dry_run` para publicar.",
                (true, false) => "Não há lote pendente. Rodar sem `dry_run` publica o estado atual do arquivo local " +
                                 "(mudanças de `/audit-import`, `/audit-edit` ou `/audit-setranks`), se o GitHub estiver desatualizado.",
                (false, true) => "Os lotes pendentes foram consolidados e enviados ao GitHub.",
                (false, false) => "Não havia lote pendente: o estado atual do arquivo local foi enviado ao GitHub."
            };

            var report = outcome.Report;
            var embed = new DiscordEmbedBuilder()
                .WithTitle(outcome.DryRun ? "Prévia da consolidação (dry run)" : "Auditoria publicada")
                .WithDescription(description)
                .WithColor(outcome.DryRun ? DiscordColor.Blurple : DiscordColor.Green)
                .WithTimestamp(DateTimeOffset.UtcNow)
                .AddField(new DiscordEmbedField("Lotes consolidados", report.BatchesApplied.ToString(), true))
                .AddField(new DiscordEmbedField("Jogadores atualizados", report.PlayersUpdated.ToString(), true))
                .AddField(new DiscordEmbedField("Batalhas somadas", report.BattlesAdded.ToString(), true))
                .AddField(new DiscordEmbedField("Total no efetivo", outcome.TotalEntries.ToString(), true));

            if (report.BatchesSkipped > 0)
                embed.AddField(new DiscordEmbedField("Lotes já contabilizados (ignorados)", report.BatchesSkipped.ToString(), true));

            if (report.NewPlayers.Count > 0)
                embed.AddField(new DiscordEmbedField($"Novos jogadores ({report.NewPlayers.Count})",
                    AuditEmbeds.FieldValue(report.NewPlayers.Take(30)), false));

            if (outcome.ArchivePath is not null)
                embed.AddField(new DiscordEmbedField("Arquivamento", $"`{outcome.ArchivePath}`", false));

            var links = new List<string>();
            if (outcome.AuditCommitUrl is not null) links.Add($"[auditoria consolidada]({outcome.AuditCommitUrl})");
            if (outcome.ArchiveCommitUrl is not null) links.Add($"[lote arquivado]({outcome.ArchiveCommitUrl})");
            if (links.Count > 0)
                embed.AddField(new DiscordEmbedField("Commits", string.Join(" · ", links), false));

            if (outcome.DryRun)
                embed.WithFooter($"{outcome.PendingBatches} lote(s) pendente(s)");

            return AuditEmbeds.Fit(embed).Build();
        }
    }
}
