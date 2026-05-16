# Message Reposting Feature

## Overview
This feature automatically stores chat messages for 24 hours and randomly reposts some of them back to the chat at random intervals.

## How it Works
1. **Message Storage**: All non-bot messages are stored in memory for 24 hours
2. **Automatic Cleanup**: Messages older than 24 hours are automatically removed every hour
3. **Random Reposting**: At random intervals (2-4 hours), a random stored message is reposted to the target channel
4. **Memory Management**: The system maintains a maximum of 1000 messages to prevent memory issues

## Configuration
The feature is configured in `config/config.json`:

```json
"messageReposting": {
    "enabled": true,
    "targetChannelId": 1417988333644157078,
    "repostIntervalMinutes": 60,
    "messageRetentionHours": 24,
    "minimumMessagesForRepost": 50
}
```

- `enabled`: Whether the feature is active
- `targetChannelId`: The channel where messages will be reposted
- `repostIntervalMinutes`: Base interval for reposting (actual intervals are random between 2-4 hours)
- `messageRetentionHours`: How long messages are stored (default: 24 hours)
- `minimumMessagesForRepost`: Minimum messages required before reposting (default: 50)

## Message Filtering
The system automatically filters out:
- Bot messages
- System messages
- Empty messages
- Messages longer than 2000 characters

## Important Note
**The cleanup process only removes messages from the bot's memory, NOT from Discord chat.** All original messages remain in Discord permanently. The 24-hour retention only applies to the bot's internal storage used for reposting.

## Implementation Details
- **Service**: `MessageStorageService` in `Services/MessageStorageService.cs`
- **Integration**: Added to `Program.cs` with message listener
- **Storage**: Uses `ConcurrentQueue<StoredMessage>` for thread-safe operations
- **Timers**: Uses `Timer` for cleanup and repost scheduling

## Usage
1. Enable the feature in the configuration file
2. Set the target channel ID where you want messages to be reposted
3. Restart the bot
4. The system will automatically start storing and reposting messages

## Manual Repost Command
You can also manually trigger a message repost using the slash command:
- `/repost` - Manually reposts a random stored message
- **Cooldown**: Puts the automatic reposting service on cooldown for 2 hours
- **Requirements**: Message reposting service must be enabled
- **Permissions**: Available to all users

## Status Check Command
Check the current status of message storage:
- `/messages` - Shows message storage statistics and status
- **Information displayed**:
  - Number of stored messages
  - Minimum messages required
  - Whether reposting is available
  - Progress bar toward minimum requirement
  - Helpful status information
- **Availability**: Registered globally and in your guild for instant access

**Cooldown Behavior:**
- When someone uses `/repost`, it immediately posts a random message
- The automatic reposting service then goes on cooldown for 2 hours
- During cooldown, automatic reposts are skipped but the scheduling continues
- Multiple users can use the command, but each use resets the 2-hour cooldown

**Message Requirements:**
- Minimum **50 messages** must be stored before any repost can occur
- This prevents reposting right before memory cleanup
- Ensures a good variety of messages are available
- Both automatic and manual reposts respect this requirement

## Notes
- Messages are reposted exactly as they were originally sent (no embedding)
- The reposting schedule is completely random to feel more natural
- The feature respects Discord rate limits and message guidelines
- Manual reposts use the same message filtering as automatic reposts
