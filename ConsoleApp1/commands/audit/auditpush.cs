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

            var (audit, pending) = await AuditStore.Instance.ReadBothAsync();

            // Sem lote pendente o comando ainda tem trabalho: /audit-import,
            // /audit-edit e /audit-setranks mexem direto no arquivo consolidado, e
            // essas mudancas so chegam ao GitHub por aqui. Antes o comando parava
            // com "Nada pendente" e elas ficavam presas na maquina do bot.
            var hasPending = pending.batches.Count > 0;

            // A consolidacao acontece primeiro em memoria, sobre uma copia: nada e
            // gravado nem publicado enquanto o resultado nao estiver pronto.
            var merged = audit.Clone();
            var report = hasPending
                ? AuditMerger.MergePendingIntoAudit(merged, pending)
                : new MergeReport();

            if (dryRun)
            {
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(
                    BuildReportEmbed(report, pending, merged, hasPending, null, null, null, dryRun: true)));
                return;
            }

            var step = "inicialização";
            try
            {
                step = "resolução das credenciais";
                var github = await AuditGitHubService.CreateAsync(config.audit);
                var auditPath = string.IsNullOrWhiteSpace(config.audit?.auditFilePath) ? "audit.json" : config.audit!.auditFilePath!;
                var archiveDir = string.IsNullOrWhiteSpace(config.audit?.archiveDirectory) ? "audits" : config.audit!.archiveDirectory!;

                step = "verificação da branch";
                await github.EnsureBranchExistsAsync();

                string? archivePath = null;
                string? archiveSha = null;

                if (hasPending)
                {
                    // O arquivamento vai PRIMEIRO: e o registro bruto e imutavel do
                    // lote. Se o passo seguinte falhar, o lote ja esta salvo na branch
                    // e pode ser reprocessado.
                    step = "resolução do arquivo de arquivamento";
                    archivePath = await github.ResolveArchivePathAsync(archiveDir, DateTimeOffset.UtcNow);

                    step = "publicação do arquivamento";
                    archiveSha = await github.PutFileAsync(
                        archivePath,
                        AuditStore.Serialize(pending),
                        $"audit: arquivo de lote {DateTimeOffset.UtcNow:yyyy-MM-dd} ({pending.batches.Count} lote(s), {report.PlayersUpdated} jogador(es))");
                }
                else
                {
                    // Sem lote para arquivar, publicar so faz sentido se o GitHub
                    // estiver mesmo atrasado - senao geraria um commit vazio a cada
                    // execucao do comando.
                    step = "comparação com o GitHub";
                    var publishedJson = await github.TryGetFileContentAsync(auditPath);
                    var published = publishedJson is null ? null : AuditStore.Deserialize<AuditFile>(publishedJson);

                    if (published is not null && SameData(published, merged))
                    {
                        await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(new DiscordEmbedBuilder()
                            .WithTitle("Nada a publicar")
                            .WithDescription("Não há lote pendente e o GitHub já está igual ao arquivo local. " +
                                             "Use `/audit-add` para registrar uma batalha.")
                            .WithColor(DiscordColor.Orange)
                            .AddField(new DiscordEmbedField("Total no efetivo", merged.entries.Count.ToString(), true))));
                        return;
                    }
                }

                step = "publicação da auditoria consolidada";
                var auditSha = await github.PutFileAsync(
                    auditPath,
                    AuditStore.Serialize(merged),
                    hasPending
                        ? $"audit: consolidação {DateTimeOffset.UtcNow:yyyy-MM-dd} (+{report.BattlesAdded} batalha(s))"
                        : $"audit: sincronização do arquivo local {DateTimeOffset.UtcNow:yyyy-MM-dd} ({merged.entries.Count} jogador(es))");

                if (hasPending)
                {
                    // So depois dos dois envios: grava o consolidado localmente e limpa
                    // o pendente, na mesma operacao, para nunca ficarem divergentes.
                    step = "gravação local";
                    var consumed = pending.batches.Select(b => b.batchId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    await AuditStore.Instance.UpdateAsync((storedAudit, storedPending) =>
                    {
                        storedAudit.version = merged.version;
                        storedAudit.entries = merged.entries;
                        storedAudit.appliedBatchIds = merged.appliedBatchIds;
                        storedPending.batches.RemoveAll(b => consumed.Contains(b.batchId));
                        return true;
                    });
                }

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(
                    BuildReportEmbed(report, pending, merged, hasPending, github.CommitUrl(auditSha),
                        archiveSha is null ? null : github.CommitUrl(archiveSha), archivePath, dryRun: false)));
            }
            catch (Exception ex)
            {
                var embed = new DiscordEmbedBuilder()
                    .WithTitle("Falha ao publicar a auditoria")
                    .WithDescription($"A operação falhou durante: **{step}**.")
                    .WithColor(DiscordColor.IndianRed)
                    .AddField(new DiscordEmbedField("Motivo", AuditEmbeds.Trim(AuditGitHubService.DescribeError(ex), 1000), false))
                    .AddField(new DiscordEmbedField("O que acontece agora",
                        "O arquivo pendente **NÃO** foi limpo; corrija o problema e execute `/audit-push` novamente. " +
                        "Lotes já publicados não são contados duas vezes.", false));

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(embed));
            }
        }

        /// <summary>
        /// Compara os dados de duas versoes do arquivo consolidado ignorando
        /// lastUpdatedUtc: esse carimbo muda a cada gravacao local e sozinho nao
        /// justifica um commit.
        /// </summary>
        private static bool SameData(AuditFile a, AuditFile b)
        {
            var left = a.Clone();
            var right = b.Clone();
            left.lastUpdatedUtc = default;
            right.lastUpdatedUtc = default;

            return string.Equals(AuditStore.Serialize(left), AuditStore.Serialize(right), StringComparison.Ordinal);
        }

        private static DiscordEmbed BuildReportEmbed(
            MergeReport report,
            PendingFile pending,
            AuditFile merged,
            bool hasPending,
            string? auditCommitUrl,
            string? archiveCommitUrl,
            string? archivePath,
            bool dryRun)
        {
            var description = (dryRun, hasPending) switch
            {
                (true, true) => "Nada foi gravado nem enviado ao GitHub. Rode sem `dry_run` para publicar.",
                (true, false) => "Não há lote pendente. Rodar sem `dry_run` publica o estado atual do arquivo local " +
                                 "(mudanças de `/audit-import`, `/audit-edit` ou `/audit-setranks`), se o GitHub estiver desatualizado.",
                (false, true) => "Os lotes pendentes foram consolidados e enviados ao GitHub.",
                (false, false) => "Não havia lote pendente: o estado atual do arquivo local foi enviado ao GitHub."
            };

            var embed = new DiscordEmbedBuilder()
                .WithTitle(dryRun ? "Prévia da consolidação (dry run)" : "Auditoria publicada")
                .WithDescription(description)
                .WithColor(dryRun ? DiscordColor.Blurple : DiscordColor.Green)
                .WithTimestamp(DateTimeOffset.UtcNow)
                .AddField(new DiscordEmbedField("Lotes consolidados", report.BatchesApplied.ToString(), true))
                .AddField(new DiscordEmbedField("Jogadores atualizados", report.PlayersUpdated.ToString(), true))
                .AddField(new DiscordEmbedField("Batalhas somadas", report.BattlesAdded.ToString(), true))
                .AddField(new DiscordEmbedField("Total no efetivo", merged.entries.Count.ToString(), true));

            if (report.BatchesSkipped > 0)
                embed.AddField(new DiscordEmbedField("Lotes já contabilizados (ignorados)", report.BatchesSkipped.ToString(), true));

            if (report.NewPlayers.Count > 0)
                embed.AddField(new DiscordEmbedField($"Novos jogadores ({report.NewPlayers.Count})",
                    AuditEmbeds.FieldValue(report.NewPlayers.Take(30)), false));

            if (archivePath is not null)
                embed.AddField(new DiscordEmbedField("Arquivamento", $"`{archivePath}`", false));

            var links = new List<string>();
            if (auditCommitUrl is not null) links.Add($"[auditoria consolidada]({auditCommitUrl})");
            if (archiveCommitUrl is not null) links.Add($"[lote arquivado]({archiveCommitUrl})");
            if (links.Count > 0)
                embed.AddField(new DiscordEmbedField("Commits", string.Join(" · ", links), false));

            if (dryRun)
                embed.WithFooter($"{pending.batches.Count} lote(s) pendente(s)");

            return embed.Build();
        }
    }
}
