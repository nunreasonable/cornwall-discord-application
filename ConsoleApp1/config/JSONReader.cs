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
    }
}
