using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CornwallUtilities.config;
using DisCatSharp;

namespace CornwallUtilities.Services
{
    internal sealed class PermissionResolutionResult
    {
        public int PermissionLevel { get; set; }
        public bool HadLookupFailure { get; set; }
    }

    internal sealed class DashboardAuthService
    {
        private readonly DashboardJSONReader _reader;
        private readonly SemaphoreSlim _mutex = new(1, 1);

        public DashboardAuthService(string configPath = "config/dashboard_auth.json")
        {
            _reader = new DashboardJSONReader(configPath);
        }

        public async Task<DashboardConfigStructure> GetConfigAsync()
        {
            return await _reader.ReadAsync();
        }

        public async Task<PermissionResolutionResult> ResolvePermissionLevelAsync(DiscordClient client, ulong userId)
        {
            var config = await _reader.ReadAsync();

            if (config.level4UserIds.Contains(userId))
            {
                return new PermissionResolutionResult
                {
                    PermissionLevel = 4,
                    HadLookupFailure = false
                };
            }

            if (config.guildId == 0)
            {
                return new PermissionResolutionResult
                {
                    PermissionLevel = 0,
                    HadLookupFailure = false
                };
            }

            Exception? lastException = null;
            try
            {
                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        var guild = await client.GetGuildAsync(config.guildId);
                        var member = await guild.GetMemberAsync(userId);
                        var roleIds = member.Roles.Select(r => r.Id).ToHashSet();

                        if (config.level3RoleIds.Any(roleIds.Contains))
                        {
                            return new PermissionResolutionResult
                            {
                                PermissionLevel = 3,
                                HadLookupFailure = false
                            };
                        }

                        if (config.level2RoleIds.Any(roleIds.Contains))
                        {
                            return new PermissionResolutionResult
                            {
                                PermissionLevel = 2,
                                HadLookupFailure = false
                            };
                        }

                        if (config.level1RoleIds.Any(roleIds.Contains))
                        {
                            return new PermissionResolutionResult
                            {
                                PermissionLevel = 1,
                                HadLookupFailure = false
                            };
                        }

                        return new PermissionResolutionResult
                        {
                            PermissionLevel = 0,
                            HadLookupFailure = false
                        };
                    }
                    catch (Exception ex)
                    {
                        lastException = ex;
                        if (attempt < 3)
                        {
                            await Task.Delay(200 * attempt);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                lastException = ex;
            }

            Console.WriteLine($"[DashboardAuth] Falha ao validar permissao do usuario {userId}: {lastException?.Message}");
            return new PermissionResolutionResult
            {
                PermissionLevel = 0,
                HadLookupFailure = true
            };
        }

        public async Task<(DashboardWhitelistedUser user, bool addedNew)> ValidateAndUpsertWhitelistAsync(ulong userId, string username, int permissionLevel)
        {
            await _mutex.WaitAsync();
            try
            {
                var config = await _reader.ReadAsync();
                var existing = config.whitelistedUsers.FirstOrDefault(u => u.userId == userId);
                if (existing is null)
                {
                    var created = new DashboardWhitelistedUser
                    {
                        userId = userId,
                        username = username,
                        permissionLevel = permissionLevel,
                        firstWhitelistedAt = DateTimeOffset.UtcNow,
                        lastValidatedAt = DateTimeOffset.UtcNow
                    };
                    config.whitelistedUsers.Add(created);
                    await _reader.WriteAsync(config);
                    return (created, true);
                }

                existing.username = username;
                existing.permissionLevel = permissionLevel;
                existing.lastValidatedAt = DateTimeOffset.UtcNow;
                await _reader.WriteAsync(config);
                return (existing, false);
            }
            finally
            {
                _mutex.Release();
            }
        }

        public async Task AppendAuditAsync(DashboardAuditEntry entry)
        {
            await _mutex.WaitAsync();
            try
            {
                var config = await _reader.ReadAsync();
                config.auditLog.Add(entry);

                const int keepMax = 500;
                if (config.auditLog.Count > keepMax)
                {
                    config.auditLog = config.auditLog
                        .OrderByDescending(a => a.timestampUtc)
                        .Take(keepMax)
                        .OrderBy(a => a.timestampUtc)
                        .ToList();
                }

                await _reader.WriteAsync(config);
            }
            finally
            {
                _mutex.Release();
            }
        }

        public async Task<IReadOnlyList<DashboardAuditEntry>> GetAuditAsync(int take = 200)
        {
            var config = await _reader.ReadAsync();
            return config.auditLog
                .OrderByDescending(a => a.timestampUtc)
                .Take(take)
                .ToList();
        }
    }
}
