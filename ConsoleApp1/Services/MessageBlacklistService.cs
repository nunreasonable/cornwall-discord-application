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
        private const int TimeoutThreshold = 5;
        private static readonly TimeSpan TimeoutDuration = TimeSpan.FromMinutes(5);
        private readonly string[] _blacklistedTerms;
        private readonly string[] _responseMessage2Terms;
        private readonly string _responseMessage;
        private readonly string? _responseMessage2;
        private readonly ConcurrentDictionary<string, int> _userTermCounts = new();

        public MessageBlacklistService(
            DiscordClient client,
            IEnumerable<string> blacklistedTerms,
            string responseMessage,
            IEnumerable<string>? responseMessage2Terms = null,
            string? responseMessage2 = null,
            IEnumerable<ulong>? notifyUserIds = null)
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
        }

        public async Task<bool> HandleMessageAsync(DiscordMessage message)
        {
            if (message.Author.IsBot || message.Author.IsSystem == true)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(message.Content) || _blacklistedTerms.Length == 0)
            {
                return false;
            }

            var matchedTerm = _blacklistedTerms.FirstOrDefault(term =>
                message.Content.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);

            if (matchedTerm == null)
            {
                return false;
            }

            var response = BuildResponse(message, matchedTerm);
            if (string.IsNullOrWhiteSpace(response))
            {
                return false;
            }

            if (response.Length > 2000)
            {
                response = response[..1997] + "...";
            }

            await message.Channel.SendMessageAsync(new DiscordMessageBuilder()
                .WithContent(response)
                .WithReply(message.Id));

            var timeoutApplied = await TryApplyTimeoutAsync(message, matchedTerm, violationCount);
            if (timeoutApplied)
            {
                _userTermCounts.TryRemove(countKey, out _);
            }

            if (!string.IsNullOrWhiteSpace(response) || timeoutApplied)
            {
                Console.WriteLine($"Blacklist response handled for message {message.Id} by {message.Author.Username} (matched: {matchedTerm}).");
                return true;
            }

            return false;
        }

        private async Task<bool> TryApplyTimeoutAsync(DiscordMessage message, string matchedTerm, int violationCount)
        {
            if (violationCount <= TimeoutThreshold)
            {
                return false;
            }

            var guild = message.Channel.Guild;
            if (guild is null)
            {
                Console.WriteLine($"Blacklist timeout skipped for message {message.Id}: message was not sent in a guild.");
                return false;
            }

            try
            {
                var member = await guild.GetMemberAsync(message.Author.Id);
                await member.TimeoutAsync(DateTimeOffset.UtcNow.Add(TimeoutDuration), $"Uso recorrente de termo bloqueado: {matchedTerm}");
                Console.WriteLine($"Applied 5-minute timeout to user {message.Author.Id} for repeated blacklisted term \"{matchedTerm}\".");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to apply blacklist timeout to user {message.Author.Id}: {ex.Message}");
                return false;
            }
        }

        private string BuildResponse(DiscordMessage message, string matchedTerm)
        {
            var response = ShouldUseSecondaryResponse(message.Content, matchedTerm)
                ? _responseMessage2 ?? _responseMessage
                : _responseMessage;

            response = response.Replace("{user}", message.Author.Id.ToString(), StringComparison.OrdinalIgnoreCase);
            response = response.Replace("{mention}", message.Author.Mention, StringComparison.OrdinalIgnoreCase);
            response = response.Replace("{term}", matchedTerm, StringComparison.OrdinalIgnoreCase);
            response = response.Replace("{channel}", message.Channel.Mention, StringComparison.OrdinalIgnoreCase);

            return response;
        }

        private bool ShouldUseSecondaryResponse(string messageContent, string matchedTerm)
        {
            return _responseMessage2Terms.Length > 0
                && !string.IsNullOrWhiteSpace(_responseMessage2)
                && _responseMessage2Terms.Any(term =>
                    string.Equals(matchedTerm, term, StringComparison.OrdinalIgnoreCase)
                    || messageContent.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
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
                    _nextAllowedDmAlert = now.Add(DmAlertCooldown);
                }
                else
                {
                    _pendingInfractions.Add(infraction);
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
                    lock (_dmAlertLock)
                    {
                        if (_pendingInfractions.Count == 0)
                        {
                            _bulkFlushLoopRunning = false;
                            return;
                        }

                        batch = new List<BlacklistInfraction>(_pendingInfractions);
                        _pendingInfractions.Clear();
                        _nextAllowedDmAlert = DateTimeOffset.UtcNow.Add(DmAlertCooldown);
                    }

                    await TrySendAlertToConfiguredUsersAsync(BuildBulkAlert(batch));
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

        private static string BuildBulkAlert(IReadOnlyList<BlacklistInfraction> infractions)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"⚠️ Aviso: {infractions.Count} novas infrações durante o cooldown.");
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

            if (included < infractions.Count)
            {
                sb.AppendLine($"... e mais {infractions.Count - included} infrações.");
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