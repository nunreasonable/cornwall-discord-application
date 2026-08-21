using System;
using System.Collections.Generic;
using System.Globalization;
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
        /// <summary>
        /// Cabecalhos que identificam a coluna do nome de usuario.
        ///
        /// A comparacao e feita sobre o texto normalizado (sem acento, maiusculo,
        /// espacos colapsados) porque as abas nao escrevem igual: as companhias
        /// usam "NOME DE USUARIO" e PROMOCOES usa "NOME DO USUARIO".
        /// </summary>
        private static readonly string[] s_usernameHeaders =
        {
            "NOME DE USUARIO",
            "NOME DO USUARIO",
            "USERNAME"
        };

        /// <summary>Cabecalhos da aba de promocoes, para montar a linha "antiga -> nova".</summary>
        private const string OldRankHeader = "PATENTE ANTIGA";
        private const string NewRankHeader = "PROMOCAO";

        [SlashCommand("checkspreadsheetinfo", "Verifica as informações da planilha Regimental")]
        public async Task CheckSpreadsheetInfoCommand(InteractionContext ctx, [Option("username", "Seu nome de usuário na planilha.")] string username)
        {
            // Defer response while we fetch the spreadsheet and prepare the embed
            await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);

            var config = new JSONReader();
            await config.ReadJSON();

            var tabs = (config.spreadsheetInfoTabs ?? Array.Empty<SpreadsheetInfoTab>())
                .Where(t => !string.IsNullOrWhiteSpace(t?.csvUrl))
                .ToList();

            if (tabs.Count == 0)
            {
                var errEmbed = new DiscordEmbedBuilder()
                    .WithTitle("Erro ao buscar planilha")
                    .WithDescription("Nenhuma aba da planilha esta configurada. Preencha `spreadsheetInfoTabs` no config.json.")
                    .WithColor(DiscordColor.IndianRed);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(errEmbed));
                return;
            }

            // As abas sao independentes, entao vao juntas em vez de uma de cada
            // vez: com quatro delas, sequencial somaria quatro idas ao Google
            // dentro do prazo da interacao.
            var results = await Task.WhenAll(tabs.Select(tab => LoadTabAsync(tab, username)));

            var failures = results.Where(r => r.Error != null).ToList();
            var matches = results.Where(r => r.Error == null && r.Row != null).ToList();

            if (matches.Count == 0)
            {
                // Um "nao encontrado" so vale se todas as abas foram lidas. Se
                // alguma falhou, o jogador pode estar exatamente nela, e afirmar
                // que ele nao existe seria mentira.
                var allFailed = failures.Count == tabs.Count;
                var embed = new DiscordEmbedBuilder()
                    .WithTitle(allFailed ? "Erro ao buscar planilha" : "Usuário não encontrado")
                    .WithColor(DiscordColor.IndianRed);

                embed.WithDescription(allFailed
                    ? "Não foi possível ler nenhuma aba da planilha. Verifique o URL e a disponibilidade dela."
                    : failures.Count > 0
                        ? $"Nenhum registro para **{username}** nas abas lidas — mas {failures.Count} aba(s) falharam, então ele ainda pode estar numa delas."
                        : $"Nenhum registro para **{username}** foi encontrado na planilha.");

                var searched = string.Join(", ", results.Where(r => r.Error == null).Select(r => r.Tab.name));
                if (!string.IsNullOrWhiteSpace(searched))
                    embed.AddField(new DiscordEmbedField("Abas pesquisadas", searched, false));

                foreach (var failure in failures.Take(4))
                    embed.AddField(new DiscordEmbedField($"Falha em {failure.Tab.name}", failure.Error!, false));

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(embed));
                return;
            }

            await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(BuildEmbed(username, matches, failures)));
        }

        /// <summary>
        /// Baixa uma aba e procura o jogador nela. Nunca lanca: a falha volta em
        /// <see cref="TabResult.Error"/> para que uma aba fora do ar nao derrube a
        /// resposta inteira.
        /// </summary>
        private static async Task<TabResult> LoadTabAsync(SpreadsheetInfoTab tab, string username)
        {
            try
            {
                // Prazo explicito no lugar do teto silencioso de 20s do cliente
                // compartilhado: a exportacao da planilha pode demorar.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var csvText = await HttpClientProvider.LongRunning.GetStringAsync(tab.csvUrl, cts.Token);

                // ParseCsv le o documento inteiro: quebrar por linha antes corrompia
                // os registros cujos campos entre aspas contem quebra de linha.
                var records = CsvUtil.ParseCsv(csvText);
                if (records.Count == 0)
                    return TabResult.Failed(tab, "A aba retornou um CSV vazio.");

                if (!TryFindHeader(records, out var headerRowIndex, out var usernameColumnIndex))
                    return TabResult.Failed(tab, "Não achei a coluna do nome de usuário nesta aba.");

                var labels = records[headerRowIndex];

                var row = records
                    .Skip(headerRowIndex + 1)
                    .FirstOrDefault(r => usernameColumnIndex < r.Count &&
                                         string.Equals(r[usernameColumnIndex].Trim(), username.Trim(),
                                                       StringComparison.OrdinalIgnoreCase));

                return new TabResult
                {
                    Tab = tab,
                    Labels = labels,
                    Row = row,
                    UsernameColumnIndex = usernameColumnIndex
                };
            }
            catch (Exception ex)
            {
                return TabResult.Failed(tab, ex.Message);
            }
        }

        /// <summary>
        /// Acha a linha de cabecalho e a coluna do nome de usuario.
        ///
        /// Antes ambos eram constantes - linha 4, coluna D - herdadas da planilha
        /// antiga. A planilha atual poe o cabecalho na primeira linha, e PROMOCOES
        /// so comeca na coluna K, com dez colunas vazias a esquerda: qualquer
        /// indice fixo erra em pelo menos uma das abas. Varrer ate achar o rotulo
        /// tambem sobrevive a alguem inserir uma coluna na planilha.
        /// </summary>
        private static bool TryFindHeader(List<List<string>> records, out int headerRowIndex, out int usernameColumnIndex)
        {
            // Poucas linhas: um cabecalho que nao esteja no topo do arquivo esta
            // logo abaixo dele, e varrer a planilha toda so acharia falso positivo.
            var limit = Math.Min(records.Count, 10);

            for (var r = 0; r < limit; r++)
            {
                for (var col = 0; col < records[r].Count; col++)
                {
                    if (s_usernameHeaders.Contains(Normalize(records[r][col])))
                    {
                        headerRowIndex = r;
                        usernameColumnIndex = col;
                        return true;
                    }
                }
            }

            headerRowIndex = -1;
            usernameColumnIndex = -1;
            return false;
        }

        /// <summary>Maiusculo, sem acento e com espacos colapsados, para comparar cabecalho.</summary>
        private static string Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var decomposed = value.Trim().ToUpperInvariant().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            var lastWasSpace = false;

            foreach (var ch in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                    continue;

                if (char.IsWhiteSpace(ch))
                {
                    if (!lastWasSpace && sb.Length > 0)
                        sb.Append(' ');
                    lastWasSpace = true;
                    continue;
                }

                lastWasSpace = false;
                sb.Append(ch);
            }

            return sb.ToString().TrimEnd().Normalize(NormalizationForm.FormC);
        }

        private static DiscordEmbedBuilder BuildEmbed(string username, List<TabResult> matches, List<TabResult> failures)
        {
            var personnel = matches.Where(m => !m.Tab.promotions).ToList();
            var promotions = matches.Where(m => m.Tab.promotions).ToList();

            var foundIn = string.Join(", ", matches.Select(m => m.Tab.name));
            var embed = new DiscordEmbedBuilder()
                .WithTitle("Informações da planilha")
                .WithDescription($"Dados encontrados para **{username}** em {foundIn}.")
                .WithColor(DiscordColor.Blurple)
                .WithTimestamp(DateTimeOffset.UtcNow);

            var budget = new EmbedBudget(embed);

            foreach (var match in personnel)
            {
                // A propria coluna do nome sai: ela repete o que o titulo do
                // campo ja diz e gastaria uma linha do limite do Discord.
                var lines = DescribeRow(match, skipUsernameColumn: true);
                if (lines.Count > 0)
                    budget.AddSection(match.Tab.name!, lines);
            }

            foreach (var match in promotions)
            {
                var lines = DescribePromotion(match);
                if (lines.Count > 0)
                    budget.AddSection("Promoção", lines);
            }

            foreach (var failure in failures.Take(2))
                budget.AddSection($"Falha em {failure.Tab.name}", new List<string> { failure.Error! });

            if (budget.Truncated)
                embed.WithFooter("Parte das colunas foi omitida: não cabe no limite de uma mensagem do Discord.");

            return embed;
        }

        /// <summary>Uma linha "rotulo: valor" por coluna preenchida.</summary>
        private static List<string> DescribeRow(TabResult match, bool skipUsernameColumn)
        {
            var lines = new List<string>();
            var labels = match.Labels ?? new List<string>();
            var row = match.Row!;
            var maxColumns = Math.Max(labels.Count, row.Count);

            for (var i = 0; i < maxColumns; i++)
            {
                if (skipUsernameColumn && i == match.UsernameColumnIndex)
                    continue;

                var label = i < labels.Count ? labels[i].Trim() : string.Empty;
                var value = i < row.Count ? row[i].Trim() : string.Empty;

                // Coluna sem rotulo E sem valor e so o preenchimento a esquerda que
                // PROMOCOES tem: nao vira linha nenhuma.
                if (string.IsNullOrWhiteSpace(label) && string.IsNullOrWhiteSpace(value))
                    continue;

                if (string.IsNullOrWhiteSpace(label))
                    label = $"Coluna {i + 1}";

                lines.Add($"**{label}**: {(value.Length > 0 ? value : "(vazio)")}");
            }

            return lines;
        }

        /// <summary>
        /// A aba de promocoes vira uma linha "antiga -> nova", que e o que se quer
        /// ler de relance, com as demais colunas listadas abaixo.
        /// </summary>
        private static List<string> DescribePromotion(TabResult match)
        {
            var labels = match.Labels ?? new List<string>();
            var row = match.Row!;

            string ValueOf(string header)
            {
                var index = labels.FindIndex(l => Normalize(l) == header);
                return index >= 0 && index < row.Count ? row[index].Trim() : string.Empty;
            }

            var oldRank = ValueOf(OldRankHeader);
            var newRank = ValueOf(NewRankHeader);

            var lines = new List<string>();
            if (oldRank.Length > 0 || newRank.Length > 0)
            {
                lines.Add($"**{(oldRank.Length > 0 ? oldRank : "?")}** → **{(newRank.Length > 0 ? newRank : "?")}**");
            }

            // O resto da linha entra igual as abas de companhia, menos as duas
            // colunas que a seta acima ja resumiu.
            foreach (var line in DescribeRow(match, skipUsernameColumn: true))
            {
                var isRankLine = line.StartsWith($"**{OldRankHeader}", StringComparison.OrdinalIgnoreCase);
                if (isRankLine)
                    continue;

                var labelEnd = line.IndexOf("**:", StringComparison.Ordinal);
                if (labelEnd > 2)
                {
                    var label = Normalize(line.Substring(2, labelEnd - 2));
                    if (label == OldRankHeader || label == NewRankHeader)
                        continue;
                }

                lines.Add(line);
            }

            return lines;
        }

        /// <summary>
        /// Monta os fields respeitando os DOIS limites do Discord: 1024 caracteres
        /// por field e 6000 no embed inteiro. So o primeiro era conferido, entao
        /// uma linha com muitas colunas podia montar 24 fields e levar BadRequest
        /// na resposta toda.
        /// </summary>
        private sealed class EmbedBudget
        {
            private const int MaxTotalChars = 5500;
            private const int MaxFieldChars = 900; // folga sobre os 1024 do Discord
            private const int MaxFields = 24;

            private readonly DiscordEmbedBuilder _embed;
            private int _totalChars;
            private int _fieldCount;

            public EmbedBudget(DiscordEmbedBuilder embed)
            {
                _embed = embed;
                _totalChars = embed.Description?.Length ?? 0;
            }

            public bool Truncated { get; private set; }

            public void AddSection(string name, List<string> lines)
            {
                if (string.IsNullOrWhiteSpace(name))
                    name = "Planilha";

                var text = new StringBuilder();
                var part = 1;

                // Flush com o acumulador VAZIO nao pode virar field: o Discord
                // recusa field sem valor com BadRequest e leva junto a resposta
                // inteira. Era o que acontecia quando a primeira linha da secao
                // ja passava sozinha do teto do field - a condicao de corte
                // disparava antes de haver qualquer texto acumulado.
                void Flush()
                {
                    if (text.Length == 0)
                        return;

                    var fieldName = part == 1 ? name : $"{name} (parte {part})";
                    if (_fieldCount >= MaxFields || _totalChars + fieldName.Length + text.Length > MaxTotalChars)
                    {
                        Truncated = true;
                        text.Clear();
                        return;
                    }

                    _embed.AddField(new DiscordEmbedField(fieldName, text.ToString(), false));
                    _totalChars += fieldName.Length + text.Length;
                    _fieldCount++;
                    part++;
                    text.Clear();
                }

                foreach (var raw in lines)
                {
                    // Uma celula da planilha pode sozinha ser maior que um field
                    // inteiro (o limite do Discord e 1024). Sem o corte, ela ia
                    // para o field como estava e a resposta voltava BadRequest.
                    var line = raw.Length <= MaxFieldChars
                        ? raw
                        : raw.Substring(0, MaxFieldChars - 1) + "…";

                    if (text.Length + line.Length + 1 > MaxFieldChars)
                    {
                        Flush();
                        if (Truncated)
                            return;
                    }

                    if (text.Length > 0)
                        text.Append('\n');
                    text.Append(line);
                }

                Flush();
            }
        }

        private sealed class TabResult
        {
            public SpreadsheetInfoTab Tab { get; init; } = null!;
            public List<string>? Labels { get; init; }
            public List<string>? Row { get; init; }
            public int UsernameColumnIndex { get; init; } = -1;
            public string? Error { get; init; }

            public static TabResult Failed(SpreadsheetInfoTab tab, string error) =>
                new() { Tab = tab, Error = error };
        }
    }
}
