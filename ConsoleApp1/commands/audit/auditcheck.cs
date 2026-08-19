using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CornwallUtilities.config;
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
    internal class AuditCheck : ApplicationCommandsModule
    {
        [SlashCommand("audit-check", "Mostra o conteúdo do arquivo de auditoria")]
        public async Task AuditCheckCommand(
            InteractionContext ctx,
            [Autocomplete(typeof(AuditUsernameAutocompleteProvider))]
            [Option("jogador", "Ver apenas um jogador", true)] string? jogador = null,
            [Choice("Nome", "nome")]
            [Choice("Batalhas", "batalhas")]
            [Choice("Kills", "kills")]
            [Choice("K/D", "kd")]
            [Option("ordenar", "Como ordenar a lista")] string ordenar = "batalhas",
            [Option("privado", "Mostrar apenas para você")] bool privado = false)
        {
            var deferBuilder = new DiscordInteractionResponseBuilder();
            if (privado)
                deferBuilder.AsEphemeral();

            await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource, deferBuilder);

            var (audit, pending) = await AuditStore.Instance.ReadBothAsync();

            if (!string.IsNullOrWhiteSpace(jogador))
            {
                await ShowSinglePlayerAsync(ctx, audit, pending, jogador!);
                return;
            }

            var ordered = Sort(audit.entries, ordenar);

            var pendingNote = pending.batches.Count > 0
                ? $"⚠️ {pending.batches.Count} lote(s) ainda não consolidado(s) — use `/audit-push`."
                : "Todos os lotes estão consolidados.";

            var pages = AuditEmbeds.BuildAuditPages(audit, ordered, pendingNote);

            // Uma unica pagina nao precisa dos botoes de navegacao.
            if (pages.Count == 1)
            {
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(pages[0]));
                return;
            }

            var paginated = pages.Select(p => new Page(string.Empty, p)).ToList();

            // Ordem dos parametros: (deferred, ephemeral). Estavam trocados, entao
            // com "privado: false" a interatividade achava que a interacao ainda
            // nao tinha sido respondida e tentava um CreateResponse por cima do
            // defer da linha 35 - o Discord recusava com BadRequest e o comando
            // morria. Aqui o defer ja aconteceu, entao deferred e sempre true e o
            // ephemeral acompanha a opcao "privado" usada no defer.
            // O paginador so aceita cliques de quem rodou o comando. Registrar o
            // dono aqui permite que o handler de componentes explique isso a quem
            // clicar sem ser dono, em vez de deixar o Discord dizer so
            // "interacao falhou". O id da mensagem paginada e o da propria
            // resposta original - o defer da linha 35 ja a criou.
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

        private static async Task ShowSinglePlayerAsync(InteractionContext ctx, AuditFile audit, PendingFile pending, string jogador)
        {
            var entry = audit.entries.FirstOrDefault(e => string.Equals(e.username, jogador, StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
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

            // Quantas batalhas ainda estao na fila para este jogador.
            var pendingBattles = pending.batches
                .Count(b => b.entries.Any(e => string.Equals(e.username, entry.username, StringComparison.OrdinalIgnoreCase)));

            var embed = new DiscordEmbedBuilder()
                .WithTitle(entry.username)
                .WithColor(DiscordColor.Blurple)
                .WithTimestamp(audit.lastUpdatedUtc == default ? DateTimeOffset.UtcNow : audit.lastUpdatedUtc)
                .AddField(new DiscordEmbedField("Cargo", string.IsNullOrWhiteSpace(entry.rank) ? "*sem cargo*" : entry.rank, false))
                .AddField(new DiscordEmbedField("Kills", entry.kills.ToString(), true))
                .AddField(new DiscordEmbedField("Deaths", entry.deaths.ToString(), true))
                .AddField(new DiscordEmbedField("Assists", entry.assists.ToString(), true))
                .AddField(new DiscordEmbedField("Batalhas", entry.battles.ToString(), true))
                .AddField(new DiscordEmbedField("K/D", entry.KdRatio, true));

            if (pendingBattles > 0)
                embed.AddField(new DiscordEmbedField("Pendente",
                    $"{pendingBattles} batalha(s) ainda não consolidada(s).", false));

            await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(embed));
        }

        private static IEnumerable<AuditEntry> Sort(List<AuditEntry> entries, string mode) => mode switch
        {
            "nome" => entries.OrderBy(e => e.username, StringComparer.OrdinalIgnoreCase),
            "kills" => entries.OrderByDescending(e => e.kills).ThenBy(e => e.username, StringComparer.OrdinalIgnoreCase),
            "kd" => entries.OrderByDescending(e => e.deaths == 0 ? e.kills : (double)e.kills / e.deaths)
                           .ThenBy(e => e.username, StringComparer.OrdinalIgnoreCase),
            _ => entries.OrderByDescending(e => e.battles).ThenBy(e => e.username, StringComparer.OrdinalIgnoreCase)
        };
    }
}
