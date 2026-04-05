using DisCatSharp.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CornwallUtilities.Services
{
    public class MessageBlacklistService
    {
        private readonly string[] _blacklistedTerms;
        private readonly string[] _responseMessage2Terms;
        private readonly string _responseMessage;
        private readonly string? _responseMessage2;

        public MessageBlacklistService(IEnumerable<string> blacklistedTerms, string responseMessage, IEnumerable<string>? responseMessage2Terms = null, string? responseMessage2 = null)
        {
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

            Console.WriteLine($"Blacklist response sent for message {message.Id} by {message.Author.Username} (matched: {matchedTerm}).");
            return true;
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
    }
}