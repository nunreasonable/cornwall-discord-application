using DisCatSharp;
using DisCatSharp.Entities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CornwallUtilities.Services
{
    public class MessageStorageService
    {
        private readonly ConcurrentQueue<StoredMessage> _messageQueue;
        private readonly Timer _cleanupTimer;
        private Timer? _repostTimer;
        private readonly Random _random;
        private readonly DiscordClient _client;
        private readonly ulong _targetChannelId;
        private readonly int _repostIntervalMinutes;
        private readonly int _messageRetentionHours;
        private DateTime _nextRepostTime;
        private readonly int _minimumMessagesForRepost;

        public MessageStorageService(DiscordClient client, ulong targetChannelId, int repostIntervalMinutes = 60, int messageRetentionHours = 24, int minimumMessagesForRepost = 50)
        {
            _client = client;
            _targetChannelId = targetChannelId;
            _repostIntervalMinutes = repostIntervalMinutes;
            _messageRetentionHours = messageRetentionHours;
            _minimumMessagesForRepost = minimumMessagesForRepost;
            _messageQueue = new ConcurrentQueue<StoredMessage>();
            _random = new Random();
            _nextRepostTime = DateTime.UtcNow;

            // Start cleanup timer (runs every hour)
            _cleanupTimer = new Timer(CleanupOldMessages, null!, TimeSpan.FromHours(1), TimeSpan.FromHours(1));

            // Start repost timer with random intervals
            ScheduleNextRepost();
        }

        public void StoreMessage(DiscordMessage message)
        {
            // Skip bot messages and system messages
            if (message.Author.IsBot || message.Author.IsSystem == true) 
                return;

            // Skip messages that are too short or too long
            if (string.IsNullOrWhiteSpace(message.Content) || message.Content.Length > 2000)
                return;

            var storedMessage = new StoredMessage
            {
                Content = message.Content,
                AuthorUsername = message.Author.Username,
                Timestamp = DateTime.UtcNow,
                ChannelId = message.ChannelId
            };

            _messageQueue.Enqueue(storedMessage);

            // Keep only messages from last 24 hours (rough limit to prevent memory issues)
            if (_messageQueue.Count > 1000)
            {
                CleanupOldMessages(null!);
            }
        }

        private void ScheduleNextRepost()
        {
            // Random interval between 2 hours and 4 hours to ensure minimum 2 hour gap
            var nextInterval = _random.Next(120, 241);
            _repostTimer?.Dispose();
            _repostTimer = new Timer(async _ => await TryRepostRandomMessage(), null!, TimeSpan.FromMinutes(nextInterval), Timeout.InfiniteTimeSpan);
        }

        private async Task TryRepostRandomMessage()
        {
            try
            {
                // Check if we're on cooldown
                if (DateTime.UtcNow < _nextRepostTime)
                {
                    Console.WriteLine($"Automatic repost on cooldown until {_nextRepostTime}");
                    return;
                }

                var messages = _messageQueue.ToArray();
                if (messages.Length == 0) 
                {
                    Console.WriteLine("No messages available for reposting");
                    return;
                }

                // Check if we have enough messages to avoid reposting right before cleanup
                if (messages.Length < _minimumMessagesForRepost)
                {
                    Console.WriteLine($"Not enough messages for reposting ({messages.Length}/{_minimumMessagesForRepost})");
                    return;
                }

                // Pick a random message
                var randomMessage = messages[_random.Next(messages.Length)];

                // Get the target channel
                var channel = await _client.GetChannelAsync(_targetChannelId);
                if (channel != null)
                {
                    await channel.SendMessageAsync(randomMessage.Content);
                    Console.WriteLine($"Reposted message from {randomMessage.AuthorUsername}: {randomMessage.Content}");
                    
                    // Set cooldown for 2 hours
                    _nextRepostTime = DateTime.UtcNow.AddHours(2);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reposting message: {ex.Message}");
            }
            finally
            {
                // Schedule next repost
                ScheduleNextRepost();
            }
        }

        private void CleanupOldMessages(object? state)
        {
            var cutoffTime = DateTime.UtcNow.AddHours(-_messageRetentionHours);
            var messages = _messageQueue.ToArray();
            var validMessages = messages.Where(m => m.Timestamp > cutoffTime).ToArray();

            // Clear and re-add valid messages to memory only
            // NOTE: This only removes messages from memory, NOT from Discord chat
            while (_messageQueue.TryDequeue(out _)) { }
            
            foreach (var message in validMessages)
            {
                _messageQueue.Enqueue(message);
            }

            Console.WriteLine($"Memory cleanup completed. Removed {messages.Length - validMessages.Length} old messages from memory. Current count: {validMessages.Length}");
        }

        public int GetMessageCount()
        {
            return _messageQueue.Count;
        }

        public int GetMinimumMessages()
        {
            return _minimumMessagesForRepost;
        }

        public StoredMessage? GetRandomMessage()
        {
            var messages = _messageQueue.ToArray();
            if (messages.Length == 0) return null;
            
            return messages[_random.Next(messages.Length)];
        }

        public async Task<bool> ManualRepostAsync()
        {
            try
            {
                var messages = _messageQueue.ToArray();
                if (messages.Length == 0) return false;

                // Check if we have enough messages to avoid reposting right before cleanup
                if (messages.Length < _minimumMessagesForRepost)
                {
                    Console.WriteLine($"Manual repost rejected: Not enough messages ({messages.Length}/{_minimumMessagesForRepost})");
                    return false;
                }

                var randomMessage = GetRandomMessage();
                if (randomMessage == null) return false;

                var channel = await _client.GetChannelAsync(_targetChannelId);
                if (channel != null)
                {
                    await channel.SendMessageAsync(randomMessage.Content);
                    Console.WriteLine($"Manual repost triggered: {randomMessage.Content}");
                    
                    // Set cooldown for 2 hours
                    _nextRepostTime = DateTime.UtcNow.AddHours(2);
                    
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in manual repost: {ex.Message}");
            }
            
            return false;
        }

        public void Dispose()
        {
            _cleanupTimer?.Dispose();
            _repostTimer?.Dispose();
        }
    }

    public class StoredMessage
    {
        public string Content { get; set; } = string.Empty;
        public string AuthorUsername { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public ulong ChannelId { get; set; }
    }
}
