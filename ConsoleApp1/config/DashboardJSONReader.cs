using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace CornwallUtilities.config
{
    internal sealed class DashboardJSONReader
    {
        private readonly string _path;
        private readonly SemaphoreSlim _fileLock = new(1, 1);

        public DashboardJSONReader(string path = "config/dashboard_auth.json")
        {
            _path = path;
        }

        public async Task<DashboardConfigStructure> ReadAsync()
        {
            await _fileLock.WaitAsync();
            try
            {
                if (!File.Exists(_path))
                {
                    var defaultConfig = new DashboardConfigStructure();
                    var defaultJson = JsonConvert.SerializeObject(defaultConfig, Formatting.Indented);
                    await File.WriteAllTextAsync(_path, defaultJson);
                    return defaultConfig;
                }

                var json = await File.ReadAllTextAsync(_path);
                var data = JsonConvert.DeserializeObject<DashboardConfigStructure>(json);
                return data ?? new DashboardConfigStructure();
            }
            finally
            {
                _fileLock.Release();
            }
        }

        public async Task WriteAsync(DashboardConfigStructure data)
        {
            await _fileLock.WaitAsync();
            try
            {
                var json = JsonConvert.SerializeObject(data, Formatting.Indented);
                await File.WriteAllTextAsync(_path, json);
            }
            finally
            {
                _fileLock.Release();
            }
        }
    }

    internal sealed class DashboardConfigStructure
    {
        public string listenUrl { get; set; } = "http://127.0.0.1:5056/";
        public string[] allowedOrigins { get; set; } = new[] { "*" };
        public ulong guildId { get; set; }
        public ulong[] level4UserIds { get; set; } = Array.Empty<ulong>();
        public ulong[] level3RoleIds { get; set; } = Array.Empty<ulong>();
        public ulong[] level2RoleIds { get; set; } = Array.Empty<ulong>();
        public ulong[] level1RoleIds { get; set; } = Array.Empty<ulong>();
        public ulong[] regimentRoleIds { get; set; } = Array.Empty<ulong>();
        public int linkCodeLifetimeMinutes { get; set; } = 10;
        public int sessionLifetimeMinutes { get; set; } = 60;
        public List<DashboardWhitelistedUser> whitelistedUsers { get; set; } = new();
        public List<DashboardAuditEntry> auditLog { get; set; } = new();
    }

    internal sealed class DashboardWhitelistedUser
    {
        public ulong userId { get; set; }
        public string username { get; set; } = string.Empty;
        public int permissionLevel { get; set; }
        public DateTimeOffset firstWhitelistedAt { get; set; }
        public DateTimeOffset lastValidatedAt { get; set; }
    }

    internal sealed class DashboardAuditEntry
    {
        public DateTimeOffset timestampUtc { get; set; }
        public ulong actorUserId { get; set; }
        public string actorUsername { get; set; } = string.Empty;
        public string actorAvatarUrl { get; set; } = string.Empty;
        public string action { get; set; } = string.Empty;
        public string details { get; set; } = string.Empty;
    }
}
