using System;
using System.Linq;
using System.Net.Http;
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
            [Option("sobrescrever", "Sobrescreve os dados de quem já existe pelos da planilha (perigoso)")] bool sobrescrever = false)
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
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var csv = await AuditCsvImporter.FetchAsync(config.audit, cts.Token);

                ImportReport report;
                if (dryRun)
                {
                    // Simula sobre uma copia: nada e gravado.
                    var preview = (await AuditStore.Instance.ReadAuditAsync()).Clone();
                    report = AuditMerger.MergeImport(preview, csv.Rows, sobrescrever, csv.HasKda);
                }
                else
                {
                    report = await AuditStore.Instance.UpdateAsync((audit, _) =>
                        AuditMerger.MergeImport(audit, csv.Rows, sobrescrever, csv.HasKda));
                }

                report.SkippedRows += csv.SkippedRows;

                // Dry run nao muda nada, entao nao entra no historico.
                if (!dryRun)
                    await AuditLog.RecordAsync(ctx, AuditLog.ActionImport,
                        $"Importou a planilha: {report.Added.Count} adicionado(s), " +
                        $"{report.Overwritten} sobrescrito(s), {report.SkippedRows} linha(s) ignorada(s)" +
                        (sobrescrever ? " (com sobrescrita)" : string.Empty) + ".");

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

                embed.AddField(new DiscordEmbedField("Observação", csv.HasKda
                    ? "A importação traz nome, **cargo**, **batalhas** e K/D/A da planilha. Jogadores que já existem no arquivo não são " +
                      "alterados: o `audit.json` passa a ser a fonte da verdade e já soma a planilha **mais** tudo que foi consolidado " +
                      "depois, então sobrescrever faria os totais voltarem atrás."
                    : "A planilha atual traz apenas **nome**, **patente** e **batalhas** — kills, deaths e assists não existem lá e são " +
                      "acumulados só pelo `/audit-add`, então a importação não toca neles. Jogadores que já existem no arquivo não são " +
                      "alterados: o `audit.json` é a fonte da verdade e já inclui tudo que foi consolidado depois da planilha.", false));

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(AuditEmbeds.Fit(embed)));
            }
            catch (OperationCanceledException)
            {
                // Inclui TaskCanceledException: sem este caso o texto interno do
                // .NET ("HttpClient.Timeout of N seconds") vazava para o usuario.
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(
                    AuditEmbeds.Error("Falha na importação",
                        "A planilha demorou demais para responder. Tente de novo em alguns minutos; " +
                        "se continuar, verifique a conexão da máquina do bot.")));
            }
            catch (HttpRequestException ex)
            {
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(
                    AuditEmbeds.Error("Falha na importação", AuditEmbeds.Trim(
                        $"Não consegui baixar a planilha: {ex.Message}", 1000))));
            }
            catch (Exception ex)
            {
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(
                    AuditEmbeds.Error("Falha na importação", AuditEmbeds.Trim(AuditGitHubService.DescribeError(ex), 1000))));
            }
        }
    }
}
