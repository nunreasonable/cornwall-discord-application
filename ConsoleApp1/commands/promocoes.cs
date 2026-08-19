using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CornwallUtilities.Services;
using CornwallUtilities.Services.Audit;
using DisCatSharp.ApplicationCommands;
using DisCatSharp.ApplicationCommands.Attributes;
using DisCatSharp.ApplicationCommands.Context;
using DisCatSharp.Entities;
using DisCatSharp.Enums;
using DisCatSharp.Interactivity.Entities;
using DisCatSharp.Interactivity.Extensions;

namespace CornwallUtilities.commands
{
    internal class Promocoes : ApplicationCommandsModule
    {
        private const int MaxLinesPerPage = 20;

        [SlashCommand("promocoes", "Calcula quem está elegível a promoção segundo a escada do regimento")]
        public async Task PromocoesCommand(
            InteractionContext ctx,
            [Autocomplete(typeof(AuditUsernameAutocompleteProvider))]
            [Option("jogador", "Ver apenas um jogador", true)] string? jogador = null,
            [Option("privado", "Mostrar apenas para você")] bool privado = false)
        {
            var deferBuilder = new DiscordInteractionResponseBuilder();
            if (privado)
                deferBuilder.AsEphemeral();

            await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource, deferBuilder);

            var audit = await AuditStore.Instance.ReadAuditAsync();

            if (audit.entries.Count == 0)
            {
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(new DiscordEmbedBuilder()
                    .WithTitle("Auditoria vazia")
                    .WithDescription("Nenhum jogador registrado ainda. Use `/audit-add` primeiro.")
                    .WithColor(DiscordColor.Orange)));
                return;
            }

            if (!string.IsNullOrWhiteSpace(jogador))
            {
                await ShowSinglePlayerAsync(ctx, audit, jogador!);
                return;
            }

