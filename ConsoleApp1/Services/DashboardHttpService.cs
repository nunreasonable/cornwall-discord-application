using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CornwallUtilities.config;
using DisCatSharp;
using DisCatSharp.Entities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CornwallUtilities.Services
{
    internal sealed class DashboardHttpService
    {
        private readonly DiscordClient _client;
        private readonly DashboardAuthService _auth;
        private readonly HttpListener _listener = new();
        private readonly ConcurrentDictionary<string, PendingLinkCode> _pendingCodes = new();
        private readonly ConcurrentDictionary<string, DashboardSession> _sessions = new();
        private HashSet<string> _allowedOrigins = new(StringComparer.OrdinalIgnoreCase);

        private CancellationTokenSource? _cts;

        public DashboardHttpService(DiscordClient client, DashboardAuthService auth)
        {
            _client = client;
            _auth = auth;
        }

        public async Task StartAsync()
        {
            var config = await _auth.GetConfigAsync();
            var prefix = config.listenUrl;
            if (string.IsNullOrWhiteSpace(prefix))
                prefix = "http://127.0.0.1:5056/";

            if (!prefix.EndsWith('/'))
                prefix += "/";

            _allowedOrigins = config.allowedOrigins
                .Where(o => !string.IsNullOrWhiteSpace(o))
                .Select(o => o.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (_allowedOrigins.Count == 0)
                _allowedOrigins.Add("*");

            _listener.Prefixes.Clear();
            _listener.Prefixes.Add(prefix);
            _listener.Start();

            _cts = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));

            Console.WriteLine($"Dashboard API online at {prefix}");
        }

        public void Stop()
        {
            try
            {
                _cts?.Cancel();
                _listener.Stop();
            }
            catch
            {
            }
        }

        public async Task<(string code, DateTimeOffset expiresAt)> GenerateLinkCodeAsync(DiscordUser user)
        {
            var cfg = await _auth.GetConfigAsync();
            var code = $"CORN-{Random.Shared.Next(100000, 999999)}";
            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(cfg.linkCodeLifetimeMinutes, 1, 30));

            _pendingCodes[code] = new PendingLinkCode
            {
                Code = code,
                UserId = user.Id,
                Username = user.Username,
                AvatarUrl = user.AvatarUrl,
                ExpiresAt = expiresAt
            };

            return (code, expiresAt);
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch
                {
                    if (ct.IsCancellationRequested)
                        break;
                    continue;
                }

                _ = Task.Run(() => ProcessContextAsync(ctx));
            }
        }

        private async Task ProcessContextAsync(HttpListenerContext ctx)
        {
            try
            {
                AddCorsHeaders(ctx.Request, ctx.Response);
                if (ctx.Request.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Response.StatusCode = 204;
                    ctx.Response.Close();
                    return;
                }

                var path = ctx.Request.Url?.AbsolutePath?.TrimEnd('/') ?? string.Empty;
                if (string.IsNullOrWhiteSpace(path))
                    path = "/";

                if (ctx.Request.HttpMethod == "GET" && path == "/api/health")
                {
                    await WriteJsonAsync(ctx.Response, 200, new { ok = true, timestamp = DateTimeOffset.UtcNow });
                    return;
                }

                if (ctx.Request.HttpMethod == "POST" && path == "/api/auth/login")
                {
                    await HandleLoginAsync(ctx);
                    return;
                }

                if (ctx.Request.HttpMethod == "POST" && path == "/api/auth/logout")
                {
                    await HandleLogoutAsync(ctx);
                    return;
                }

                if (ctx.Request.HttpMethod == "GET" && path == "/api/auth/me")
                {
                    await HandleMeAsync(ctx);
                    return;
                }

                if (ctx.Request.HttpMethod == "GET" && path == "/api/audit")
                {
                    await HandleAuditAsync(ctx);
                    return;
                }

                if (ctx.Request.HttpMethod == "POST" && path == "/api/messages/send")
                {
                    await HandleSendMessageAsync(ctx);
                    return;
                }

                if (ctx.Request.HttpMethod == "POST" && path == "/api/roles/add")
                {
                    await HandleRoleChangeAsync(ctx, true);
                    return;
                }

                if (ctx.Request.HttpMethod == "POST" && path == "/api/roles/remove")
                {
                    await HandleRoleChangeAsync(ctx, false);
                    return;
                }

                if (ctx.Request.HttpMethod == "POST" && path == "/api/punishments/timeout")
                {
                    await HandleTimeoutAsync(ctx);
                    return;
                }

                if (ctx.Request.HttpMethod == "POST" && path == "/api/punishments/remove-from-regiment")
                {
                    await HandleRemoveFromRegimentAsync(ctx);
                    return;
                }

                await WriteJsonAsync(ctx.Response, 404, new { error = "Rota não encontrada." });
            }
            catch (Exception ex)
            {
                await WriteJsonAsync(ctx.Response, 500, new { error = "Erro interno no dashboard.", details = ex.Message });
            }
        }

        private async Task HandleLoginAsync(HttpListenerContext ctx)
        {
            var body = await ReadBodyAsJsonAsync(ctx.Request);
            var code = (string?)body?["linkCode"];
            if (string.IsNullOrWhiteSpace(code))
            {
                await WriteJsonAsync(ctx.Response, 400, new { error = "linkCode é obrigatório." });
                return;
            }

            CleanupExpiredCodes();

            if (!_pendingCodes.TryRemove(code.Trim(), out var pending) || pending.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                await WriteJsonAsync(ctx.Response, 401, new { error = "Código inválido ou expirado. Gere um novo com /dashboardlink." });
                return;
            }

            var permissionResult = await _auth.ResolvePermissionLevelAsync(_client, pending.UserId);
            if (permissionResult.PermissionLevel <= 0)
            {
                if (permissionResult.HadLookupFailure)
                {
                    await WriteJsonAsync(ctx.Response, 503, new { error = "Nao foi possivel validar permissoes no Discord agora. Tente novamente em alguns segundos." });
                    return;
                }

                await WriteJsonAsync(ctx.Response, 403, new { error = "Usuário sem permissão para o dashboard." });
                return;
            }

            await _auth.ValidateAndUpsertWhitelistAsync(pending.UserId, pending.Username, permissionResult.PermissionLevel);

            var cfg = await _auth.GetConfigAsync();
            var sessionLifetime = Math.Clamp(cfg.sessionLifetimeMinutes, 5, 240);
            var token = Guid.NewGuid().ToString("N");
            var session = new DashboardSession
            {
                Token = token,
                UserId = pending.UserId,
                Username = pending.Username,
                AvatarUrl = pending.AvatarUrl,
                PermissionLevel = permissionResult.PermissionLevel,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(sessionLifetime)
            };

            _sessions[token] = session;

            await _auth.AppendAuditAsync(new DashboardAuditEntry
            {
                timestampUtc = DateTimeOffset.UtcNow,
                actorUserId = session.UserId,
                actorUsername = session.Username,
                actorAvatarUrl = session.AvatarUrl,
                action = "LOGIN",
                details = "Login efetuado via link do bot."
            });

            await WriteJsonAsync(ctx.Response, 200, new
            {
                token,
                user = new
                {
                    id = session.UserId,
                    username = session.Username,
                    avatarUrl = session.AvatarUrl,
                    permissionLevel = session.PermissionLevel,
                    permissionName = GetPermissionName(session.PermissionLevel)
                }
            });
        }

        private async Task HandleLogoutAsync(HttpListenerContext ctx)
        {
            var session = TryGetSession(ctx.Request);
            if (session is not null)
            {
                _sessions.TryRemove(session.Token, out _);
            }

            await WriteJsonAsync(ctx.Response, 200, new { ok = true });
        }

        private async Task HandleMeAsync(HttpListenerContext ctx)
        {
            var session = TryGetSession(ctx.Request);
            if (session is null)
            {
                await WriteJsonAsync(ctx.Response, 401, new { error = "Sessão inválida." });
                return;
            }

            await WriteJsonAsync(ctx.Response, 200, new
            {
                user = new
                {
                    id = session.UserId,
                    username = session.Username,
                    avatarUrl = session.AvatarUrl,
                    permissionLevel = session.PermissionLevel,
                    permissionName = GetPermissionName(session.PermissionLevel),
                    expiresAt = session.ExpiresAt
                }
            });
        }

        private async Task HandleAuditAsync(HttpListenerContext ctx)
        {
            var session = TryGetSession(ctx.Request);
            if (session is null)
            {
                await WriteJsonAsync(ctx.Response, 401, new { error = "Sessão inválida." });
                return;
            }

            var logs = await _auth.GetAuditAsync(200);
            await WriteJsonAsync(ctx.Response, 200, new { logs });
        }

        private async Task HandleSendMessageAsync(HttpListenerContext ctx)
        {
            var session = RequirePermission(ctx.Request, 1);
            if (session is null)
            {
                await WriteJsonAsync(ctx.Response, 403, new { error = "Permissão insuficiente." });
                return;
            }

            var body = await ReadBodyAsJsonAsync(ctx.Request);
            var channelId = ReadSnowflake(body, "channelId");
            var message = (string?)body?["message"];
            if (!channelId.HasValue || string.IsNullOrWhiteSpace(message))
            {
                await WriteJsonAsync(ctx.Response, 400, new { error = "channelId e message são obrigatórios." });
                return;
            }

            try
            {
                var channel = await _client.GetChannelAsync(channelId.Value);
                var chunkCount = 0;
                foreach (var chunk in SplitMessageForDiscord(message))
                {
                    await channel.SendMessageAsync(chunk);
                    chunkCount++;
                }

                await AuditAsync(session, "SEND_MESSAGE", $"Enviou mensagem para o canal {channelId} em {chunkCount} parte(s).");
                await WriteJsonAsync(ctx.Response, 200, new { ok = true });
            }
            catch (Exception ex)
            {
                await WriteJsonAsync(ctx.Response, 400, new { error = "Falha ao enviar mensagem.", details = ex.Message });
            }
        }

        private static IEnumerable<string> SplitMessageForDiscord(string message)
        {
            const int maxLength = 2000;

            for (var index = 0; index < message.Length; index += maxLength)
            {
                var length = Math.Min(maxLength, message.Length - index);
                yield return message.Substring(index, length);
            }
        }

        private async Task HandleRoleChangeAsync(HttpListenerContext ctx, bool add)
        {
            var session = RequirePermission(ctx.Request, 3);
            if (session is null)
            {
                await WriteJsonAsync(ctx.Response, 403, new { error = "Permissão insuficiente para gerenciar cargos." });
                return;
            }

            var body = await ReadBodyAsJsonAsync(ctx.Request);
            var userId = ReadSnowflake(body, "userId");
            var roleId = ReadSnowflake(body, "roleId");
            var reason = ((string?)body?["reason"])?.Trim();

            if (!userId.HasValue || !roleId.HasValue)
            {
                await WriteJsonAsync(ctx.Response, 400, new { error = "userId e roleId são obrigatórios." });
                return;
            }

            var config = await _auth.GetConfigAsync();
            try
            {
                var guild = await _client.GetGuildAsync(config.guildId);
                var member = await guild.GetMemberAsync(userId.Value);
                var role = guild.GetRole(roleId.Value);
                if (role is null)
                {
                    await WriteJsonAsync(ctx.Response, 400, new { error = $"Cargo {roleId} não existe neste servidor." });
                    return;
                }

                if (add)
                    await member.GrantRoleAsync(role, reason);
                else
                    await member.RevokeRoleAsync(role, reason);

                await AuditAsync(session, add ? "ADD_ROLE" : "REMOVE_ROLE", $"{(add ? "Adicionou" : "Removeu")} cargo {roleId} do usuário {userId}.");
                await WriteJsonAsync(ctx.Response, 200, new { ok = true });
            }
            catch (Exception ex)
            {
                await WriteJsonAsync(ctx.Response, 400, new { error = "Falha ao alterar cargo.", details = ex.Message });
            }
        }

        private async Task HandleTimeoutAsync(HttpListenerContext ctx)
        {
            var session = RequirePermission(ctx.Request, 2);
            if (session is null)
            {
                await WriteJsonAsync(ctx.Response, 403, new { error = "Permissão insuficiente para timeout." });
                return;
            }

            var body = await ReadBodyAsJsonAsync(ctx.Request);
            var userId = ReadSnowflake(body, "userId");
            var durationMinutes = (int?)body?["durationMinutes"];
            var reason = ((string?)body?["reason"])?.Trim();

            if (!userId.HasValue || !durationMinutes.HasValue || durationMinutes.Value <= 0)
            {
                await WriteJsonAsync(ctx.Response, 400, new { error = "userId e durationMinutes válidos são obrigatórios." });
                return;
            }

            var config = await _auth.GetConfigAsync();
            try
            {
                var guild = await _client.GetGuildAsync(config.guildId);
                var member = await guild.GetMemberAsync(userId.Value);

                await member.TimeoutAsync(DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(durationMinutes.Value, 1, 40320)), reason);

                await TrySendPunishmentDmAsync(member, "Timeout", reason);
                await AuditAsync(session, "TIMEOUT", $"Aplicou timeout em {userId} por {durationMinutes} minuto(s).");
                await WriteJsonAsync(ctx.Response, 200, new { ok = true });
            }
            catch (Exception ex)
            {
                await WriteJsonAsync(ctx.Response, 400, new { error = "Falha ao aplicar timeout.", details = ex.Message });
            }
        }

        private async Task HandleRemoveFromRegimentAsync(HttpListenerContext ctx)
        {
            var session = RequirePermission(ctx.Request, 2);
            if (session is null)
            {
                await WriteJsonAsync(ctx.Response, 403, new { error = "Permissão insuficiente para remover do regimento." });
                return;
            }

            var body = await ReadBodyAsJsonAsync(ctx.Request);
            var userId = ReadSnowflake(body, "userId");
            var reason = ((string?)body?["reason"])?.Trim();

            if (!userId.HasValue)
            {
                await WriteJsonAsync(ctx.Response, 400, new { error = "userId é obrigatório." });
                return;
            }

            var config = await _auth.GetConfigAsync();
            try
            {
                var guild = await _client.GetGuildAsync(config.guildId);
                var member = await guild.GetMemberAsync(userId.Value);

                foreach (var roleId in config.regimentRoleIds)
                {
                    var role = guild.GetRole(roleId);
                    if (role is not null && member.Roles.Any(r => r.Id == roleId))
                    {
                        await member.RevokeRoleAsync(role, reason);
                    }
                }

                await TrySendPunishmentDmAsync(member, "Remoção do regimento", reason);
                await AuditAsync(session, "REMOVE_FROM_REGIMENT", $"Removeu do regimento o usuário {userId}.");
                await WriteJsonAsync(ctx.Response, 200, new { ok = true });
            }
            catch (Exception ex)
            {
                await WriteJsonAsync(ctx.Response, 400, new { error = "Falha ao remover usuário do regimento.", details = ex.Message });
            }
        }

        private async Task TrySendPunishmentDmAsync(DiscordMember member, string punishmentType, string? reason)
        {
            try
            {
                var dm = await member.CreateDmChannelAsync();
                var text = $"Você recebeu a punição: {punishmentType}.";
                if (!string.IsNullOrWhiteSpace(reason))
                    text += $" Motivo: {reason}";
                await dm.SendMessageAsync(text);
            }
            catch
            {
            }
        }

        private async Task AuditAsync(DashboardSession actor, string action, string details)
        {
            await _auth.AppendAuditAsync(new DashboardAuditEntry
            {
                timestampUtc = DateTimeOffset.UtcNow,
                actorUserId = actor.UserId,
                actorUsername = actor.Username,
                actorAvatarUrl = actor.AvatarUrl,
                action = action,
                details = details
            });
        }

        private DashboardSession? TryGetSession(HttpListenerRequest request)
        {
            CleanupExpiredSessions();

            var authHeader = request.Headers["Authorization"];
            if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return null;

            var token = authHeader.Substring("Bearer ".Length).Trim();
            if (!_sessions.TryGetValue(token, out var session))
                return null;

            if (session.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                _sessions.TryRemove(token, out _);
                return null;
            }

            return session;
        }

        private DashboardSession? RequirePermission(HttpListenerRequest request, int minLevel)
        {
            var session = TryGetSession(request);
            if (session is null)
                return null;

            return session.PermissionLevel >= minLevel ? session : null;
        }

        private static async Task<JObject?> ReadBodyAsJsonAsync(HttpListenerRequest request)
        {
            if (!request.HasEntityBody)
                return null;

            using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
            var raw = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            return JObject.Parse(raw);
        }

        private static async Task WriteJsonAsync(HttpListenerResponse response, int statusCode, object payload)
        {
            var json = JsonConvert.SerializeObject(payload, Formatting.Indented);
            var buffer = Encoding.UTF8.GetBytes(json);

            response.StatusCode = statusCode;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            response.Close();
        }

        private void AddCorsHeaders(HttpListenerRequest request, HttpListenerResponse response)
        {
            var origin = request.Headers["Origin"];
            if (_allowedOrigins.Contains("*"))
            {
                response.Headers["Access-Control-Allow-Origin"] = "*";
            }
            else if (!string.IsNullOrWhiteSpace(origin) && _allowedOrigins.Contains(origin))
            {
                response.Headers["Access-Control-Allow-Origin"] = origin;
                response.Headers["Vary"] = "Origin";
            }

            response.Headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type";
            response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        }

        private static ulong? ReadSnowflake(JObject? body, string field)
        {
            var token = body?[field];
            if (token is null)
                return null;

            if (token.Type == JTokenType.Integer)
                return token.Value<ulong>();

            if (token.Type == JTokenType.String)
            {
                var raw = token.Value<string>()?.Trim();
                if (ulong.TryParse(raw, out var parsed))
                    return parsed;
            }

            return null;
        }

        private static string GetPermissionName(int level)
        {
            return level switch
            {
                4 => "Developer",
                3 => "General Staff",
                2 => "Regimental Command",
                1 => "NCO/Officer",
                _ => "Unknown"
            };
        }

        private void CleanupExpiredCodes()
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var kv in _pendingCodes)
            {
                if (kv.Value.ExpiresAt <= now)
                    _pendingCodes.TryRemove(kv.Key, out _);
            }
        }

        private void CleanupExpiredSessions()
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var kv in _sessions)
            {
                if (kv.Value.ExpiresAt <= now)
                    _sessions.TryRemove(kv.Key, out _);
            }
        }

        private sealed class PendingLinkCode
        {
            public string Code { get; set; } = string.Empty;
            public ulong UserId { get; set; }
            public string Username { get; set; } = string.Empty;
            public string AvatarUrl { get; set; } = string.Empty;
            public DateTimeOffset ExpiresAt { get; set; }
        }

        private sealed class DashboardSession
        {
            public string Token { get; set; } = string.Empty;
            public ulong UserId { get; set; }
            public string Username { get; set; } = string.Empty;
            public string AvatarUrl { get; set; } = string.Empty;
            public int PermissionLevel { get; set; }
            public DateTimeOffset ExpiresAt { get; set; }
        }
    }
}
