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
        private readonly string _whitelistPath;
        private readonly string _auditLogPath;
        private readonly SemaphoreSlim _fileLock = new(1, 1);

        public DashboardJSONReader(string path = "config/dashboard_auth.json")
        {
            _path = path;
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = ".";
            }

            _whitelistPath = Path.Combine(directory, "dashboard_whitelisted_users.json");
            _auditLogPath = Path.Combine(directory, "dashboard_audit_log.json");
        }

        public async Task<DashboardConfigStructure> ReadAsync()
        {
            await _fileLock.WaitAsync();
            try
            {
                var legacyConfig = new DashboardConfigStructure();
                DashboardCoreConfigStructure coreConfig;
                var rewriteCore = false;

                if (!File.Exists(_path))
                {
                    coreConfig = new DashboardCoreConfigStructure();
                    await WriteJsonAsync(_path, coreConfig);
                }
                else
                {
                    var json = await File.ReadAllTextAsync(_path);
                    coreConfig = JsonConvert.DeserializeObject<DashboardCoreConfigStructure>(json) ?? new DashboardCoreConfigStructure();
                    legacyConfig = JsonConvert.DeserializeObject<DashboardConfigStructure>(json) ?? new DashboardConfigStructure();
                    rewriteCore = json.Contains("\"whitelistedUsers\"", StringComparison.Ordinal)
                        || json.Contains("\"auditLog\"", StringComparison.Ordinal);
                }

                var whitelistedUsers = await ReadJsonAsync(
                    _whitelistPath,
                    legacyConfig.whitelistedUsers ?? new List<DashboardWhitelistedUser>());
                var auditLog = await ReadJsonAsync(
                    _auditLogPath,
                    legacyConfig.auditLog ?? new List<DashboardAuditEntry>());

                if (rewriteCore)
                {
                    await WriteJsonAsync(_path, coreConfig);
                }

                return MergeConfig(coreConfig, whitelistedUsers, auditLog);
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
                var coreConfig = ToCoreConfig(data);
                await WriteJsonAsync(_path, coreConfig);
                await WriteJsonAsync(_whitelistPath, data.whitelistedUsers ?? new List<DashboardWhitelistedUser>());
                await WriteJsonAsync(_auditLogPath, data.auditLog ?? new List<DashboardAuditEntry>());
            }
            finally
            {
                _fileLock.Release();
            }
        }

        private static DashboardConfigStructure MergeConfig(
            DashboardCoreConfigStructure coreConfig,
            List<DashboardWhitelistedUser> whitelistedUsers,
            List<DashboardAuditEntry> auditLog)
        {
            return new DashboardConfigStructure
            {
                listenUrl = coreConfig.listenUrl,
                allowedOrigins = coreConfig.allowedOrigins,
                guildId = coreConfig.guildId,
                level4UserIds = coreConfig.level4UserIds,
                level3RoleIds = coreConfig.level3RoleIds,
                level2RoleIds = coreConfig.level2RoleIds,
                level1RoleIds = coreConfig.level1RoleIds,
                regimentRoleIds = coreConfig.regimentRoleIds,
                linkCodeLifetimeMinutes = coreConfig.linkCodeLifetimeMinutes,
                sessionLifetimeMinutes = coreConfig.sessionLifetimeMinutes,
                whitelistedUsers = whitelistedUsers,
                auditLog = auditLog
            };
        }

        private static DashboardCoreConfigStructure ToCoreConfig(DashboardConfigStructure data)
        {
            return new DashboardCoreConfigStructure
            {
                listenUrl = data.listenUrl,
                allowedOrigins = data.allowedOrigins,
                guildId = data.guildId,
                level4UserIds = data.level4UserIds,
                level3RoleIds = data.level3RoleIds,
                level2RoleIds = data.level2RoleIds,
                level1RoleIds = data.level1RoleIds,
                regimentRoleIds = data.regimentRoleIds,
                linkCodeLifetimeMinutes = data.linkCodeLifetimeMinutes,
                sessionLifetimeMinutes = data.sessionLifetimeMinutes
            };
        }

        private static async Task<T> ReadJsonAsync<T>(string path, T defaultValue)
        {
            if (!File.Exists(path))
            {
                await WriteJsonAsync(path, defaultValue);
                return defaultValue;
            }

            var json = await File.ReadAllTextAsync(path);
            var data = JsonConvert.DeserializeObject<T>(json);
            return data ?? defaultValue;
        }

        /// <summary>
        /// Escrita atomica (.tmp + File.Move), igual a do AuditStore. Uma queda no
        /// meio de um WriteAllText truncava o arquivo - e aqui estao a whitelist e
        /// o log de auditoria do dashboard.
        /// </summary>
        private static async Task WriteJsonAsync<T>(string path, T data)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var json = JsonConvert.SerializeObject(data, Formatting.Indented);
            var tmp = path + ".tmp";

            await File.WriteAllTextAsync(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
    }

    internal sealed class DashboardCoreConfigStructure
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
