using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using CornwallUtilities.config;
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
        public async Task CheckSpreadsheetInfoCommand(InteractionContext ctx, [Option("username", "Seu nome de usuário na planilha (coluna de identificação).")] string username)
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
                using var http = new HttpClient();
                var csvText = await http.GetStringAsync(url);

                var lines = csvText
                    .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                    .ToList();

                if (lines.Count == 0)
                {
                    throw new Exception("A planilha retornou um CSV vazio.");
                }

                var headers = ParseCsvLine(lines[0]);

                // Row 4 (index 3) contains the display labels for columns; data starts below it
                const int labelRowIndex = 3;
                var labels = lines.Count > labelRowIndex
                    ? ParseCsvLine(lines[labelRowIndex])
                    : headers;

                var rows = lines.Skip(labelRowIndex + 1)
                    .Select(ParseCsvLine)
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
                    .WithDescription($"Dados encontrados para **{username}** (coluna D: **{usernameColumnName}**).")
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

                // Split into multiple fields so we don't exceed Discord limits (1024 chars per field, max 25 fields)
                var fieldText = new StringBuilder();
                var fieldCount = 0;
                foreach (var line in columnLines)
                {
                    if (fieldText.Length + line.Length + 1 > 900) // keep some headroom
                    {
                        embed.AddField(new DiscordEmbedField($"Dados (parte {fieldCount + 1})", fieldText.ToString(), false));
                        fieldText.Clear();
                        fieldCount++;

                        if (fieldCount >= 24) // keep one field for footer or summary if needed
                            break;
                    }

                    if (fieldText.Length > 0)
                        fieldText.Append('\n');
                    fieldText.Append(line);
                }

                if (fieldText.Length > 0 && fieldCount < 25)
                {
                    embed.AddField(new DiscordEmbedField($"Dados (parte {fieldCount + 1})", fieldText.ToString(), false));
                }

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

        private static List<string> ParseCsvLine(string line)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(line))
                return result;

            var sb = new StringBuilder();
            var inQuotes = false;

            for (var i = 0; i < line.Length; i++)
            {
                var ch = line[i];

                if (ch == '"')
                {
                    // If this is a double quote inside a quoted field, consume it and add a quote
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                        continue;
                    }

                    inQuotes = !inQuotes;
                    continue;
                }

                if (ch == ',' && !inQuotes)
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                    continue;
                }

                sb.Append(ch);
            }

            result.Add(sb.ToString());
            return result;
        }
    }
}