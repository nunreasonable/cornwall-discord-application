using DisCatSharp;
using DisCatSharp.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CornwallUtilities.Services
{
    public class MessageBlacklistService
    {
        private static readonly TimeSpan DefaultDmAlertCooldown = TimeSpan.FromMinutes(5);

        private readonly DiscordClient _client;
        private readonly string[] _blacklistedTerms;
        private readonly string[] _responseMessage2Terms;
        private readonly string _responseMessage;
        private readonly string? _responseMessage2;
        private readonly ulong[] _notifyUserIds;
        private readonly TimeSpan _dmAlertCooldown;

        // Guards every field below it. The DM alerts are throttled so a flood of infractions
        // can't turn the bot into a DM spammer (which is what gets bots quarantined).
        private readonly object _dmAlertLock = new();
        private readonly List<BlacklistInfraction> _pendingInfractions = new();
        private DateTimeOffset _nextAllowedDmAlert = DateTimeOffset.MinValue;
        private bool _bulkFlushLoopRunning;
        private int _droppedInfractions;

        /// <summary>Quantas infracoes cabem na fila de resumo de uma janela.</summary>
        private const int MaxPendingInfractions = 500;

        public MessageBlacklistService(
            DiscordClient client,
            IEnumerable<string> blacklistedTerms,
            string responseMessage,
            IEnumerable<string>? responseMessage2Terms = null,
            string? responseMessage2 = null,
            IEnumerable<ulong>? notifyUserIds = null,
            int? dmAlertCooldownMinutes = null)
        {
            _client = client;
            _blacklistedTerms = blacklistedTerms
                .Where(term => !string.IsNullOrWhiteSpace(term))
                .Select(term => term.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            _responseMessage = responseMessage ?? string.Empty;
            _responseMessage2Terms = responseMessage2Terms == null
                ? Array.Empty<string>()
                : responseMessage2Terms
                    .Where(term => !string.IsNullOrWhiteSpace(term))
                    .Select(term => term.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            _responseMessage2 = string.IsNullOrWhiteSpace(responseMessage2) ? null : responseMessage2;
            _notifyUserIds = (notifyUserIds ?? Array.Empty<ulong>())
                .Where(id => id != 0)
                .Distinct()
                .Take(2)
                .ToArray();
            _dmAlertCooldown = dmAlertCooldownMinutes.HasValue
                ? TimeSpan.FromMinutes(Math.Clamp(dmAlertCooldownMinutes.Value, 1, 60))
                : DefaultDmAlertCooldown;
        }

        public async Task<bool> HandleMessageAsync(DiscordMessage message)
        {
            if (message.Author.IsBot || message.Author.IsSystem == true)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(message.Content))
            {
                return false;
            }

            if (_blacklistedTerms.Length == 0 && _responseMessage2Terms.Length == 0)
            {
                return false;
            }

            var match = FindMatch(message.Content);
            if (match is null)
            {
                return false;
            }

            var (matchedTerm, useSecondary) = match.Value;

            var response = BuildResponse(message, matchedTerm, useSecondary);
            if (!string.IsNullOrWhiteSpace(response))
            {
                if (response.Length > 2000)
                {
                    response = response[..1997] + "...";
                }

                // Sem WithAllowedMentions(Mentions.None) um @everyone ou @cargo no
                // responseMessage do config (que ainda passa por {mention}/{term})
                // seria disparado a cada infracao. O texto e informativo; nao deve
                // pingar ninguem.
                await message.Channel.SendMessageAsync(new DiscordMessageBuilder()
                    .WithContent(response)
                    .WithAllowedMentions(Mentions.None)
                    .WithReply(message.Id));
            }

            // The alert fires even when there is no configured reply text, so infractions are
            // never silently dropped just because responseMessage is blank.
            await TryNotifyConfiguredUsersAsync(message, matchedTerm);

            Console.WriteLine($"Blacklist match on message {message.Id} by {message.Author.Username} (matched: \"{matchedTerm}\").");
            return true;
        }

        /// <summary>
        /// Looks for a hit in either term list. The secondary list wins so a phrase from it
        /// isn't hijacked by a primary term that happens to appear in the same message.
        /// </summary>
        private (string Term, bool UseSecondary)? FindMatch(string content)
        {
            if (_responseMessage2 is not null)
            {
                var secondary = _responseMessage2Terms.FirstOrDefault(term =>
                    content.Contains(term, StringComparison.OrdinalIgnoreCase));
                if (secondary is not null)
                {
                    return (secondary, true);
                }
            }

            var primary = _blacklistedTerms.FirstOrDefault(term =>
                content.Contains(term, StringComparison.OrdinalIgnoreCase));

            return primary is null ? null : (primary, false);
        }

        private string BuildResponse(DiscordMessage message, string matchedTerm, bool useSecondary)
        {
            var response = useSecondary
                ? _responseMessage2 ?? _responseMessage
                : _responseMessage;

            response = response.Replace("{user}", message.Author.Id.ToString(), StringComparison.OrdinalIgnoreCase);
            response = response.Replace("{mention}", message.Author.Mention, StringComparison.OrdinalIgnoreCase);
            response = response.Replace("{term}", matchedTerm, StringComparison.OrdinalIgnoreCase);
            response = response.Replace("{channel}", message.Channel.Mention, StringComparison.OrdinalIgnoreCase);

            return response;
        }

        private async Task<bool> TryNotifyConfiguredUsersAsync(DiscordMessage message, string matchedTerm)
        {
            if (_notifyUserIds.Length == 0)
            {
                return false;
            }

            var infraction = CreateInfraction(message, matchedTerm);

            bool shouldSendImmediate;
            bool shouldStartFlushLoop;
            lock (_dmAlertLock)
            {
                var now = DateTimeOffset.UtcNow;
                shouldSendImmediate = now >= _nextAllowedDmAlert;
                if (shouldSendImmediate)
                {
                    _nextAllowedDmAlert = now.Add(_dmAlertCooldown);
                }
                else if (_pendingInfractions.Count < MaxPendingInfractions)
                {
                    _pendingInfractions.Add(infraction);
                }
                else
                {
                    // Teto de memoria: num flood, o resumo ja vai truncado de
                    // qualquer jeito - guardar mais nao acrescenta nada.
                    _droppedInfractions++;
                }

                shouldStartFlushLoop = !_bulkFlushLoopRunning;
                if (shouldStartFlushLoop)
                {
                    _bulkFlushLoopRunning = true;
                }
            }

            if (shouldStartFlushLoop)
            {
                _ = RunBulkFlushLoopAsync();
            }

            if (!shouldSendImmediate)
            {
                return false;
            }

            return await TrySendAlertToConfiguredUsersAsync(BuildSingleAlert(infraction));
        }

        private async Task<bool> TrySendAlertToConfiguredUsersAsync(string alert)
        {
            var sentAny = false;
            foreach (var userId in _notifyUserIds)
            {
                // Mesmo limitador global dos comandos de DM: o cooldown daqui
                // espaca os ALERTAS, mas dois alertas seguidos para dois
                // destinatarios ainda saiam em rajada.
                await DmRateLimiter.WaitForSlotAsync();

                try
                {
                    var user = await _client.GetUserAsync(userId);
                    var dm = await user.CreateDmChannelAsync();
                    await dm.SendMessageAsync(alert);
                    sentAny = true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to send blacklist DM alert to {userId}: {ex.Message}");
                }
            }

            return sentAny;
        }

        /// <summary>
        /// Sleeps until the current cooldown window closes, then sends one digest covering
        /// everything that piled up during it. Exits once a window closes with nothing pending.
        /// </summary>
        private async Task RunBulkFlushLoopAsync()
        {
            try
            {
                while (true)
                {
                    DateTimeOffset nextWindow;
                    lock (_dmAlertLock)
                    {
                        nextWindow = _nextAllowedDmAlert;
                    }

                    var delay = nextWindow - DateTimeOffset.UtcNow;
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay);
                    }

                    List<BlacklistInfraction> batch;
                    int dropped;
                    lock (_dmAlertLock)
                    {
                        if (_pendingInfractions.Count == 0)
                        {
                            _bulkFlushLoopRunning = false;
                            return;
                        }

                        batch = new List<BlacklistInfraction>(_pendingInfractions);
                        dropped = _droppedInfractions;
                        _pendingInfractions.Clear();
                        _droppedInfractions = 0;
                        _nextAllowedDmAlert = DateTimeOffset.UtcNow.Add(_dmAlertCooldown);
                    }

                    await TrySendAlertToConfiguredUsersAsync(BuildBulkAlert(batch, dropped));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Blacklist bulk DM loop failed: {ex.Message}");
                lock (_dmAlertLock)
                {
                    _bulkFlushLoopRunning = false;
                }
            }
        }

        private BlacklistInfraction CreateInfraction(DiscordMessage message, string matchedTerm)
        {
            var content = string.IsNullOrWhiteSpace(message.Content) ? "[sem texto]" : message.Content;
            if (content.Length > 300)
            {
                content = content[..300] + "...";
            }

            return new BlacklistInfraction(
                DateTimeOffset.UtcNow,
                message.Author.Username,
                message.Author.Id,
                matchedTerm,
                message.Channel.Mention,
                content);
        }

        private static string BuildSingleAlert(BlacklistInfraction infraction)
        {
            return
                "⚠️ Aviso: conteúdo da blacklist foi dito.\n" +
                $"Usuário: {infraction.AuthorUsername} ({infraction.AuthorId})\n" +
                $"Termo detectado: {infraction.MatchedTerm}\n" +
                $"Canal: {infraction.ChannelMention}\n" +
                $"Mensagem: {infraction.Content}";
        }

        private static string BuildBulkAlert(IReadOnlyList<BlacklistInfraction> infractions, int dropped)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"⚠️ Aviso: {infractions.Count + dropped} novas infrações durante o cooldown.");
            sb.AppendLine("Resumo:");

            var included = 0;
            foreach (var inf in infractions)
            {
                var line =
                    $"- [{inf.Timestamp:HH:mm:ss}] {inf.AuthorUsername} ({inf.AuthorId}) | termo: {inf.MatchedTerm} | canal: {inf.ChannelMention} | msg: {inf.Content}";
                if (sb.Length + line.Length + Environment.NewLine.Length > 1800)
                {
                    break;
                }

                sb.AppendLine(line);
                included++;
            }

            var omitted = infractions.Count - included + dropped;
            if (omitted > 0)
            {
                sb.AppendLine($"... e mais {omitted} infrações.");
            }

            return sb.ToString();
        }

        private sealed record BlacklistInfraction(
            DateTimeOffset Timestamp,
            string AuthorUsername,
            ulong AuthorId,
            string MatchedTerm,
            string ChannelMention,
            string Content);
    }
}
