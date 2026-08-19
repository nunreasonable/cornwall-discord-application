using System;
using System.Collections.Generic;
using System.Linq;

namespace CornwallUtilities.Services.Audit
{
    /// <summary>
    /// Um degrau da escada de promoções do regimento.
    /// </summary>
    /// <param name="Name">Exatamente como o valor aparece em <see cref="AuditEntry.rank"/>.</param>
    /// <param name="EmojiName">Nome do emoji no servidor, resolvido em runtime.</param>
    /// <param name="RequiredBattles">Batalhas necessárias. -1 quando não há critério numérico.</param>
    /// <param name="NeedsApproval">Se há critério subjetivo além do número.</param>
    /// <param name="Criteria">Texto do critério, mostrado ao usuário.</param>
    internal sealed record PromotionRank(
        string Name,
        string EmojiName,
        int RequiredBattles,
        bool NeedsApproval,
        string Criteria);

    /// <summary>Situação de um jogador em relação à escada.</summary>
    internal enum PromotionStatus
    {
        /// <summary>Está no degrau que os números justificam. Nada a fazer.</summary>
        UpToDate,

        /// <summary>Alcançou um degrau acima do atual, e o critério é só numérico.</summary>
        Eligible,

        /// <summary>Alcançou o número, mas o degrau exige avaliação humana.</summary>
        EligibleNeedsApproval,

        /// <summary>Patente de NCO/oficial acima da escada: progressão não é calculada.</summary>
        OutsideLadder,

        /// <summary>Não tem batalhas suficientes nem para o primeiro degrau.</summary>
        BelowFirstRank
    }

    /// <summary>Resultado do cálculo para um jogador.</summary>
    internal sealed class PromotionResult
    {
        public string Username { get; init; } = string.Empty;
        public string CurrentRankName { get; init; } = string.Empty;
        public int Battles { get; init; }
        public PromotionStatus Status { get; init; }

        /// <summary>Degrau atual reconhecido na escada, ou null se fora dela.</summary>
        public PromotionRank? CurrentRank { get; init; }

        /// <summary>Degrau mais alto que as batalhas alcançam, quando é acima do atual.</summary>
        public PromotionRank? TargetRank { get; init; }

        /// <summary>Quantos degraus separam o atual do alvo. 0 quando não há promoção.</summary>
        public int StepsSkipped { get; init; }

        /// <summary>Próximo degrau ainda não alcançado, para mostrar o que falta.</summary>
        public PromotionRank? NextRank { get; init; }

        /// <summary>Batalhas faltando para <see cref="NextRank"/>. 0 quando não se aplica.</summary>
        public int BattlesToNext { get; init; }

        public bool IsPromotable =>
            Status is PromotionStatus.Eligible or PromotionStatus.EligibleNeedsApproval;
    }

    /// <summary>
    /// A escada de promoções do 12° Regimento.
    ///
    /// Deliberadamente sem nenhuma referência ao DisCatSharp: é lógica pura,
    /// o que permite testá-la fora do Discord contra o audit.json real.
    ///
    /// O que este código NÃO faz é decidir promoções. Do Kingsman para cima o
    /// critério tem uma parte humana ("ser um soldado capaz", "usar o cérebro",
    /// "competência ao nível de granadeiro"), e Lance Serjeant não tem número
    /// nenhum. Aqui só se responde elegibilidade; a promoção continua sendo
    /// decisão do comando do regimento.
    /// </summary>
    internal static class PromotionLadder
    {
        /// <summary>Da patente mais baixa para a mais alta. A ordem É a escada.</summary>
        public static readonly IReadOnlyList<PromotionRank> Ranks = new List<PromotionRank>
        {
            new("Fencible", "Fencible", 1, false,
                "Participar de 1 batalha e ficar nela do início ao fim."),

            new("Regular", "Regular", 3, false,
                "Participar de 3 batalhas e ficar nelas do início ao fim."),

            new("Private", "Private", 5, false,
                "Participar de 5 eventos por completo."),

            new("Kingsman", "Kingsman", 10, true,
                "Participar de 10+ eventos e ser um soldado capaz; dado aos melhores recrutas do regimento."),

            new("Lance Corporal", "LanceCorporal", 20, true,
                "Participar de 20 eventos com atividade consistente e usar o cérebro."),

            new("Corporal", "Corporal", 35, true,
                "Participar de 35 eventos com atividade consistente e competência ao nível de granadeiro."),

            // Sem critério numérico: entra na escada para poder ser exibida como
            // destino, mas nunca é calculada a partir das batalhas.
            new("Lance Serjeant", "lance_serjeant", -1, true,
                "Patente inicial de NCO, dada àqueles com contribuições significativas para o regimento.")
        };

