using System;
using System.Linq;
using System.Threading;
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
    internal class AuditImport : ApplicationCommandsModule
    {
        [SlashCommand("audit-import", "Importa a auditoria atual da planilha para o arquivo local")]
        public async Task AuditImportCommand(
            InteractionContext ctx,
            // Padrao true de proposito: este comando semeia o arquivo que guarda
            // o historico inteiro do regimento, entao gravar precisa ser explicito.
            [Option("dry_run", "Só mostra o que seria importado (padrão)")] bool dryRun = true,
            [Option("sobrescrever", "Sobrescreve K/D/A de quem já existe (perigoso)")] bool sobrescrever = false)
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

            if (config.audit is null)
            {
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(
                    AuditEmbeds.Error("Configuração ausente", "A seção `audit` não existe no config.jsonc.")));
                return;
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var csv = await AuditCsvImporter.FetchAsync(config.audit, cts.Token);

                ImportReport report;
                if (dryRun)
                {
                    // Simula sobre uma copia: nada e gravado.
                    var preview = (await AuditStore.Instance.ReadAuditAsync()).Clone();
                    report = AuditMerger.MergeImport(preview, csv.Rows, sobrescrever);
                }
                else
                {
                    report = await AuditStore.Instance.UpdateAsync((audit, _) =>
                        AuditMerger.MergeImport(audit, csv.Rows, sobrescrever));
                }

                report.SkippedRows += csv.SkippedRows;

                var embed = new DiscordEmbedBuilder()
                    .WithTitle(dryRun ? "Prévia da importação (dry run)" : "Importação concluída")
                    .WithDescription(dryRun
                        ? "Nada foi gravado. Rode com `dry_run: false` para aplicar."
                        : "O arquivo local foi atualizado. Publique com `/audit-push`.")
                    .WithColor(dryRun ? DiscordColor.Blurple : DiscordColor.Green)
                    .WithTimestamp(DateTimeOffset.UtcNow)
                    .AddField(new DiscordEmbedField("Linhas lidas da planilha", csv.Rows.Count.ToString(), true))
                    .AddField(new DiscordEmbedField("Adicionados", report.Added.Count.ToString(), true))
                    .AddField(new DiscordEmbedField("Linhas ignoradas", report.SkippedRows.ToString(), true));

                if (report.Added.Count > 0)
                    embed.AddField(new DiscordEmbedField("Novos jogadores", AuditEmbeds.FieldValue(report.Added.Take(20)), false));

                if (sobrescrever)
                    embed.AddField(new DiscordEmbedField("Sobrescritos", report.Overwritten.ToString(), true));
                else if (report.Conflicts.Count > 0)
                    embed.AddField(new DiscordEmbedField($"Já existentes com números diferentes ({report.Conflicts.Count})",
                        AuditEmbeds.FieldValue(report.Conflicts.Take(15)), false));

                embed.AddField(new DiscordEmbedField("Observação",
                    "A importação traz nome, **cargo**, **batalhas** e K/D/A da aba Roster, preservando todo o histórico já acumulado. " +
                    "Jogadores que já existem no arquivo não são alterados: o `audit.json` passa a ser a fonte da verdade e já soma " +
                    "a planilha **mais** tudo que foi consolidado depois, então sobrescrever faria os totais voltarem atrás.", false));

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(embed));
            }
            catch (Exception ex)
            {
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(
                    AuditEmbeds.Error("Falha na importação", AuditEmbeds.Trim(AuditGitHubService.DescribeError(ex), 1000))));
            }
        }
    }
}
