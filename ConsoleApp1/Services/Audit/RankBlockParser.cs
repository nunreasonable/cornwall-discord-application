using System;
using System.Collections.Generic;

namespace CornwallUtilities.Services.Audit
{
    internal readonly record struct ParsedRank(string Username, string Rank);

    internal sealed class RankParseResult
    {
        /// <summary>Linhas validas, na ordem em que aparecem no bloco.</summary>
        public List<ParsedRank> Entries { get; } = new();

        /// <summary>Linhas que nao deram para ler, com o motivo, para mostrar de volta.</summary>
        public List<(int LineNumber, string Raw, string Reason)> Failures { get; } = new();

        /// <summary>Nome repetido no bloco: vale a ultima linha, e o aviso sobe.</summary>
        public List<string> Duplicates { get; } = new();
    }

    /// <summary>
    /// Le o bloco do modal do /audit-setranks. Cada linha e "nome = cargo".
    ///
    /// Existe separado do <see cref="AuditParser"/> porque a forma e outra:
    /// aquele quebra a linha em tokens por espaco, e aqui o cargo e texto livre
    /// que quase sempre TEM espaco ("Lance Serjeant"). O separador e o primeiro
    /// "=" da linha, entao um "=" dentro do cargo continua fazendo parte dele.
    ///
    /// Lado direito vazio nao e erro: e como se remove o cargo de alguem, igual
    /// ao que o campo em branco ja fazia no modal antigo.
    /// </summary>
    internal static class RankBlockParser
    {
        /// <summary>Mesmo teto do /audit-edit para nome de jogador.</summary>
        private const int MaxUsernameLength = 32;

        /// <summary>Mesmo teto que o campo de cargo do modal antigo aceitava.</summary>
        public const int MaxRankLength = 40;

        public static RankParseResult Parse(string? block)
        {
            var result = new RankParseResult();
            if (string.IsNullOrWhiteSpace(block))
                return result;

            // Preserva a ordem do bloco e a posicao da primeira aparicao do nome:
            // reescrever a mesma pessoa duas vezes nao pode duplicar a linha no
            // resumo nem gravar duas vezes.
            var position = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            var lines = block.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);

            for (var i = 0; i < lines.Length; i++)
            {
                var lineNumber = i + 1;
                var raw = lines[i].Trim();

                if (raw.Length == 0)
                    continue;

                var separator = raw.IndexOf('=');
                if (separator < 0)
                {
                    result.Failures.Add((lineNumber, raw, "Falta o \"=\" separando o nome do cargo."));
                    continue;
                }

                var username = raw[..separator].Trim();
                var rank = raw[(separator + 1)..].Trim();

                if (username.Length == 0)
                {
                    result.Failures.Add((lineNumber, raw, "Nome do jogador vazio antes do \"=\"."));
                    continue;
                }

                if (username.Length > MaxUsernameLength)
                {
                    result.Failures.Add((lineNumber, raw, $"Nome passa de {MaxUsernameLength} caracteres."));
                    continue;
                }

                if (rank.Length > MaxRankLength)
                {
                    result.Failures.Add((lineNumber, raw, $"Cargo passa de {MaxRankLength} caracteres."));
                    continue;
                }

                if (position.TryGetValue(username, out var existing))
                {
                    // A ultima linha vence, que e o que quem digitou espera ao
                    // corrigir uma linha logo abaixo da outra.
                    result.Entries[existing] = new ParsedRank(result.Entries[existing].Username, rank);
                    result.Duplicates.Add(username);
                    continue;
                }

                position[username] = result.Entries.Count;
                result.Entries.Add(new ParsedRank(username, rank));
            }

            return result;
        }

        /// <summary>
        /// Monta o bloco que vai pre-preenchido no modal: uma linha por jogador
        /// da cesta, ja com o cargo atual, para editar o lado direito.
        /// </summary>
        public static string Build(IEnumerable<(string Username, string? Rank)> players)
        {
            var lines = new List<string>();
            foreach (var (username, rank) in players)
                lines.Add($"{username} = {rank ?? string.Empty}".TrimEnd());

            return string.Join("\n", lines);
        }
    }
}
