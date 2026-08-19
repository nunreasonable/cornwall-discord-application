using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CornwallUtilities.config;
using CornwallUtilities.Services;
using DisCatSharp.Entities;
using DisCatSharp.ApplicationCommands;
using DisCatSharp.Enums;
using DisCatSharp.ApplicationCommands.Context;
using DisCatSharp.ApplicationCommands.Attributes;

namespace CornwallUtilities.commands
{
    internal class CheckSpreadsheetInfo : ApplicationCommandsModule
    {
        [SlashCommand("checkspreadsheetinfo", "Verifica as informações da planilha Regimental")]
        public async Task CheckSpreadsheetInfoCommand(InteractionContext ctx, [Option("username", "Seu nome de usuário na planilha.")] string username)
        {
            // Defer response while we fetch the spreadsheet and prepare the embed
            await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);

            var config = new JSONReader();
            await config.ReadJSON();
            var url = config.spreadsheetRosterCsvUrl ?? config.spreadsheetCsvUrl;

            if (string.IsNullOrWhiteSpace(url))
            {
                var errEmbed = new DiscordEmbedBuilder()
                    .WithTitle("Erro ao buscar planilha")
                    .WithDescription("A URL da planilha (aba Roster) não está configurada. Verifique o arquivo config.json.")
                    .WithColor(DiscordColor.IndianRed);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(errEmbed));
                return;
            }

            try
            {
                // Prazo explicito no lugar do teto silencioso de 20s do cliente
                // compartilhado: a exportacao da planilha pode demorar.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var csvText = await HttpClientProvider.LongRunning.GetStringAsync(url, cts.Token);

                // ParseCsv le o documento inteiro: quebrar por linha antes corrompia
                // os registros cujos campos entre aspas contem quebra de linha.
                var records = CsvUtil.ParseCsv(csvText);

                if (records.Count == 0)
                {
                    throw new Exception("A planilha retornou um CSV vazio.");
                }

                var headers = records[0];

                // Row 4 (index 3) contains the display labels for columns; data starts below it
                const int labelRowIndex = 3;
                var labels = records.Count > labelRowIndex
                    ? records[labelRowIndex]
                    : headers;

                var rows = records.Skip(labelRowIndex + 1)
                    .Where(r => r.Count > 0)
                    .ToList();

                // In the roster sheet, usernames are expected to be in column D (4th column)
                const int usernameColumnIndex = 3;
                var usernameColumnName = labels.ElementAtOrDefault(usernameColumnIndex) ?? "Coluna D";

                // Filter rows by the provided username (case-insensitive comparison)
                var matchingRows = rows
                    .Where(r => usernameColumnIndex < r.Count &&
                                string.Equals(r[usernameColumnIndex], username, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matchingRows.Count == 0)
                {
                    var notFoundEmbed = new DiscordEmbedBuilder()
                        .WithTitle("Usuário não encontrado")
                        .WithDescription($"Nenhum registro para **{username}** foi encontrado na coluna D da planilha.")
                        .WithColor(DiscordColor.IndianRed);
                        notFoundEmbed.AddField(new DiscordEmbedField("Coluna pesquisada", usernameColumnName, true))
                        .AddField(new DiscordEmbedField("Total de linhas", rows.Count.ToString(), true));

                    await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(notFoundEmbed));
                    return;
                }

                // Build an embed with the first matching row and a summary
                var foundRow = matchingRows[0];
                var embed = new DiscordEmbedBuilder()
                    .WithTitle("Informações da planilha (Roster)")
                    .WithDescription($"Dados encontrados para **{username}**.")
                    .WithColor(DiscordColor.Blurple)
                    .WithTimestamp(DateTimeOffset.UtcNow)
                    .AddField(new DiscordEmbedField("Linhas totais", rows.Count.ToString(), true))
                    .AddField(new DiscordEmbedField("Resultados encontrados", matchingRows.Count.ToString(), true));

                // Build a display of all columns for the matched row, using the labels from row 4
                // Skip columns where both the label and the value are empty.
                var columnLines = new List<string>();
                var maxColumns = Math.Max(labels.Count, foundRow.Count);
                for (var i = 0; i < maxColumns; i++)
                {
                    var rawLabel = i < labels.Count ? labels[i] : string.Empty;
                    var label = !string.IsNullOrWhiteSpace(rawLabel) ? rawLabel : string.Empty;
                    var value = i < foundRow.Count ? foundRow[i] : string.Empty;

                    if (string.IsNullOrWhiteSpace(label) && string.IsNullOrWhiteSpace(value))
                        continue; // ignore entirely empty columns

                    if (string.IsNullOrWhiteSpace(label))
                        label = $"Coluna {i + 1}";

                    columnLines.Add($"**{label}**: {(!string.IsNullOrEmpty(value) ? value : "(vazio)")}");
                }

                // Quebra em varios fields respeitando os DOIS limites do Discord:
                // 1024 caracteres por field e 6000 no embed inteiro. So o primeiro
                // era conferido, entao uma linha com muitas colunas podia montar
                // 24 fields e levar BadRequest na resposta toda.
                const int maxTotalChars = 5500;
                var fieldText = new StringBuilder();
                var fieldCount = 0;
                var totalChars = embed.Description?.Length ?? 0;
                var truncated = false;

                void Flush()
                {
                    var name = $"Dados (parte {fieldCount + 1})";
                    embed.AddField(new DiscordEmbedField(name, fieldText.ToString(), false));
                    totalChars += name.Length + fieldText.Length;
                    fieldText.Clear();
                    fieldCount++;
                }

                foreach (var line in columnLines)
                {
                    if (fieldText.Length + line.Length + 1 > 900) // keep some headroom
                    {
                        if (totalChars + fieldText.Length > maxTotalChars || fieldCount >= 24)
                        {
                            truncated = true;
                            break;
                        }

                        Flush();
                    }

                    if (fieldText.Length > 0)
                        fieldText.Append('\n');
                    fieldText.Append(line);
                }

                if (fieldText.Length > 0 && fieldCount < 25 && totalChars + fieldText.Length <= maxTotalChars)
                    Flush();
                else if (fieldText.Length > 0)
                    truncated = true;

                if (truncated)
                    embed.WithFooter("Parte das colunas foi omitida: a linha não cabe no limite de uma mensagem do Discord.");

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(embed));
            }
            catch (Exception ex)
            {
                var errEmbed = new DiscordEmbedBuilder()
                    .WithTitle("Erro ao buscar planilha")
                    .WithDescription("Não foi possível ler os dados da planilha. Verifique o URL e a disponibilidade da planilha.")
                    .AddField(new DiscordEmbedField("Detalhes", ex.Message, false))
                    .WithColor(DiscordColor.IndianRed);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(errEmbed));
            }
        }

    }
}