        /// <summary>
        /// Patentes acima da escada. Quem está aqui não tem promoção calculada:
        /// a progressão de NCO/oficial não é por contagem de batalha.
        /// </summary>
        private static readonly HashSet<string> RanksAboveLadder = new(StringComparer.OrdinalIgnoreCase)
        {
            "Serjeant",
            "Colour Serjeant",
            "Quartermaster-Serjeant",
            "Quartermaster Serjeant",
            "Serjeant Major",
            "Ensign",
            "Lieutenant",
            "Captain",
            "Major",
            "Lieutenant Colonel",
            "Colonel"
        };

        /// <summary>Degraus com critério numérico, que é o que dá para calcular.</summary>
        private static IEnumerable<PromotionRank> NumericRanks =>
            Ranks.Where(r => r.RequiredBattles >= 0);

        /// <summary>Posição do degrau na escada, ou -1 se não for um degrau conhecido.</summary>
        private static int IndexOfRank(PromotionRank? rank)
        {
            if (rank is null)
                return -1;

            for (var i = 0; i < Ranks.Count; i++)
            {
                if (ReferenceEquals(Ranks[i], rank))
                    return i;
            }

            return -1;
        }

        public static PromotionRank? FindRank(string? rankName)
        {
            if (string.IsNullOrWhiteSpace(rankName))
                return null;

            var needle = rankName.Trim();
            return Ranks.FirstOrDefault(r => string.Equals(r.Name, needle, StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsAboveLadder(string? rankName) =>
            !string.IsNullOrWhiteSpace(rankName) && RanksAboveLadder.Contains(rankName.Trim());

        /// <summary>
        /// O degrau mais alto que essa quantidade de batalhas alcança, ou null se
        /// não chega nem ao primeiro.
        /// </summary>
        public static PromotionRank? HighestReached(int battles) =>
            NumericRanks.Where(r => battles >= r.RequiredBattles).LastOrDefault();

        /// <summary>
        /// O primeiro degrau numérico ainda não alcançado, para mostrar o que falta.
        /// Null quando já se alcançou o topo da parte calculável.
        /// </summary>
        public static PromotionRank? NextUnreached(int battles) =>
            NumericRanks.FirstOrDefault(r => battles < r.RequiredBattles);

        /// <summary>
        /// Calcula a situação de um jogador.
        ///
        /// O alvo é o degrau MAIS ALTO que as batalhas alcançam, não o
        /// imediatamente seguinte: alguém marcado como Fencible com 40 batalhas
        /// está elegível a Corporal, e responder "Regular" seria inútil numa
        /// rodada de promoções.
        /// </summary>
        public static PromotionResult Evaluate(string username, string? currentRankName, int battles)
        {
            var rankName = (currentRankName ?? string.Empty).Trim();

            if (IsAboveLadder(rankName))
            {
                return new PromotionResult
                {
                    Username = username,
                    CurrentRankName = rankName,
                    Battles = battles,
                    Status = PromotionStatus.OutsideLadder
                };
            }

            var current = FindRank(rankName);
            var reached = HighestReached(battles);
            var next = NextUnreached(battles);

            // Lance Serjeant é o topo da escada e não tem número: quem já está
            // nela não tem para onde subir por cálculo.
            if (current is not null && current.RequiredBattles < 0)
            {
                return new PromotionResult
                {
                    Username = username,
                    CurrentRankName = rankName,
                    Battles = battles,
                    Status = PromotionStatus.UpToDate,
                    CurrentRank = current
                };
            }

            if (reached is null)
            {
                // Nem o primeiro degrau. Inclui rank vazio e "Recruit", que
                // existem no arquivo real.
                return new PromotionResult
                {
                    Username = username,
                    CurrentRankName = rankName,
                    Battles = battles,
                    Status = PromotionStatus.BelowFirstRank,
                    CurrentRank = current,
                    NextRank = next,
                    BattlesToNext = next is null ? 0 : Math.Max(0, next.RequiredBattles - battles)
                };
            }

            var currentIndex = IndexOfRank(current);
            var reachedIndex = IndexOfRank(reached);

            if (reachedIndex <= currentIndex)
            {
                return new PromotionResult
                {
                    Username = username,
                    CurrentRankName = rankName,
                    Battles = battles,
                    Status = PromotionStatus.UpToDate,
                    CurrentRank = current,
                    NextRank = next,
                    BattlesToNext = next is null ? 0 : Math.Max(0, next.RequiredBattles - battles)
                };
            }

            return new PromotionResult
            {
                Username = username,
                CurrentRankName = rankName,
                Battles = battles,
                Status = reached.NeedsApproval
                    ? PromotionStatus.EligibleNeedsApproval
                    : PromotionStatus.Eligible,
                CurrentRank = current,
                TargetRank = reached,
                StepsSkipped = reachedIndex - currentIndex,
                NextRank = next,
                BattlesToNext = next is null ? 0 : Math.Max(0, next.RequiredBattles - battles)
            };
        }

        public static PromotionResult Evaluate(AuditEntry entry) =>
            Evaluate(entry.username, entry.rank, entry.battles);
    }
}