            await ShowRosterAsync(ctx, audit, privado);
        }

        // ---------------------------------------------------------------- emojis

        /// <summary>
        /// Resolve o emoji da patente pelo NOME, na lista do servidor.
        ///
        /// O formato ":Fencible~1:" e o que o cliente do Discord mostra quando ha
        /// nomes repetidos entre servidores; enviado por um bot sairia como texto
        /// literal. Resolver por nome evita ID chumbado no codigo e sobrevive a um
        /// emoji recriado - se nao achar, devolve vazio e a linha fica so com o
        /// nome da patente, sem quebrar nada.
        /// </summary>
        private static string Emoji(DiscordGuild? guild, PromotionRank? rank)
        {
            if (guild is null || rank is null)
                return string.Empty;

            var emoji = guild.Emojis.Values
                .FirstOrDefault(e => string.Equals(e.Name, rank.EmojiName, StringComparison.OrdinalIgnoreCase));

            if (emoji is null)
                return string.Empty;

            return emoji.IsAnimated ? $"<a:{emoji.Name}:{emoji.Id}> " : $"<:{emoji.Name}:{emoji.Id}> ";
        }

        private static string RankLabel(DiscordGuild? guild, PromotionRank? rank, string fallback) =>
            rank is null
                ? (string.IsNullOrWhiteSpace(fallback) ? "*sem cargo*" : fallback)
                : $"{Emoji(guild, rank)}**{rank.Name}**";

        // ------------------------------------------------------------ individual

        private static async Task ShowSinglePlayerAsync(InteractionContext ctx, AuditFile audit, string jogador)
        {
            var entry = audit.entries.FirstOrDefault(e =>
                string.Equals(e.username, jogador, StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                // Mesmo tratamento de /audit-check: sugerir nomes parecidos em vez
                // de so dizer que nao achou.
                var suggestions = audit.entries
                    .Where(e => e.username.Contains(jogador, StringComparison.OrdinalIgnoreCase))
                    .Select(e => e.username)
                    .Take(10)
                    .ToList();

                var notFound = new DiscordEmbedBuilder()
                    .WithTitle("Jogador não encontrado")
                    .WithDescription($"**{AuditEmbeds.Trim(jogador, 60)}** não está na auditoria.")
                    .WithColor(DiscordColor.IndianRed);

                if (suggestions.Count > 0)
                    notFound.AddField(new DiscordEmbedField("Você quis dizer", AuditEmbeds.FieldValue(suggestions), false));

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(notFound));
                return;
            }

            var result = PromotionLadder.Evaluate(entry);
            var guild = ctx.Guild;

            var embed = new DiscordEmbedBuilder()
                .WithTitle(entry.username)
                .WithColor(ColorFor(result.Status))
                .WithTimestamp(audit.lastUpdatedUtc == default ? DateTimeOffset.UtcNow : audit.lastUpdatedUtc)
                .AddField(new DiscordEmbedField(
                    "Patente atual",
                    RankLabel(guild, result.CurrentRank, result.CurrentRankName),
                    true))
                .AddField(new DiscordEmbedField("Eventos", entry.battles.ToString(), true));

            switch (result.Status)
            {
                case PromotionStatus.OutsideLadder:
                    embed.WithDescription(
                        "Patente de NCO/oficial: a progressão não é calculada por contagem de eventos.");
                    break;

                case PromotionStatus.BelowFirstRank:
                    embed.WithDescription(
                        $"Ainda não alcançou o primeiro degrau da escada.");
                    AddNextField(embed, guild, result);
                    break;

                case PromotionStatus.Eligible:
                    embed.WithDescription(
                        $"✅ Elegível a {RankLabel(guild, result.TargetRank, "")}{StepsNote(result)}\n" +
                        $"O critério é apenas numérico e já está cumprido.");
                    embed.AddField(new DiscordEmbedField("Critério", result.TargetRank!.Criteria, false));
                    AddNextField(embed, guild, result);
                    break;

                case PromotionStatus.EligibleNeedsApproval:
                    embed.WithDescription(
                        $"🟡 Elegível a {RankLabel(guild, result.TargetRank, "")}{StepsNote(result)} — **requer aprovação**\n" +
                        $"A contagem de eventos está cumprida, mas esta patente também depende de avaliação do comando.");
                    embed.AddField(new DiscordEmbedField("Critério", result.TargetRank!.Criteria, false));
                    AddNextField(embed, guild, result);
                    break;

                case PromotionStatus.UpToDate:
                default:
                    embed.WithDescription("Está no degrau que a contagem de eventos justifica.");
                    AddNextField(embed, guild, result);
                    break;
            }

            await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(AuditEmbeds.Fit(embed)));
        }

        private static void AddNextField(DiscordEmbedBuilder embed, DiscordGuild? guild, PromotionResult result)
        {
            if (result.NextRank is null)
            {
                embed.AddField(new DiscordEmbedField(
                    "Próximo degrau",
                    $"Chegou ao topo da parte calculável da escada. " +
                    $"{Emoji(guild, PromotionLadder.FindRank("Lance Serjeant"))}**Lance Serjeant** é concedida manualmente.",
                    false));
                return;
            }

            embed.AddField(new DiscordEmbedField(
                "Próximo degrau",
                $"{RankLabel(guild, result.NextRank, "")} — faltam **{result.BattlesToNext}** evento(s)\n" +
                $"*{result.NextRank.Criteria}*",
                false));
        }

        private static string StepsNote(PromotionResult result) =>
            result.StepsSkipped > 1 ? $" *(salta {result.StepsSkipped} degraus)*" : string.Empty;

        private static DiscordColor ColorFor(PromotionStatus status) => status switch
        {
            PromotionStatus.Eligible => DiscordColor.SpringGreen,
            PromotionStatus.EligibleNeedsApproval => DiscordColor.Gold,
            PromotionStatus.OutsideLadder => DiscordColor.Gray,
            PromotionStatus.BelowFirstRank => DiscordColor.Orange,
            _ => DiscordColor.Blurple
        };

        // ---------------------------------------------------------------- roster

        private static async Task ShowRosterAsync(InteractionContext ctx, AuditFile audit, bool privado)
        {
            var guild = ctx.Guild;

            var results = audit.entries
                .Select(PromotionLadder.Evaluate)
                .Where(r => r.IsPromotable)
                // Mais alto primeiro, e dentro do mesmo alvo quem tem mais eventos.
                .OrderByDescending(r => r.TargetRank!.RequiredBattles)
                .ThenByDescending(r => r.Battles)
                .ThenBy(r => r.Username, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var totalAvaliados = audit.entries.Count;

            if (results.Count == 0)
            {
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(new DiscordEmbedBuilder()
                    .WithTitle("Nenhuma promoção pendente")
                    .WithDescription($"Os {totalAvaliados} jogadores da auditoria estão no degrau que a contagem de eventos justifica.")
                    .WithColor(DiscordColor.SpringGreen)));
                return;
            }

            var automaticas = results.Where(r => r.Status == PromotionStatus.Eligible).ToList();
            var comAprovacao = results.Where(r => r.Status == PromotionStatus.EligibleNeedsApproval).ToList();

            var pages = BuildPages(guild, automaticas, comAprovacao, totalAvaliados);

            if (pages.Count == 1)
            {
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(pages[0]));
                return;
            }

            var paginated = pages.Select(p => new Page(string.Empty, p)).ToList();

            // Mesmo cuidado de /audit-check: registrar o dono permite ao handler de
            // componentes explicar a quem clicar sem ser dono, em vez de o Discord
            // mostrar so "interacao falhou".
            var original = await ctx.GetOriginalResponseAsync();
            PaginationOwnership.Register(original.Id, ctx.User.Id);

            try
            {
                await ctx.Interaction.SendPaginatedResponseAsync(true, privado, ctx.User, paginated);
            }
            finally
            {
                PaginationOwnership.Unregister(original.Id);
            }
        }

        private static List<DiscordEmbedBuilder> BuildPages(
            DiscordGuild? guild,
            List<PromotionResult> automaticas,
            List<PromotionResult> comAprovacao,
            int totalAvaliados)
        {
            var blocks = new List<(string Header, List<PromotionResult> Items)>();

            if (automaticas.Count > 0)
                blocks.Add(("✅ Critério numérico cumprido", automaticas));

            if (comAprovacao.Count > 0)
                blocks.Add(("🟡 Elegíveis — requerem aprovação do comando", comAprovacao));

            var pages = new List<DiscordEmbedBuilder>();
            var lines = new List<string>();
            string? currentHeader = null;

            void Flush()
            {
                if (lines.Count == 0)
                    return;

                var embed = new DiscordEmbedBuilder()
                    .WithTitle("Promoções pendentes")
                    .WithDescription(string.Join("\n", lines))
                    .WithColor(DiscordColor.Gold)
                    .WithFooter($"{automaticas.Count + comAprovacao.Count} de {totalAvaliados} jogadores · página {pages.Count + 1}")
                    .WithTimestamp(DateTimeOffset.UtcNow);

                pages.Add(AuditEmbeds.Fit(embed));
                lines.Clear();
            }

            foreach (var (header, items) in blocks)
            {
                currentHeader = header;
                lines.Add($"__{header}__");

                var used = 0;
                foreach (var r in items)
                {
                    lines.Add(FormatLine(guild, r));
                    used++;

                    if (used % MaxLinesPerPage == 0 && used < items.Count)
                    {
                        Flush();
                        // A pagina seguinte repete o cabecalho para nao perder o
                        // contexto de qual bloco esta sendo listado.
                        lines.Add($"__{currentHeader} (continuação)__");
                    }
                }

                lines.Add(string.Empty);
            }

            Flush();
            return pages;
        }

        private static string FormatLine(DiscordGuild? guild, PromotionResult r)
        {
            var atual = r.CurrentRank is null
                ? (string.IsNullOrWhiteSpace(r.CurrentRankName) ? "*sem cargo*" : AuditEmbeds.Trim(r.CurrentRankName, 20))
                : r.CurrentRank.Name;

            var salto = r.StepsSkipped > 1 ? $" *(+{r.StepsSkipped})*" : string.Empty;

            return $"**{AuditEmbeds.Trim(r.Username, 24)}** · {r.Battles} ev. · {atual} → " +
                   $"{Emoji(guild, r.TargetRank)}{r.TargetRank!.Name}{salto}";
        }
    }
}
