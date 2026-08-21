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
    public class MessageStorageService : IDisposable
    {
        private readonly ConcurrentQueue<StoredMessage> _messageQueue;
        private readonly Timer _cleanupTimer;
        private Timer? _repostTimer;
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

            // Only store messages from the target channel
            if (message.ChannelId != _targetChannelId)
            {
                // Sem log aqui: isso rodava para toda mensagem de todo canal e
                // Console.WriteLine e sincronizado por lock global.
                return;
            }

            // Create comprehensive stored message
            var storedMessage = new StoredMessage
            {
                Content = message.Content ?? string.Empty,
                AuthorUsername = message.Author.Username,
                Timestamp = DateTime.UtcNow,
                ChannelId = message.ChannelId,
                HasMentions = message.MentionedUsers.Count > 0 || message.MentionedRoles.Count > 0 || message.MentionedChannels.Count > 0,
                // Channel?.GuildId, e nao Channel.GuildId: isto roda no caminho
                // do despacho do gateway, e uma NullReferenceException aqui
                // derruba o evento MESSAGE_CREATE inteiro em vez de so perder
                // uma URL.
                OriginalMessageUrl = $"https://discord.com/channels/{message.Channel?.GuildId}/{message.ChannelId}/{message.Id}"
            };

            // Process attachments
            foreach (var attachment in message.Attachments)
            {
                if (attachment is null)
                {
                    continue;
                }

                var attachmentUrl = attachment.Url?.ToString() ?? string.Empty;
                var storedAttachment = new StoredAttachment
                {
                    FileName = attachment.Id.ToString() ?? string.Empty, // Use ID as filename since FileName property doesn't exist
                    Url = attachmentUrl,
                    ContentType = "attachment", // Simplified since MediaType might not be available
                    FileSize = null, // FileSize might not be available
                    IsImage = attachmentUrl.Contains("image") || attachmentUrl.EndsWith(".png") || attachmentUrl.EndsWith(".jpg") || attachmentUrl.EndsWith(".gif"),
                    Description = string.Empty
                };
                storedMessage.Attachments.Add(storedAttachment);
            }

            // Process embeds
            foreach (var embed in message.Embeds)
            {
                var storedEmbed = new StoredEmbed
                {
                    Title = embed.Title ?? string.Empty,
                    Description = embed.Description ?? string.Empty,
                    Url = embed.Url?.ToString() ?? string.Empty,
                    Color = embed.Color.HasValue ? embed.Color.Value.ToString() : string.Empty
                };

                // Process embed fields
                foreach (var field in embed.Fields)
                {
                    storedEmbed.Fields.Add(new StoredEmbedField
                    {
                        Name = field.Name,
                        Value = field.Value,
                        Inline = field.Inline
                    });
                }

                // Process embed image
                if (embed.Image != null)
                {
                    storedEmbed.Image = new StoredEmbedImage
                    {
                        Url = embed.Image.Url?.ToString() ?? string.Empty,
                        ProxyUrl = embed.Image.ProxyUrl?.ToString() ?? string.Empty,
                        Width = embed.Image.Width,
                        Height = embed.Image.Height
                    };
                }

                // Process embed thumbnail
                if (embed.Thumbnail != null)
                {
                    storedEmbed.Thumbnail = new StoredEmbedImage
                    {
                        Url = embed.Thumbnail.Url?.ToString() ?? string.Empty,
                        ProxyUrl = embed.Thumbnail.ProxyUrl?.ToString() ?? string.Empty,
                        Width = embed.Thumbnail.Width,
                        Height = embed.Thumbnail.Height
                    };
                }

                // Process embed footer
                if (embed.Footer != null)
                {
                    storedEmbed.Footer = new StoredEmbedFooter
                    {
                        Text = embed.Footer.Text ?? string.Empty,
                        IconUrl = embed.Footer.IconUrl?.ToString() ?? string.Empty
                    };
                }

                // Process embed author
                if (embed.Author != null)
                {
                    storedEmbed.Author = new StoredEmbedAuthor
                    {
                        Name = embed.Author.Name ?? string.Empty,
                        Url = embed.Author.Url?.ToString() ?? string.Empty,
                        IconUrl = embed.Author.IconUrl?.ToString() ?? string.Empty
                    };
                }

                storedMessage.Embeds.Add(storedEmbed);
            }

            // Process stickers
            foreach (var sticker in message.Stickers)
            {
                var storedSticker = new StoredSticker
                {
                    Name = sticker.Name,
                    Description = sticker.Description ?? string.Empty,
                    Url = $"https://cdn.discordapp.com/stickers/{sticker.Id}.png", // Construct sticker URL
                    FormatType = sticker.FormatType.ToString()
                };
                storedMessage.Stickers.Add(storedSticker);
            }

            // Process reactions
            foreach (var reaction in message.Reactions)
            {
                var emojiUrl = "";
                try
                {
                    // Try to get emoji URL - this might fail for some emojis
                    if (reaction.Emoji.Id != 0)
                    {
                        emojiUrl = $"https://cdn.discordapp.com/emojis/{reaction.Emoji.Id}";
                    }
                }
                catch
                {
                    // Fallback for any issues
                    emojiUrl = string.Empty;
                }
                
                var storedReaction = new StoredReaction
                {
                    EmojiName = reaction.Emoji.Name ?? "Unknown",
                    IsCustomEmoji = reaction.Emoji.Id != 0,
                    EmojiUrl = emojiUrl,
                    Count = reaction.Count
                };
                storedMessage.Reactions.Add(storedReaction);
            }

            // Determine message type
            storedMessage.MessageType = DetermineMessageType(storedMessage);

            // Store the message
            _messageQueue.Enqueue(storedMessage);
            
            // Corte barato e limitado. O CleanupOldMessages completo (que drena e
            // reenfileira a fila inteira) roda no timer horario, nunca aqui - isso
            // ficava no caminho do despacho de eventos do gateway.
            while (_messageQueue.Count > 1000 && _messageQueue.TryDequeue(out _))
            {
            }
        }

        private MessageType DetermineMessageType(StoredMessage message)
        {
            var hasText = !string.IsNullOrWhiteSpace(message.Content);
            var hasAttachments = message.Attachments.Count > 0;
            var hasEmbeds = message.Embeds.Count > 0;
            var hasStickers = message.Stickers.Count > 0;
            var hasReactions = message.Reactions.Count > 0;

            var contentTypes = new List<bool> { hasText, hasAttachments, hasEmbeds, hasStickers, hasReactions };
            var activeTypes = contentTypes.Count(x => x);

            if (activeTypes == 0) return MessageType.SystemMessage;
            if (activeTypes == 1)
            {
                if (hasAttachments && message.Attachments.All(a => a.IsImage)) return MessageType.ImageOnly;
                if (hasAttachments) return MessageType.AttachmentOnly;
                if (hasEmbeds) return MessageType.EmbedOnly;
                if (hasStickers) return MessageType.StickerOnly;
                if (hasReactions) return MessageType.ReactionOnly;
            }

            return MessageType.MixedContent;
        }

        private void ScheduleNextRepost()
        {
            // Random interval between 2 hours and 4 hours to ensure minimum 2 hour gap
            var nextInterval = Random.Shared.Next(120, 241);
            var due = TimeSpan.FromMinutes(nextInterval);

            // Um unico timer reaproveitado via Change(). Antes o timer era
            // descartado e recriado de dentro do proprio callback, e o lambda
            // `async _ => await ...` era um `async void`: qualquer excecao que
            // escapasse derrubava o processo inteiro.
            if (_repostTimer is null)
                _repostTimer = new Timer(static state => _ = ((MessageStorageService)state!).RunRepostSafeAsync(), this, due, Timeout.InfiniteTimeSpan);
            else
                _repostTimer.Change(due, Timeout.InfiniteTimeSpan);
        }

        /// <summary>
        /// Envolve TryRepostRandomMessage para que nenhuma excecao escape do
        /// callback do timer (o que mataria o processo).
        /// </summary>
        private async Task RunRepostSafeAsync()
        {
            try
            {
                await TryRepostRandomMessage();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[repost] erro nao tratado: {ex}");
                try { ScheduleNextRepost(); } catch { /* servico descartado */ }
            }
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

                // Pick a random message that has text and/or attachment URLs so it can be reposted faithfully
                var repostableMessages = messages.Where(IsRepostableMessage).ToArray();
                if (repostableMessages.Length == 0)
                {
                    Console.WriteLine("No repostable messages with text or attachments available");
                    return;
                }

                var randomMessage = repostableMessages[Random.Shared.Next(repostableMessages.Length)];

                // Get the target channel
                var channel = await _client.GetChannelAsync(_targetChannelId);
                if (channel is not null)
                {
                    var sent = await RepostExactMessage(channel, randomMessage);
                    if (!sent)
                    {
                        Console.WriteLine("Selected message could not be reposted as exact text.");
                        return;
                    }

                    Console.WriteLine($"Reposted exact message from {randomMessage.AuthorUsername}");
                    
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

        /// <summary>
        /// Descarta da memoria o que passou da janela de retencao. NAO apaga
        /// nada do Discord.
        ///
        /// A poda e feita pela FRENTE da fila, com TryPeek, e nao drenando tudo
        /// para reenfileirar o que sobrou: a fila e FIFO e o Timestamp e posto
        /// no enfileiramento, entao as mais antigas estao sempre na frente. A
        /// versao anterior tirava um retrato, esvaziava a fila e devolvia o
        /// retrato - qualquer mensagem que chegasse entre o retrato e o dreno
        /// era engolida pelo `while (TryDequeue)` e sumia sem nunca ter sido
        /// velha.
        /// </summary>
        private void CleanupOldMessages(object? state)
        {
            var cutoffTime = DateTime.UtcNow.AddHours(-_messageRetentionHours);
            var removed = 0;

            while (_messageQueue.TryPeek(out var oldest) && oldest.Timestamp <= cutoffTime)
            {
                if (!_messageQueue.TryDequeue(out _))
                    break;

                removed++;
            }

            Console.WriteLine($"Memory cleanup completed. Removed {removed} old messages from memory. Current count: {_messageQueue.Count}");
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
            
            return messages[Random.Shared.Next(messages.Length)];
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

                var repostableMessages = messages.Where(IsRepostableMessage).ToArray();
                if (repostableMessages.Length == 0)
                {
                    Console.WriteLine("Manual repost rejected: No repostable messages with text or attachments available");
                    return false;
                }

                var randomMessage = repostableMessages[Random.Shared.Next(repostableMessages.Length)];
                if (randomMessage is null) return false;

                var channel = await _client.GetChannelAsync(_targetChannelId);
                if (channel is not null)
                {
                    var sent = await RepostExactMessage(channel, randomMessage);
                    if (!sent)
                    {
                        Console.WriteLine("Manual repost rejected: Selected message could not be reposted as exact text");
                        return false;
                    }

                    Console.WriteLine("Manual repost triggered: exact message content sent");
                    
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

        private static bool IsRepostableMessage(StoredMessage message)
        {
            return !string.IsNullOrWhiteSpace(message.Content) ||
                   message.Attachments.Any(a => !string.IsNullOrWhiteSpace(a.Url));
        }

        private async Task<bool> RepostExactMessage(DiscordChannel channel, StoredMessage message)
        {
            // Uma mensagem guardada de quem tem Nitro pode passar dos 2000
            // caracteres que o bot consegue enviar; cortar aqui evita um 400 que
            // derrubaria o repost inteiro.
            //
            // O corte vai para uma variavel local, e nao de volta para o objeto
            // guardado: escrever em message.Content mutilava permanentemente a
            // copia que continua na fila, entao a mensagem original ficava
            // truncada para sempre depois do primeiro repost.
            var content = message.Content.Length > 2000
                ? message.Content[..1997] + "..."
                : message.Content;

            var hasText = !string.IsNullOrWhiteSpace(content);
            var attachmentUrls = message.Attachments
                .Select(a => a.Url)
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Distinct()
                .ToList();

            if (!hasText && attachmentUrls.Count == 0)
            {
                return false;
            }

            // Send original text exactly as stored so Discord markdown/rich formatting is preserved.
            //
            // Mentions.None e obrigatorio aqui: o texto e reenviado tal e qual, e
            // sem isso um @everyone, cargo ou usuario citado na mensagem original
            // seria pingado de novo a cada repost.
            if (hasText)
            {
                await channel.SendMessageAsync(new DiscordMessageBuilder()
                    .WithContent(content)
                    .WithAllowedMentions(Mentions.None));
            }

            // Send attachment URLs so Discord can render image previews/rich embeds from CDN links.
            foreach (var url in attachmentUrls)
            {
                await channel.SendMessageAsync(new DiscordMessageBuilder()
                    .WithContent(url)
                    .WithAllowedMentions(Mentions.None));
            }

            return true;
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
        public List<StoredAttachment> Attachments { get; set; } = new();
        public List<StoredEmbed> Embeds { get; set; } = new();
        public List<StoredSticker> Stickers { get; set; } = new();
        public List<StoredReaction> Reactions { get; set; } = new();
        public MessageType MessageType { get; set; }
        public bool HasMentions { get; set; }
        public string OriginalMessageUrl { get; set; } = string.Empty;
    }

    public class StoredAttachment
    {
        public string FileName { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public long? FileSize { get; set; }
        public bool IsImage { get; set; }
        public string Description { get; set; } = string.Empty;
    }

    public class StoredEmbed
    {
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string Color { get; set; } = string.Empty;
        public List<StoredEmbedField> Fields { get; set; } = new();
        public StoredEmbedImage? Image { get; set; }
        public StoredEmbedImage? Thumbnail { get; set; }
        public StoredEmbedFooter? Footer { get; set; }
        public StoredEmbedAuthor? Author { get; set; }
    }

    public class StoredEmbedField
    {
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public bool Inline { get; set; }
    }

    public class StoredEmbedImage
    {
        public string Url { get; set; } = string.Empty;
        public string ProxyUrl { get; set; } = string.Empty;
        public int? Width { get; set; }
        public int? Height { get; set; }
    }

    public class StoredEmbedFooter
    {
        public string Text { get; set; } = string.Empty;
        public string IconUrl { get; set; } = string.Empty;
    }

    public class StoredEmbedAuthor
    {
        public string Name { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string IconUrl { get; set; } = string.Empty;
    }

    public class StoredSticker
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string FormatType { get; set; } = string.Empty;
    }

    public class StoredReaction
    {
        public string EmojiName { get; set; } = string.Empty;
        public bool IsCustomEmoji { get; set; }
        public string EmojiUrl { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    public enum MessageType
    {
        Default,
        ImageOnly,
        AttachmentOnly,
        EmbedOnly,
        StickerOnly,
        ReactionOnly,
        MixedContent,
        SystemMessage
    }
}
