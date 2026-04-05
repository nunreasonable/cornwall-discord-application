using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace CornwallUtilities.config
{
    internal class JSONReader
    {
        public string? token { get; private set; }
        public string? prefix { get; private set; }
        public string? spreadsheetCsvUrl { get; private set; }
        public string? spreadsheetRosterCsvUrl { get; private set; }
        public string? defaultGameLink { get; private set; }

        // Config used by the /enlistuser slash command
        public ulong? enlistPermissionRoleId { get; private set; }
        public ulong? enlistLogChannelId { get; private set; }
        public string? enlistAltCheckUrl { get; private set; }
        public ulong[]? enlistTargetRoleIds { get; private set; }

        // Config used by the ROBLOX enlistment command
        public ulong? robloxEnlistChannelId { get; private set; }
        public ulong[]? robloxEnlistBlockedRoleIds { get; private set; }

        // Config used by the deployment command
        public string? deploymentGameLink { get; private set; }
        public string? deploymentVoiceChatLink { get; private set; }
        public ulong[]? deploymentAllowedRoleIds { get; private set; }
        public ulong[]? deploymentDefaultRoles { get; private set; }
        public string? deploymentTitle { get; private set; }
        public string? deploymentVoiceChannel { get; private set; }
        public string? deploymentImageUrl { get; private set; }
        public string? deploymentPlaceId { get; private set; }
        public string? deploymentQuickLaunchLink { get; private set; }
        public ulong? deploymentChannelId { get; private set; }

        // Config used by message reposting
        public bool? messageRepostingEnabled { get; private set; }
        public ulong? messageRepostingTargetChannelId { get; private set; }
        public int? messageRepostingIntervalMinutes { get; private set; }
        public int? messageRepostingRetentionHours { get; private set; }
        public int? messageRepostingMinimumMessages { get; private set; }
        public ulong[]? messageRepostingExcludedChannelIds { get; private set; }

        // Config used by message blacklist detection
        public MessageBlacklistConfig? messageBlacklist { get; private set; }

        public async Task ReadJSON()
        {
            using var sr = new StreamReader("config/config.json");
            var json = await sr.ReadToEndAsync();
            var data = JsonConvert.DeserializeObject<JSONStructure>(json);

            token = data?.token;
            prefix = data?.prefix;
            spreadsheetCsvUrl = data?.spreadsheetCsvUrl;
            spreadsheetRosterCsvUrl = data?.spreadsheetRosterCsvUrl;
            defaultGameLink = data?.defaultGameLink;

            enlistPermissionRoleId = data?.enlistPermissionRoleId;
            enlistLogChannelId = data?.enlistLogChannelId;
            enlistAltCheckUrl = data?.enlistAltCheckUrl;
            enlistTargetRoleIds = data?.enlistTargetRoleIds;

            robloxEnlistChannelId = data?.robloxEnlistChannelId;
            robloxEnlistBlockedRoleIds = data?.robloxEnlistBlockedRoleIds;

            deploymentGameLink = data?.deploymentGameLink;
            deploymentVoiceChatLink = data?.deploymentVoiceChatLink;
            deploymentAllowedRoleIds = data?.deploymentAllowedRoleIds;
            deploymentDefaultRoles = data?.deploymentDefaultRoles;
            deploymentTitle = data?.deploymentTitle;
            deploymentVoiceChannel = data?.deploymentVoiceChannel;
            deploymentImageUrl = data?.deploymentImageUrl;
            deploymentPlaceId = data?.deploymentPlaceId;
            deploymentQuickLaunchLink = data?.deploymentQuickLaunchLink;
            deploymentChannelId = data?.deploymentChannelId;

            messageRepostingEnabled = data?.messageReposting?.enabled;
            messageRepostingTargetChannelId = data?.messageReposting?.targetChannelId;
            messageRepostingIntervalMinutes = data?.messageReposting?.repostIntervalMinutes;
            messageRepostingRetentionHours = data?.messageReposting?.messageRetentionHours;
            messageRepostingMinimumMessages = data?.messageReposting?.minimumMessagesForRepost;
            messageRepostingExcludedChannelIds = data?.messageReposting?.excludedChannelIds;

            messageBlacklist = data?.messageBlacklist;
        }
    }

    internal sealed class JSONStructure
    {
        public string? token { get; set; }
        public string? prefix { get; set; }
        public string? spreadsheetCsvUrl { get; set; }
        public string? spreadsheetRosterCsvUrl { get; set; }
        public string? defaultGameLink { get; set; }

        // Config for /enlistuser
        public ulong? enlistPermissionRoleId { get; set; }
        public ulong? enlistLogChannelId { get; set; }
        public string? enlistAltCheckUrl { get; set; }
        public ulong[]? enlistTargetRoleIds { get; set; }

        // Config for ROBLOX enlistment command
        public ulong? robloxEnlistChannelId { get; set; }
        public ulong[]? robloxEnlistBlockedRoleIds { get; set; }

        // Config for deployment command
        public string? deploymentGameLink { get; set; }
        public string? deploymentVoiceChatLink { get; set; }
        public ulong[]? deploymentAllowedRoleIds { get; set; }
        public ulong[]? deploymentDefaultRoles { get; set; }
        public string? deploymentTitle { get; set; }
        public string? deploymentVoiceChannel { get; set; }
        public string? deploymentImageUrl { get; set; }
        public string? deploymentPlaceId { get; set; }
        public string? deploymentQuickLaunchLink { get; set; }
        public ulong? deploymentChannelId { get; set; }

        // Config for message reposting
        public MessageRepostingConfig? messageReposting { get; set; }

        // Config for message blacklist detection
        public MessageBlacklistConfig? messageBlacklist { get; set; }
    }

    internal sealed class MessageRepostingConfig
    {
        public bool enabled { get; set; }
        public ulong targetChannelId { get; set; }
        public int repostIntervalMinutes { get; set; }
        public int messageRetentionHours { get; set; }
        public int minimumMessagesForRepost { get; set; }
        public ulong[]? excludedChannelIds { get; set; }
    }

    internal sealed class MessageBlacklistConfig
    {
        public bool enabled { get; set; }
        public string[]? blacklistedTerms { get; set; }
        public string? responseMessage { get; set; }
        public string? responseMessage2Term { get; set; }
        public string[]? responseMessage2Terms { get; set; }
        public string? responseMessage2 { get; set; }
    }
}
