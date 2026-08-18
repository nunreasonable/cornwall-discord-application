using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CornwallUtilities.Services.Audit
{
    internal readonly record struct ParsedLine(string Username, int Kills, int Deaths, int Assists);

    internal sealed class ParseResult
    {
        public List<ParsedLine> Entries { get; } = new();
        public List<(int LineNumber, string Raw, string Reason)> Failures { get; } = new();
        public List<string> Duplicates { get; } = new();
        public List<string> Warnings { get; } = new();
    }

    /// <summary>
    /// Le o bloco colado no /audit-add. Cada linha e "nome k d a n", onde n e
    /// apenas um token de formatacao e e descartado.
    /// </summary>
    internal static class AuditParser
    {
        private const int MaxUsernameLength = 32;

        public static ParseResult Parse(string? block)
        {
            var result = new ParseResult();
            if (string.IsNullOrWhiteSpace(block))
                return result;

            // Preserva a ordem e a primeira grafia vista do nome.
            var byName = new Dictionary<string, ParsedLine>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();

            var lines = block.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);

            for (var i = 0; i < lines.Length; i++)
            {
                var lineNumber = i + 1;
                var raw = lines[i].Trim();

                if (raw.Length == 0)
                    continue;

                var tokens = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

                // Le da DIREITA para a esquerda: os ultimos tokens sao os numeros e
                // tudo que vem antes e o nome. Isso mantem nomes com espaco
                // ("Rafa Odebrecht 12 3 5 0") intactos.
                int numericCount;
                if (tokens.Length >= 5 && IsNumber(tokens[^1]) && IsNumber(tokens[^2]) && IsNumber(tokens[^3]) && IsNumber(tokens[^4]))
                {
                    numericCount = 4; // nome k d a n
                }
                else if (tokens.Length >= 4 && IsNumber(tokens[^1]) && IsNumber(tokens[^2]) && IsNumber(tokens[^3]))
                {
                    numericCount = 3; // nome k d a  (sem o token n)
                    result.Warnings.Add($"L{lineNumber}: sem o campo final `n`, assumido como ausente.");
                }
                else
                {
                    result.Failures.Add((lineNumber, raw, "esperado `nome k d a n` com numeros no final"));
                    continue;
                }

                var name = string.Join(' ', tokens[..^numericCount]).Trim();

                // Quando ha 4 numeros, o ultimo e o `n` de formatacao: descartado.
                var stats = tokens[^numericCount..];
                var kills = ParseInt(stats[0]);
                var deaths = ParseInt(stats[1]);
                var assists = ParseInt(stats[2]);

                if (name.Length == 0)
                {
                    result.Failures.Add((lineNumber, raw, "nome vazio"));
                    continue;
                }

                if (name.Length > MaxUsernameLength)
                {
                    result.Failures.Add((lineNumber, raw, $"nome com mais de {MaxUsernameLength} caracteres"));
                    continue;
                }

                if (name.Contains(','))
                {
                    result.Failures.Add((lineNumber, raw, "nome nao pode conter virgula"));
                    continue;
                }

                if (IsNumber(name))
                {
                    result.Failures.Add((lineNumber, raw, "nome nao pode ser apenas numeros"));
                    continue;
                }

                if (kills < 0 || deaths < 0 || assists < 0)
                {
                    result.Failures.Add((lineNumber, raw, "valores negativos nao sao aceitos"));
                    continue;
                }

                if (byName.TryGetValue(name, out var existing))
                {
                    // Mesmo jogador duas vezes no mesmo lote: soma K/D/A, mas
                    // continua valendo +1 batalha (uma batalha, um lote).
                    byName[name] = existing with
                    {
                        Kills = existing.Kills + kills,
                        Deaths = existing.Deaths + deaths,
                        Assists = existing.Assists + assists
                    };
                    if (!result.Duplicates.Contains(existing.Username, StringComparer.OrdinalIgnoreCase))
                        result.Duplicates.Add(existing.Username);
                }
                else
                {
                    byName[name] = new ParsedLine(name, kills, deaths, assists);
                    order.Add(name);
                }
            }

            result.Entries.AddRange(order.Select(n => byName[n]));
            return result;
        }

        private static bool IsNumber(string token) =>
            int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

        private static int ParseInt(string token) =>
            int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : -1;
    }
}
