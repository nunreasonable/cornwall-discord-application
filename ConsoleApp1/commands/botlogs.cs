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
    /// Mostra as ultimas linhas que o bot escreveu no console, lidas do
    /// <see cref="BotLogBuffer"/>. Evita precisar de acesso SSH a maquina para
    /// descobrir por que alguma coisa falhou.
    /// </summary>
    internal class BotLogs : ApplicationCommandsModule
    {
        private const string LevelAll = "todos";
        private const string LevelInfo = "info";
        private const string LevelWarn = "aviso";
        private const string LevelError = "erro";

        [SlashCommand("logs", "Mostra os últimos logs do bot")]
        public async Task LogsCommand(
            InteractionContext ctx,
            [Option("quantidade", "Quantas linhas mostrar (1 a 100, padrão 25)")] long quantidade = 25,
            [Choice("Todos", LevelAll)]
            [Choice("Apenas erros", LevelError)]
            [Choice("Apenas avisos", LevelWarn)]
            [Choice("Apenas informativos", LevelInfo)]
            [Option("nivel", "Filtrar por severidade")] string nivel = LevelAll,
            [Option("filtro", "Mostrar apenas linhas que contenham este texto")] string? filtro = null)
        {
            // Sempre efemero: os logs carregam ids de usuario, mensagens de
            // excecao e caminhos da maquina. Antes havia a opcao `privado:false`,
            // que despejava esse conteudo no canal para qualquer um ver - nao ha
            // motivo legitimo para publica-lo, entao a opcao foi removida.
            const bool privado = true;
            var deferBuilder = new DiscordInteractionResponseBuilder().AsEphemeral();

            await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource, deferBuilder);

            var config = new JSONReader();
            await config.ReadJSON();

            // Mesmo portao de staff do -osinfo, e por um motivo mais forte: os
            // logs carregam ids de usuario, mensagens de excecao e caminhos da
            // maquina que hospeda o bot.
            var denied = AuditPermissions.CheckStaff(ctx, config);
            if (denied is not null)
            {
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(denied));
                return;
            }

            var take = (int)Math.Clamp(quantidade, 1, 100);
            var level = BotLogBuffer.ParseLevel(nivel);
            var contains = string.IsNullOrWhiteSpace(filtro) ? null : filtro.Trim();

            var lines = BotLogBuffer.Snapshot(take, level, contains);
            var scope = BuildScopeText(nivel, contains, take, lines.Count);

            if (lines.Count == 0)
            {
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(new DiscordEmbedBuilder()
                    .WithTitle("Logs do bot")
                    .WithDescription(BotLogBuffer.TotalSeen == 0
                        ? "Nenhuma linha capturada ainda. O buffer começa vazio a cada reinício do bot."
                        : $"Nenhuma linha corresponde ao filtro.\n{scope}")
                    .WithColor(DiscordColor.Orange)));
                return;
            }

            var pages = BuildPages(lines, scope);

            if (pages.Count == 1)
            {
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(pages[0]));
                return;
            }

            var paginated = pages.Select(p => new Page(string.Empty, p)).ToList();

            // Mesmo cuidado do /audit-logs: sem registrar o dono, quem nao rodou
            // o comando clica na seta e o Discord so diz "interacao falhou".
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

        private static string BuildScopeText(string nivel, string? contains, int take, int shown)
        {
            var counts = BotLogBuffer.Counts();
            var parts = new List<string>();

            if (!string.Equals(nivel, LevelAll, StringComparison.OrdinalIgnoreCase))
                parts.Add($"nível: `{nivel}`");
            if (contains is not null)
                parts.Add($"texto: `{AuditEmbeds.Trim(contains, 40)}`");

            var buffer = $"Buffer: {counts.Info} info · {counts.Aviso} aviso · {counts.Erro} erro.";

            return parts.Count == 0
                ? $"Últimas {shown} de até {take} linha(s). {buffer}"
                : $"Filtro — {string.Join(" · ", parts)} · {shown} linha(s). {buffer}";
        }

        /// <summary>
        /// Uma linha por entrada, em bloco de codigo para o alinhamento de
        /// "hora NIVEL texto" sobreviver. O teto por pagina e o mesmo do
        /// /audit-logs, folgado dentro do limite de 4096 da descricao do embed.
        /// </summary>
        private static List<DiscordEmbedBuilder> BuildPages(List<LogLine> lines, string scope, int maxCharsPerPage = 3200)
        {
            var chunks = new List<string>();
            var sb = new StringBuilder();

            foreach (var line in lines)
            {
                var stamp = line.TimestampUtc.ToLocalTime().ToString("HH:mm:ss");
                var text = $"[{stamp}] {BotLogBuffer.LevelName(line.Level),-5} {line.Text}\n";

                if (sb.Length + text.Length > maxCharsPerPage)
                {
                    chunks.Add(sb.ToString());
                    sb.Clear();
                }

                sb.Append(text);
            }

            if (sb.Length > 0)
                chunks.Add(sb.ToString());

            var pages = new List<DiscordEmbedBuilder>();
            for (var i = 0; i < chunks.Count; i++)
            {
                pages.Add(AuditEmbeds.Fit(new DiscordEmbedBuilder()
                    .WithTitle("Logs do bot")
                    .WithDescription($"{scope}\n```\n{AuditEmbeds.FenceSafe(chunks[i])}```")
                    .WithColor(DiscordColor.Blurple)
                    .WithFooter($"Página {i + 1}/{chunks.Count} — mais recente primeiro")
                    .WithTimestamp(DateTimeOffset.UtcNow)));
            }

            return pages;
        }
    }
}
