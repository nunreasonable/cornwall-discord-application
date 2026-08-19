using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
    /// <summary>
    /// Mostra o historico de quem mexeu na auditoria e o que foi feito.
    /// Os registros vem do AuditLog, alimentado pelos demais comandos /audit-*.
    /// </summary>
    internal class AuditLogs : ApplicationCommandsModule
    {
        [SlashCommand("audit-logs", "Mostra quem mexeu na auditoria e o que foi feito")]
        public async Task AuditLogsCommand(
            InteractionContext ctx,
            [Option("usuario", "Ver apenas as ações de uma pessoa")] DiscordUser? usuario = null,
            [Choice("Registro de batalha (/audit-add)", AuditLog.ActionAdd)]
            [Choice("Edição de jogador (/audit-edit)", AuditLog.ActionEdit)]
            [Choice("Remoção de jogador (/audit-edit)", AuditLog.ActionRemove)]
            [Choice("Definição de cargos (/audit-setranks)", AuditLog.ActionSetRanks)]
            [Choice("Importação da planilha (/audit-import)", AuditLog.ActionImport)]
            [Choice("Publicação no GitHub (/audit-push)", AuditLog.ActionPush)]
            [Option("acao", "Ver apenas um tipo de ação")] string? acao = null,
            [Option("privado", "Mostrar apenas para você")] bool privado = true)
        {
            var deferBuilder = new DiscordInteractionResponseBuilder();
            if (privado)
                deferBuilder.AsEphemeral();

            await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource, deferBuilder);

            var config = new JSONReader();
            await config.ReadJSON();

            var denied = AuditPermissions.CheckStaff(ctx, config);
            if (denied is not null)
            {
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(denied));
                return;
            }

            var all = await AuditLog.ReadAsync();

            // Mais recente primeiro: e o que se quer ver ao abrir o historico.
            var filtered = all
                .Where(e => usuario is null || e.userId == usuario.Id)
                .Where(e => acao is null || string.Equals(e.action, acao, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.timestampUtc)
                .ToList();

            var scope = BuildScopeText(usuario, acao, all.Count, filtered.Count);

            if (filtered.Count == 0)
            {
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(new DiscordEmbedBuilder()
                    .WithTitle("Histórico da auditoria")
                    .WithDescription(all.Count == 0
                        ? "Nenhuma ação registrada ainda. O histórico começa a ser preenchido no próximo `/audit-add`, `/audit-edit`, `/audit-setranks`, `/audit-import` ou `/audit-push`."
                        : $"Nenhuma ação corresponde ao filtro.\n{scope}")
                    .WithColor(DiscordColor.Orange)));
                return;
            }

            var pages = BuildPages(filtered, scope);

            if (pages.Count == 1)
            {
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(pages[0]));
                return;
            }

            var paginated = pages.Select(p => new Page(string.Empty, p)).ToList();

            // Mesmo cuidado do /audit-check: registrar o dono deixa o handler de
            // componentes explicar o clique de terceiros em vez de deixar o
            // Discord dizer so "interacao falhou".
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

        private static string BuildScopeText(DiscordUser? usuario, string? acao, int total, int shown)
        {
            var parts = new List<string>();
            if (usuario is not null)
                parts.Add($"pessoa: {usuario.Mention}");
            if (acao is not null)
                parts.Add($"ação: `{acao}`");

            return parts.Count == 0
                ? $"{total} ação(ões) registrada(s)."
                : $"Filtro — {string.Join(" · ", parts)} · {shown} de {total} ação(ões).";
        }

        /// <summary>
        /// Uma linha por acao, em texto normal (nao em bloco de codigo) para que a
        /// data use o carimbo nativo do Discord e apareca no fuso de quem le.
        /// </summary>
        private static List<DiscordEmbedBuilder> BuildPages(List<AuditLogEntry> entries, string scope, int maxCharsPerPage = 3200)
        {
            var chunks = new List<string>();
            var sb = new StringBuilder();

            foreach (var entry in entries)
            {
                var line =
                    $"<t:{entry.timestampUtc.ToUnixTimeSeconds()}:f> — **{AuditEmbeds.Trim(entry.username, 32)}** " +
                    $"(<@{entry.userId}>)\n`{Label(entry.action)}` {AuditEmbeds.Trim(entry.details, 200)}\n";

                if (sb.Length + line.Length > maxCharsPerPage)
                {
                    chunks.Add(sb.ToString());
                    sb.Clear();
                }

                sb.Append(line);
            }

            if (sb.Length > 0)
                chunks.Add(sb.ToString());

            var pages = new List<DiscordEmbedBuilder>();
            for (var i = 0; i < chunks.Count; i++)
            {
                pages.Add(new DiscordEmbedBuilder()
                    .WithTitle("Histórico da auditoria")
                    .WithDescription($"{scope}\n\n{chunks[i]}")
                    .WithColor(DiscordColor.Blurple)
                    .WithFooter($"Página {i + 1}/{chunks.Count} — mais recente primeiro")
                    .WithTimestamp(DateTimeOffset.UtcNow));
            }

            return pages;
        }

        private static string Label(string action) => action switch
        {
            AuditLog.ActionAdd => "batalha registrada",
            AuditLog.ActionEdit => "jogador editado",
            AuditLog.ActionRemove => "jogador removido",
            AuditLog.ActionSetRanks => "cargos definidos",
            AuditLog.ActionImport => "planilha importada",
            AuditLog.ActionPush => "publicado no GitHub",
            _ => action.ToLowerInvariant()
        };
    }
}
