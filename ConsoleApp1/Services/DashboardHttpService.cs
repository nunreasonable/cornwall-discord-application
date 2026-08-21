using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CornwallUtilities.config;
using CornwallUtilities.Services.Audit;
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

        // Tentativas de login por origem. O codigo de acesso e curto por
        // necessidade (alguem digita ele), entao o que impede a forca bruta e
        // esta janela - sem ela daria para varrer o espaco inteiro de codigos
        // enquanto um deles esta valido.
        private readonly ConcurrentDictionary<string, LoginAttempts> _loginAttempts = new();

        /// <summary>Alfabeto sem caracteres ambiguos (0/O, 1/I/L) - o codigo e digitado a mao.</summary>
        private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

        private const int MaxLoginAttemptsPerWindow = 10;
        private static readonly TimeSpan LoginAttemptWindow = TimeSpan.FromMinutes(5);

        /// <summary>De quanto em quanto tempo a permissao de uma sessao viva e reconferida no Discord.</summary>
        private static readonly TimeSpan PermissionRefreshInterval = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Espera antes de tentar de novo quando a consulta de permissao FALHOU.
        ///
        /// Curta de proposito: e o intervalo em que alguem que acabou de perder o
        /// cargo continua com o nivel antigo, entao nao pode ser da ordem do
        /// PermissionRefreshInterval. Longa o bastante para que uma instabilidade
        /// do Discord nao seja repaginada a cada request.
        /// </summary>
        private static readonly TimeSpan PermissionRetryBackoff = TimeSpan.FromSeconds(30);
        private HashSet<string> _allowedOrigins = new(StringComparer.OrdinalIgnoreCase);

        private CancellationTokenSource? _cts;

        /// <summary>Cache do retrato publico de status - ver GetCachedPublicStatus.</summary>
        private static readonly TimeSpan StatusCacheTtl = TimeSpan.FromSeconds(10);
        private readonly object _statusCacheLock = new();
        private PublicStatus? _cachedStatus;
        private DateTimeOffset _cachedStatusAt;

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
            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(cfg.linkCodeLifetimeMinutes, 1, 30));

            CleanupExpiredCodes();

            // Um codigo por pessoa: pedir um novo invalida o anterior, entao um
            // codigo esquecido num chat nao continua valendo.
            foreach (var kv in _pendingCodes)
                if (kv.Value.UserId == user.Id)
                    _pendingCodes.TryRemove(kv.Key, out _);

            // TryAdd (e nao indexador) porque duas pessoas podem sortear o mesmo
            // codigo: sobrescrever faria o codigo da primeira logar como a
            // segunda. Na colisao, sorteia de novo.
            string code;
            var pending = new PendingLinkCode
            {
                UserId = user.Id,
                Username = user.Username,
                AvatarUrl = user.AvatarUrl,
                ExpiresAt = expiresAt
            };

            do
            {
                code = GenerateCode();
                pending.Code = code;
            }
            while (!_pendingCodes.TryAdd(code, pending));

            return (code, expiresAt);
        }

        /// <summary>
        /// Codigo de acesso ao dashboard.
        ///
        /// Usa RandomNumberGenerator, nao Random: quem alcanca a porta do
        /// dashboard consegue tentar codigos, e um gerador previsivel de 6
        /// digitos deixava esse chute barato demais. Sao 10 caracteres de um
        /// alfabeto de 31 (~49 bits), o que torna a busca inviavel dentro dos
        /// poucos minutos de vida do codigo.
        /// </summary>
        private static string GenerateCode()
        {
            var chars = new char[10];
            for (var i = 0; i < chars.Length; i++)
                chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];

            return $"CORN-{new string(chars, 0, 5)}-{new string(chars, 5, 5)}";
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
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested || !_listener.IsListening)
                        break;

                    // Sem esta pausa, um listener que falha sem parar de escutar
                    // punha o laco a girar sozinho consumindo uma CPU inteira.
                    Console.WriteLine($"[dashboard] falha ao aceitar conexao: {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
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

                if (ctx.Request.HttpMethod == "GET" && path == "/api/status")
                {
                    await HandleStatusAsync(ctx);
                    return;
                }

                if (ctx.Request.HttpMethod == "GET" && path == "/api/logs")
                {
                    await HandleLogsAsync(ctx);
                    return;
                }

                if (ctx.Request.HttpMethod == "GET" && path == "/api/audit/roster")
                {
                    await HandleAuditRosterAsync(ctx);
                    return;
                }

                if (ctx.Request.HttpMethod == "GET" && path == "/api/audit/history")
                {
                    await HandleAuditHistoryAsync(ctx);
                    return;
                }

                if (ctx.Request.HttpMethod == "POST" && path == "/api/audit/entry")
                {
                    await HandleAuditEntryAsync(ctx);
                    return;
                }

                if (ctx.Request.HttpMethod == "POST" && path == "/api/audit/ranks")
                {
                    await HandleAuditRanksAsync(ctx);
                    return;
                }

                if (ctx.Request.HttpMethod == "POST" && path == "/api/audit/push")
                {
                    await HandleAuditPushAsync(ctx);
                    return;
                }

                if (ctx.Request.HttpMethod == "GET" && path == "/api/promotions")
                {
                    await HandlePromotionsAsync(ctx);
                    return;
                }

                if (ctx.Request.HttpMethod == "POST" && path == "/api/deployment")
                {
                    await HandleDeploymentAsync(ctx);
                    return;
                }

                if (ctx.Request.HttpMethod == "POST" && path == "/api/dm")
                {
                    await HandleDmStartAsync(ctx);
                    return;
                }

                if (ctx.Request.HttpMethod == "GET" && path == "/api/dm/status")
                {
                    await HandleDmStatusAsync(ctx);
                    return;
                }

                if (ctx.Request.HttpMethod == "POST" && path == "/api/enlist")
                {
                    await HandleEnlistAsync(ctx);
                    return;
                }

                await WriteJsonAsync(ctx.Response, 404, new { error = "Rota não encontrada." });
            }
            catch (JsonException)
            {
                await TryWriteJsonAsync(ctx.Response, 400, new { error = "Corpo da requisição não é um JSON válido." });
            }
            catch (Exception ex)
            {
                // O detalhe fica no log do bot, nao na resposta: mensagem de
                // excecao vazando para o cliente e superficie de informacao de
                // graca para quem estiver sondando a API.
                Console.WriteLine($"[dashboard] erro em {ctx.Request.HttpMethod} {ctx.Request.Url?.AbsolutePath}: {ex}");
                await TryWriteJsonAsync(ctx.Response, 500, new { error = "Erro interno no dashboard." });
            }
        }

        private async Task HandleLoginAsync(HttpListenerContext ctx)
        {
            // A trava vem antes de qualquer trabalho: e ela que impede varrer
            // codigos, e nao adianta pagar leitura de config para uma tentativa
            // que ja passou do limite.
            if (!RegisterLoginAttempt(ctx.Request))
            {
                await WriteJsonAsync(ctx.Response, 429, new { error = "Tentativas demais. Espere alguns minutos e tente de novo." });
                return;
            }

            JObject? body;
            try
            {
                body = await ReadBodyAsJsonAsync(ctx.Request);
            }
            catch (JsonException)
            {
                await WriteJsonAsync(ctx.Response, 400, new { error = "Corpo da requisição não é um JSON válido." });
                return;
            }

            var code = ReadString(body, "linkCode");
            if (string.IsNullOrWhiteSpace(code))
            {
                await WriteJsonAsync(ctx.Response, 400, new { error = "linkCode é obrigatório." });
                return;
            }

            CleanupExpiredCodes();

            if (!_pendingCodes.TryRemove(code.Trim().ToUpperInvariant(), out var pending) || pending.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                await WriteJsonAsync(ctx.Response, 401, new { error = "Código inválido ou expirado. Gere um novo com /dashboardlink." });
                return;
            }

            // Codigo valido: a origem deixa de estar sob suspeita.
            ClearLoginAttempts(ctx.Request);

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
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(sessionLifetime),
                NextPermissionCheckAt = DateTimeOffset.UtcNow.Add(PermissionRefreshInterval)
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
            var session = await RequirePermissionAsync(ctx.Request, 1);
            if (session is null)
            {
                await WriteAuthFailureAsync(ctx, "Permissão insuficiente.");
                return;
            }

            var body = await ReadBodyAsJsonAsync(ctx.Request);
            var channelId = ReadSnowflake(body, "channelId");
            var message = ReadString(body, "message");
            if (!channelId.HasValue || string.IsNullOrWhiteSpace(message))
            {
                await WriteJsonAsync(ctx.Response, 400, new { error = "channelId e message são obrigatórios." });
                return;
            }

            try
            {
                var channel = await _client.GetChannelAsync(channelId.Value);

                // O canal precisa ser do servidor configurado. Sem esta checagem,
                // um nivel 1 podia mandar o bot escrever em QUALQUER canal de
                // qualquer servidor onde ele esteja, bastando saber o id.
                var config = await _auth.GetConfigAsync();
                if (channel is null || channel.GuildId != config.guildId)
                {
                    await WriteJsonAsync(ctx.Response, 400, new { error = "Canal não pertence ao servidor do regimento." });
                    return;
                }

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
            var session = await // Cargo entra e sai por aqui; conceder cargo pode escalar privilegio.
            RequirePermissionAsync(ctx.Request, 2);
            if (session is null)
            {
                await WriteAuthFailureAsync(ctx, "Permissão insuficiente para gerenciar cargos.");
                return;
            }

            var body = await ReadBodyAsJsonAsync(ctx.Request);
            var userId = ReadSnowflake(body, "userId");
            var roleId = ReadSnowflake(body, "roleId");
            var reason = ReadString(body, "reason")?.Trim();

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
            var session = await RequirePermissionAsync(ctx.Request, 2);
            if (session is null)
            {
                await WriteAuthFailureAsync(ctx, "Permissão insuficiente para timeout.");
                return;
            }

            var body = await ReadBodyAsJsonAsync(ctx.Request);
            var userId = ReadSnowflake(body, "userId");
            var reason = ReadString(body, "reason")?.Trim();

            // TryReadCount, e nao `(int?)body?[...]`: o cast do Newtonsoft
            // lanca FormatException quando o campo chega como texto nao
            // numerico, e o painel recebia 500 "erro interno" no lugar do 400
            // que descreve o problema. Ausente conta como 0 e cai no mesmo 400.
            if (!userId.HasValue || !TryReadCount(body, "durationMinutes", out var durationMinutes) || durationMinutes <= 0)
            {
                await WriteJsonAsync(ctx.Response, 400, new { error = "userId e durationMinutes válidos são obrigatórios." });
                return;
            }

            var config = await _auth.GetConfigAsync();
            try
            {
                var guild = await _client.GetGuildAsync(config.guildId);
                var member = await guild.GetMemberAsync(userId.Value);

                await member.TimeoutAsync(DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(durationMinutes, 1, 40320)), reason);

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
            var session = await RequirePermissionAsync(ctx.Request, 2);
            if (session is null)
            {
                await WriteAuthFailureAsync(ctx, "Permissão insuficiente para remover do regimento.");
                return;
            }

            var body = await ReadBodyAsJsonAsync(ctx.Request);
            var userId = ReadSnowflake(body, "userId");
            var reason = ReadString(body, "reason")?.Trim();

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

        /// <summary>
        /// Saude do bot. E a UNICA rota mutante-livre sem autenticacao alem do
        /// /api/health, porque alimenta a pagina publica em ccore.daeese.me/status.
        ///
        /// Por isso o corpo padrao so traz o que ja e visivel para qualquer
        /// pessoa no servidor (esta no ar, ha quanto tempo, latencia, tamanho).
        /// Dados da maquina exigem `?detail=host` e nivel 2 - o mesmo criterio
        /// que ja restringe o -osinfo a staff.
        /// </summary>
        private async Task HandleStatusAsync(HttpListenerContext ctx)
        {
            var wantsHost = string.Equals(ctx.Request.QueryString["detail"], "host", StringComparison.OrdinalIgnoreCase);

            if (!wantsHost)
            {
                await WriteJsonAsync(ctx.Response, 200, new { bot = GetCachedPublicStatus() });
                return;
            }

            var session = await // detail=host expoe dados da maquina, nao do bot.
            RequirePermissionAsync(ctx.Request, 2);
            if (session is null)
            {
                await WriteAuthFailureAsync(ctx, "Permissão insuficiente para ver dados da máquina.");
                return;
            }

            await WriteJsonAsync(ctx.Response, 200, new
            {
                bot = BotStatusSnapshot.Public(_client),
                host = BotStatusSnapshot.Host(),
                logs = LogCountsPayload()
            });
        }

        /// <summary>
        /// Retrato publico com cache curto.
        ///
        /// Sem autenticacao na frente, cada visita a pagina de status vira uma
        /// chamada aqui; o cache impede que um refresh agressivo (ou um script)
        /// vire trabalho repetido atravessando o tunnel. Dez segundos sao curtos
        /// o bastante para "o bot caiu" aparecer praticamente na hora.
        /// </summary>
        private PublicStatus GetCachedPublicStatus()
        {
            lock (_statusCacheLock)
            {
                if (_cachedStatus is not null && DateTimeOffset.UtcNow - _cachedStatusAt < StatusCacheTtl)
                    return _cachedStatus;

                _cachedStatus = BotStatusSnapshot.Public(_client);
                _cachedStatusAt = DateTimeOffset.UtcNow;
                return _cachedStatus;
            }
        }

        /// <summary>
        /// Ultimas linhas do console. Nivel 2 porque os logs carregam ids de
        /// usuario, mensagens de excecao e caminhos da maquina.
        /// </summary>
        private async Task HandleLogsAsync(HttpListenerContext ctx)
        {
            var session = await // Logs carregam id de usuario, excecao e caminho da maquina que hospeda o bot.
            RequirePermissionAsync(ctx.Request, 2);
            if (session is null)
            {
                await WriteAuthFailureAsync(ctx, "Permissão insuficiente para ver os logs do bot.");
                return;
            }

            var take = ReadInt(ctx.Request.QueryString["take"], 100, 1, BotLogBuffer.Capacity);
            var level = BotLogBuffer.ParseLevel(ctx.Request.QueryString["level"]);
            var query = ctx.Request.QueryString["q"];

            var lines = BotLogBuffer.Snapshot(take, level, query).Select(l => new
            {
                timestampUtc = l.TimestampUtc,
                level = BotLogBuffer.LevelName(l.Level),
                tag = l.Tag,
                text = l.Text
            });

            await WriteJsonAsync(ctx.Response, 200, new { logs = lines, counts = LogCountsPayload() });
        }

        private static object LogCountsPayload()
        {
            var counts = BotLogBuffer.Counts();
            return new { info = counts.Info, aviso = counts.Aviso, erro = counts.Erro, totalSeen = BotLogBuffer.TotalSeen };
        }

        /// <summary>Efetivo consolidado mais o que ainda esta na fila.</summary>
        private async Task HandleAuditRosterAsync(HttpListenerContext ctx)
        {
            var session = await RequirePermissionAsync(ctx.Request, 1);
            if (session is null)
            {
                await WriteAuthFailureAsync(ctx, "Permissão insuficiente.");
                return;
            }

            var (audit, pending) = await AuditStore.Instance.ReadBothAsync();

            await WriteJsonAsync(ctx.Response, 200, new
            {
                lastUpdatedUtc = audit.lastUpdatedUtc,
                totals = new
                {
                    players = audit.entries.Count,
                    battles = audit.entries.Sum(e => (long)e.battles),
                    kills = audit.entries.Sum(e => (long)e.kills)
                },
                entries = audit.entries
                    .OrderByDescending(e => e.battles)
                    .ThenBy(e => e.username, StringComparer.OrdinalIgnoreCase)
                    .Select(e => new { e.username, e.kills, e.deaths, e.assists, e.battles, e.rank, kd = e.KdRatio }),
                pending = pending.batches.Select(b => new
                {
                    b.batchId,
                    b.createdUtc,
                    b.submittedByUsername,
                    players = b.entries.Count
                })
            });
        }

        /// <summary>Historico do sistema de auditoria - o mesmo que /audit-logs mostra.</summary>
        private async Task HandleAuditHistoryAsync(HttpListenerContext ctx)
        {
            var session = await RequirePermissionAsync(ctx.Request, 1);
            if (session is null)
            {
                await WriteAuthFailureAsync(ctx, "Permissão insuficiente.");
                return;
            }

            var take = ReadInt(ctx.Request.QueryString["take"], 100, 1, 500);
            var entries = await AuditLog.ReadAsync();

            await WriteJsonAsync(ctx.Response, 200, new
            {
                logs = entries
                    .OrderByDescending(e => e.timestampUtc)
                    .Take(take)
                    .Select(e => new { e.timestampUtc, userId = e.userId.ToString(), e.username, e.action, e.details })
            });
        }

        /// <summary>
        /// Edita, renomeia ou remove um jogador. Espelha o /audit-edit,
        /// inclusive a recusa de renomear para um nome que ja existe: sem ela,
        /// dois jogadores seriam fundidos em silencio.
        /// </summary>
        private async Task HandleAuditEntryAsync(HttpListenerContext ctx)
        {
            /*
             * Esta rota faz duas coisas com pesos diferentes: corrigir numeros
             * de um jogador (nivel 1) e APAGAR o registro dele (nivel 2).
             * Corrigir um K/D errado e administracao do dia a dia; apagar o
             * historico de alguem nao e.
             *
             * A ordem aqui importa. O piso da rota e conferido ANTES de tocar no
             * corpo: ReadBodyAsJsonAsync faz JObject.Parse, que LANCA em JSON
             * malformado, e sem esta checagem primeiro um chamador sem sessao
             * conseguiria disparar o parser so mandando lixo. Autenticado o
             * minimo, o corpo e lido e o `remove` decide se ainda falta subir
             * para 2.
             */
            var session = await RequirePermissionAsync(ctx.Request, 1);
            if (session is null)
            {
                await WriteAuthFailureAsync(ctx, "Permissão insuficiente para editar a auditoria.");
                return;
            }

            JObject? body;
            try
            {
                body = await ReadBodyAsJsonAsync(ctx.Request);
            }
            catch (JsonReaderException)
            {
                await WriteJsonAsync(ctx.Response, 400, new { error = "Corpo da requisição não é JSON válido." });
                return;
            }

            var remove = ReadBool(body, "remove");

            if (remove)
            {
                var elevated = await RequirePermissionAsync(ctx.Request, 2);
                if (elevated is null)
                {
                    await WriteAuthFailureAsync(ctx, "Remover registros da auditoria exige permissão 2+.");
                    return;
                }
            }

            var username = ReadString(body, "username")?.Trim();
            if (string.IsNullOrWhiteSpace(username))
            {
                await WriteJsonAsync(ctx.Response, 400, new { error = "username é obrigatório." });
                return;
            }

            var editingPending = string.Equals(ReadString(body, "scope"), "pendente", StringComparison.OrdinalIgnoreCase);
            var scopeLabel = editingPending ? "pendente" : "auditoria";

            if (remove)
            {
                var removed = await AuditStore.Instance.UpdateAsync((storedAudit, storedPending) =>
                {
                    if (!editingPending)
                        return storedAudit.entries.RemoveAll(e => string.Equals(e.username, username, StringComparison.OrdinalIgnoreCase));

                    var count = 0;
                    foreach (var batch in storedPending.batches)
                        count += batch.entries.RemoveAll(e => string.Equals(e.username, username, StringComparison.OrdinalIgnoreCase));
                    return count;
                });

                if (removed == 0)
                {
                    await WriteJsonAsync(ctx.Response, 404, new { error = $"'{username}' não foi encontrado em `{scopeLabel}`." });
                    return;
                }

                var removeDetails = $"Removeu **{username}** de `{scopeLabel}` ({removed} registro(s)).";
                await AuditLog.RecordAsync(session.UserId, session.Username, AuditLog.ActionRemove, removeDetails);
                await AuditAsync(session, "AUDIT_REMOVE", removeDetails);
                await WriteJsonAsync(ctx.Response, 200, new { ok = true, removed });
                return;
            }

            var newName = ReadString(body, "newUsername")?.Trim();
            if (string.IsNullOrWhiteSpace(newName))
                newName = username;

            if (newName.Length > 32 || newName.Contains(','))
            {
                await WriteJsonAsync(ctx.Response, 400, new { error = "O nome precisa ter até 32 caracteres e não pode conter vírgula." });
                return;
            }

            if (!TryReadCount(body, "kills", out var kills) ||
                !TryReadCount(body, "deaths", out var deaths) ||
                !TryReadCount(body, "assists", out var assists) ||
                !TryReadCount(body, "battles", out var battles))
            {
                await WriteJsonAsync(ctx.Response, 400, new { error = "kills, deaths, assists e battles precisam ser inteiros não negativos." });
                return;
            }

            AuditEntry? before = null;

            var failure = await AuditStore.Instance.UpdateAsync<string?>((storedAudit, storedPending) =>
            {
                var renamed = !string.Equals(username, newName, StringComparison.OrdinalIgnoreCase);

                if (editingPending)
                {
                    if (renamed && storedPending.batches.SelectMany(b => b.entries)
                            .Any(e => string.Equals(e.username, newName, StringComparison.OrdinalIgnoreCase)))
                        return $"Já existe '{newName}' nos lotes pendentes.";

                    var found = false;
                    foreach (var entry in storedPending.batches.SelectMany(b => b.entries)
                                 .Where(e => string.Equals(e.username, username, StringComparison.OrdinalIgnoreCase)))
                    {
                        before ??= entry.Clone();
                        entry.username = newName;
                        entry.kills = kills;
                        entry.deaths = deaths;
                        entry.assists = assists;
                        found = true;
                    }

                    // Batalhas nao sao editaveis no escopo pendente: elas sao
                    // contadas por lote na hora do push.
                    return found ? null : "O jogador não está mais nos lotes pendentes.";
                }

                if (renamed && storedAudit.entries.Any(e => string.Equals(e.username, newName, StringComparison.OrdinalIgnoreCase)))
                    return $"Já existe '{newName}' na auditoria. Renomear aqui juntaria dois jogadores sem aviso, então a operação foi cancelada.";

                var target = storedAudit.entries.FirstOrDefault(e => string.Equals(e.username, username, StringComparison.OrdinalIgnoreCase));
                if (target is null)
                    return "O jogador não está mais na auditoria.";

                before = target.Clone();
                target.username = newName;
                target.kills = kills;
                target.deaths = deaths;
                target.assists = assists;
                target.battles = battles;
                return null;
            });

            if (failure is not null)
            {
                await WriteJsonAsync(ctx.Response, 400, new { error = failure });
                return;
            }

            var details =
                $"Editou **{username}** em `{scopeLabel}`: " +
                $"{before?.kills}/{before?.deaths}/{before?.assists} ({before?.battles} bat.) → " +
                $"{newName} {kills}/{deaths}/{assists} ({(editingPending ? before?.battles : battles)} bat.)";

            await AuditLog.RecordAsync(session.UserId, session.Username, AuditLog.ActionEdit, details);
            await AuditAsync(session, "AUDIT_EDIT", details);

            await WriteJsonAsync(ctx.Response, 200, new { ok = true, username = newName });
        }

        /// <summary>Define a patente de um ou mais jogadores. Campo vazio remove.</summary>
        private async Task HandleAuditRanksAsync(HttpListenerContext ctx)
        {
            var session = await // Definir patente e digitacao de dado: nao da cargo no Discord nem remove nada.
            RequirePermissionAsync(ctx.Request, 1);
            if (session is null)
            {
                await WriteAuthFailureAsync(ctx, "Permissão insuficiente para definir patentes.");
                return;
            }

            var body = await ReadBodyAsJsonAsync(ctx.Request);
            if (body?["ranks"] is not JArray rawRanks || rawRanks.Count == 0)
            {
                await WriteJsonAsync(ctx.Response, 400, new { error = "ranks é obrigatório: uma lista de { username, rank }." });
                return;
            }

            var requested = new List<(string Username, string Rank)>();
            foreach (var item in rawRanks)
            {
                var name = ReadString(item as JObject, "username")?.Trim();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                requested.Add((name, ReadString(item as JObject, "rank")?.Trim() ?? string.Empty));
            }

            if (requested.Count == 0)
            {
                await WriteJsonAsync(ctx.Response, 400, new { error = "Nenhum jogador válido na lista." });
                return;
            }

            var changes = new List<string>();
            var missing = new List<string>();

            await AuditStore.Instance.UpdateAsync((storedAudit, _) =>
            {
                foreach (var (name, rank) in requested)
                {
                    var target = storedAudit.entries.FirstOrDefault(e => string.Equals(e.username, name, StringComparison.OrdinalIgnoreCase));
                    if (target is null)
                    {
                        missing.Add(name);
                        continue;
                    }

                    if (string.Equals(target.rank, rank, StringComparison.Ordinal))
                        continue;

                    changes.Add($"{target.username}: '{(string.IsNullOrWhiteSpace(target.rank) ? "-" : target.rank)}' → '{(string.IsNullOrWhiteSpace(rank) ? "-" : rank)}'");
                    target.rank = rank;
                }

                return true;
            });

            if (changes.Count > 0)
            {
                var details = $"Definiu cargo(s) de {changes.Count} jogador(es): {string.Join("; ", changes.Take(10))}{(changes.Count > 10 ? "…" : "")}";
                await AuditLog.RecordAsync(session.UserId, session.Username, AuditLog.ActionSetRanks, details);
                await AuditAsync(session, "AUDIT_SETRANKS", details);
            }

            await WriteJsonAsync(ctx.Response, 200, new { ok = true, changed = changes.Count, changes, missing });
        }

        /// <summary>
        /// Publica a auditoria no GitHub. Reaproveita exatamente o mesmo
        /// AuditPublisher do /audit-push, entao a ordem dos passos e as garantias
        /// contra contagem dupla valem igual aqui.
        /// </summary>
        private async Task HandleAuditPushAsync(HttpListenerContext ctx)
        {
            var session = await // Publica na API do GitHub, em repositorio publico.
            RequirePermissionAsync(ctx.Request, 2);
            if (session is null)
            {
                await WriteAuthFailureAsync(ctx, "Permissão insuficiente para publicar a auditoria.");
                return;
            }

            var body = await ReadBodyAsJsonAsync(ctx.Request);
            var dryRun = ReadBool(body, "dryRun");

            var config = new JSONReader();
            await config.ReadJSON();

            var outcome = await AuditPublisher.PublishAsync(config, dryRun);

            if (!outcome.Ok)
            {
                await WriteJsonAsync(ctx.Response, 502, new
                {
                    error = $"Falha ao publicar durante: {outcome.FailedStep}.",
                    details = outcome.Error,
                    hint = "O arquivo pendente NÃO foi limpo; republicar não conta em dobro."
                });
                return;
            }

            if (!outcome.DryRun && !outcome.NothingToDo)
            {
                await AuditLog.RecordAsync(session.UserId, session.Username, AuditLog.ActionPush, outcome.LogDetails);
                await AuditAsync(session, "AUDIT_PUSH", outcome.LogDetails);
            }

            await WriteJsonAsync(ctx.Response, 200, new
            {
                ok = true,
                dryRun = outcome.DryRun,
                nothingToDo = outcome.NothingToDo,
                hasPending = outcome.HasPending,
                pendingBatches = outcome.PendingBatches,
                totalEntries = outcome.TotalEntries,
                batchesApplied = outcome.Report.BatchesApplied,
                batchesSkipped = outcome.Report.BatchesSkipped,
                playersUpdated = outcome.Report.PlayersUpdated,
                battlesAdded = outcome.Report.BattlesAdded,
                newPlayers = outcome.Report.NewPlayers,
                auditCommitUrl = outcome.AuditCommitUrl,
                archiveCommitUrl = outcome.ArchiveCommitUrl,
                archivePath = outcome.ArchivePath
            });
        }

        /// <summary>Quem esta elegivel a promocao, pela mesma escada do /promocoes.</summary>
        private async Task HandlePromotionsAsync(HttpListenerContext ctx)
        {
            var session = await RequirePermissionAsync(ctx.Request, 1);
            if (session is null)
            {
                await WriteAuthFailureAsync(ctx, "Permissão insuficiente.");
                return;
            }

            var audit = await AuditStore.Instance.ReadAuditAsync();

            var results = audit.entries
                .Select(PromotionLadder.Evaluate)
                .OrderByDescending(r => r.IsPromotable)
                .ThenByDescending(r => r.Battles)
                .ThenBy(r => r.Username, StringComparer.OrdinalIgnoreCase)
                .Select(r => new
                {
                    username = r.Username,
                    promotable = r.IsPromotable,
                    status = r.Status.ToString(),
                    // NeedsApproval separa "e so bater o numero" de "o comando
                    // precisa avaliar": a escada nao decide promocao sozinha.
                    needsApproval = r.Status == PromotionStatus.EligibleNeedsApproval,
                    currentRank = string.IsNullOrWhiteSpace(r.CurrentRankName) ? null : r.CurrentRankName,
                    targetRank = r.TargetRank?.Name,
                    nextRank = r.NextRank?.Name,
                    stepsSkipped = r.StepsSkipped,
                    battles = r.Battles,
                    battlesToNext = r.BattlesToNext
                })
                .ToList();

            await WriteJsonAsync(ctx.Response, 200, new
            {
                promotable = results.Count(r => r.promotable),
                total = results.Count,
                players = results
            });
        }

        /// <summary>Dispara a mensagem de deployment, igual ao /deployment.</summary>
        private async Task HandleDeploymentAsync(HttpListenerContext ctx)
        {
            var session = await // Anuncio de partida: notifica, mas nao remove ninguem nem toca API externa.
            RequirePermissionAsync(ctx.Request, 1);
            if (session is null)
            {
                await WriteAuthFailureAsync(ctx, "Permissão insuficiente para enviar deployment.");
                return;
            }

            var body = await ReadBodyAsJsonAsync(ctx.Request);
            var codigo = ReadString(body, "codigo")?.Trim();
            if (string.IsNullOrWhiteSpace(codigo))
            {
                await WriteJsonAsync(ctx.Response, 400, new { error = "codigo é obrigatório." });
                return;
            }

            var dashboardConfig = await _auth.GetConfigAsync();
            var config = new JSONReader();
            await config.ReadJSON();

            try
            {
                var guild = await _client.GetGuildAsync(dashboardConfig.guildId);
                var (channel, channelError) = await DeploymentBuilder.ResolveChannelAsync(_client, config, guild);
                if (channel is null)
                {
                    await WriteJsonAsync(ctx.Response, 400, new { error = channelError ?? "Canal de deployment indisponível." });
                    return;
                }

                var deployment = DeploymentBuilder.Build(config, guild, codigo);
                await channel.SendMessageAsync(deployment.Message);

                await AuditAsync(session, "DEPLOYMENT", $"Enviou deployment com código '{codigo}' para o canal {channel.Id} ({deployment.RolesPinged} cargo(s) pingado(s)).");

                await WriteJsonAsync(ctx.Response, 200, new
                {
                    ok = true,
                    channelId = channel.Id.ToString(),
                    rolesPinged = deployment.RolesPinged,
                    warning = deployment.ImageWarning
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[dashboard] falha ao enviar deployment: {ex}");
                await WriteJsonAsync(ctx.Response, 400, new { error = "Falha ao enviar o deployment.", details = ex.Message });
            }
        }

        /// <summary>
        /// Inicia um envio de DM em massa.
        ///
        /// Responde com um jobId em vez de esperar o envio terminar: cada
        /// destinatario custa cerca de 4,2s entre o limitador global e o
        /// espacamento, entao os 500 do teto levam ~35 minutos. Manter o request
        /// aberto durante isso so garantiria um timeout.
        /// </summary>
        private async Task HandleDmStartAsync(HttpListenerContext ctx)
        {
            var session = await // DM em massa: ate 500 pessoas de uma vez.
            RequirePermissionAsync(ctx.Request, 2);
            if (session is null)
            {
                await WriteAuthFailureAsync(ctx, "Permissão insuficiente para enviar DM em massa.");
                return;
            }

            var body = await ReadBodyAsJsonAsync(ctx.Request);
            var roleId = ReadSnowflake(body, "roleId");
            var userId = ReadSnowflake(body, "userId");
            var message = ReadString(body, "message")?.Trim() ?? string.Empty;
            var code = ReadString(body, "code")?.Trim() ?? string.Empty;

            if (!roleId.HasValue && !userId.HasValue)
            {
                await WriteJsonAsync(ctx.Response, 400, new { error = "Informe roleId ou userId." });
                return;
            }

            var dashboardConfig = await _auth.GetConfigAsync();
            var config = new JSONReader();
            await config.ReadJSON();

            try
            {
                var guild = await _client.GetGuildAsync(dashboardConfig.guildId);

                DiscordRole? role = roleId.HasValue ? guild.GetRole(roleId.Value) : null;
                if (roleId.HasValue && role is null)
                {
                    await WriteJsonAsync(ctx.Response, 400, new { error = $"Cargo {roleId} não existe neste servidor." });
                    return;
                }

                DiscordUser? user = null;
                if (userId.HasValue)
                    user = await _client.GetUserAsync(userId.Value);

                // Resolver AQUI, e nao dentro do job: um alvo invalido deve
                // falhar no request, e nao virar um job que so quebra depois.
                var (members, targetName, error) = await MassDmService.ResolveRecipientsAsync(guild, role, user);
                if (members is null)
                {
                    await WriteJsonAsync(ctx.Response, 400, new { error });
                    return;
                }

                var embed = commands.DmRolesCertainRoles.BuildDeploymentDm(targetName, code, message);

                // O link vai no content, e nao no embed, para o Discord montar o
                // preview - mesmo criterio do /dmdeployment. Passar null aqui
                // faria a DM do painel sair diferente da DM do comando.
                var content = string.IsNullOrWhiteSpace(config.defaultGameLink) ? null : config.defaultGameLink;
                var job = MassDmService.StartJob(members, targetName, content, embed);

                await AuditAsync(session, "MASS_DM", $"Iniciou envio de DM para {members.Count} destinatário(s) ({targetName}) — job {job.JobId}.");

                await WriteJsonAsync(ctx.Response, 202, new { ok = true, job = job.Snapshot() });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[dashboard] falha ao iniciar DM em massa: {ex}");
                await WriteJsonAsync(ctx.Response, 400, new { error = "Falha ao iniciar o envio.", details = ex.Message });
            }
        }

        private async Task HandleDmStatusAsync(HttpListenerContext ctx)
        {
            var session = await // Acompanha o envio em massa acima.
            RequirePermissionAsync(ctx.Request, 2);
            if (session is null)
            {
                await WriteAuthFailureAsync(ctx, "Permissão insuficiente.");
                return;
            }

            var jobId = ctx.Request.QueryString["jobId"];
            if (string.IsNullOrWhiteSpace(jobId))
            {
                await WriteJsonAsync(ctx.Response, 200, new { jobs = MassDmService.RecentJobs() });
                return;
            }

            var job = MassDmService.GetJob(jobId);
            if (job is null)
            {
                await WriteJsonAsync(ctx.Response, 404, new { error = "Job não encontrado ou já expirado." });
                return;
            }

            await WriteJsonAsync(ctx.Response, 200, new { job });
        }

        /// <summary>Alista um usuario, com a mesma verificacao ROBLOX do /enlistuser.</summary>
        private async Task HandleEnlistAsync(HttpListenerContext ctx)
        {
            var session = await // Consulta a API do ROBLOX, concede cargos e muda apelido.
            RequirePermissionAsync(ctx.Request, 2);
            if (session is null)
            {
                await WriteAuthFailureAsync(ctx, "Permissão insuficiente para alistar.");
                return;
            }

            var body = await ReadBodyAsJsonAsync(ctx.Request);
            var userId = ReadSnowflake(body, "userId");
            var robloxUsername = ReadString(body, "robloxUsername")?.Trim();
            var socialRole = ReadBool(body, "socialRole");

            if (!userId.HasValue || string.IsNullOrWhiteSpace(robloxUsername))
            {
                await WriteJsonAsync(ctx.Response, 400, new { error = "userId e robloxUsername são obrigatórios." });
                return;
            }

            var dashboardConfig = await _auth.GetConfigAsync();
            var config = new JSONReader();
            await config.ReadJSON();

            DiscordGuild guild;
            DiscordMember member;
            try
            {
                guild = await _client.GetGuildAsync(dashboardConfig.guildId);
                member = await guild.GetMemberAsync(userId.Value);
            }
            catch (Exception)
            {
                await WriteJsonAsync(ctx.Response, 400, new { error = "O usuário informado não é membro do servidor." });
                return;
            }

            var check = await EnlistmentService.VerifyRobloxAsync(robloxUsername);
            if (!check.Ok)
            {
                await WriteJsonAsync(ctx.Response, 400, new { error = check.Error });
                return;
            }

            // Reprovado na verificacao nao e erro do chamador: a consulta
            // funcionou e a resposta e "negado". Por isso 200 com approved=false
            // em vez de 4xx - o painel precisa mostrar os numeros que motivaram.
            if (!check.IsLikelyMain)
            {
                await WriteJsonAsync(ctx.Response, 200, new
                {
                    ok = false,
                    approved = false,
                    reason = "Conta ROBLOX não atende aos critérios mínimos de confiabilidade.",
                    accountAgeDays = check.AccountAge.Days,
                    friends = check.FriendsCount,
                    badges = check.BadgesDisplay
                });
                return;
            }

            var applied = await EnlistmentService.ApplyAsync(_client, guild, member, config, socialRole);

            var logEmbed = EnlistmentService.BuildLogEmbed(
                _client, member, $"<@{session.UserId}> (dashboard)", robloxUsername, check, applied, socialRole);

            var announceWarnings = await EnlistmentService.AnnounceAsync(_client, guild, config, logEmbed, member);

            await AuditAsync(session, "ENLIST", $"Alistou {userId} como '{robloxUsername}' ({applied.AddedRoles.Count} cargo(s)).");

            await WriteJsonAsync(ctx.Response, 200, new
            {
                ok = true,
                approved = true,
                accountAgeDays = check.AccountAge.Days,
                friends = check.FriendsCount,
                badges = check.BadgesDisplay,
                rolesAdded = applied.AddedRoles.Select(r => r.Name),
                nicknameChanged = applied.NicknameChanged,
                warnings = applied.Warnings.Concat(announceWarnings)
            });
        }

        /// <summary>Inteiro vindo da query string, com padrao e limites.</summary>
        private static int ReadInt(string? raw, int fallback, int min, int max)
        {
            if (!int.TryParse(raw, out var value))
                return fallback;

            return Math.Clamp(value, min, max);
        }

        /// <summary>Contador nao negativo vindo do corpo JSON. Ausente = 0.</summary>
        private static bool TryReadCount(JObject? body, string field, out int value)
        {
            value = 0;
            var token = body?[field];
            if (token is null || token.Type == JTokenType.Null)
                return true;

            if (token.Type == JTokenType.Integer)
            {
                value = token.Value<int>();
                return value >= 0;
            }

            if (token.Type == JTokenType.String && int.TryParse(token.Value<string>()?.Trim(), out var parsed))
            {
                value = parsed;
                return value >= 0;
            }

            return false;
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

        /// <summary>
        /// Sessao valida E com nivel suficiente para a acao.
        ///
        /// Reconfere o nivel no Discord quando a ultima checagem esta velha: sem
        /// isso, quem perdesse o cargo continuaria mandando no dashboard ate a
        /// sessao expirar (podia dar horas). Se o Discord nao responder, o nivel
        /// conhecido e mantido - derrubar todo mundo numa instabilidade do
        /// Discord seria pior que o risco que isso cobre.
        /// </summary>
        /// <summary>
        /// Responde a uma falha de autorizacao dizendo QUAL das duas aconteceu.
        ///
        /// Antes todas as rotas devolviam 403 "Permissao insuficiente", inclusive
        /// quando o problema era simplesmente nao haver sessao. O efeito pratico
        /// era ruim: quem estava com a sessao expirada via "permissao
        /// insuficiente" em todo painel e concluia que o cargo dele e que era
        /// baixo demais - foi exatamente essa a leitura que motivou esta
        /// mudanca. Pior, o painel ja sabe tratar 401 (limpa o token e volta
        /// para a tela de login) e nunca recebia um.
        ///
        /// Agora: sem sessao -> 401, e o painel manda logar de novo; com sessao
        /// e nivel baixo -> 403 com o motivo especifico da rota.
        /// </summary>
        private async Task WriteAuthFailureAsync(HttpListenerContext ctx, string forbiddenMessage)
        {
            if (TryGetSession(ctx.Request) is null)
            {
                await WriteJsonAsync(ctx.Response, 401, new
                {
                    error = "Sessão inválida ou expirada. Rode /dashboardlink no Discord e entre de novo."
                });
                return;
            }

            await WriteJsonAsync(ctx.Response, 403, new { error = forbiddenMessage });
        }

        private async Task<DashboardSession?> RequirePermissionAsync(HttpListenerRequest request, int minLevel)
        {
            var session = TryGetSession(request);
            if (session is null)
                return null;

            if (DateTimeOffset.UtcNow >= session.NextPermissionCheckAt)
            {
                var current = await _auth.ResolvePermissionLevelAsync(_client, session.UserId);

                if (current.HadLookupFailure)
                {
                    /*
                     * Discord fora do ar nao derruba ninguem: o nivel conhecido
                     * continua valendo, porque expulsar todo mundo numa
                     * instabilidade seria pior que o risco coberto aqui.
                     *
                     * O que MUDA e a hora da proxima tentativa. Antes a falha nao
                     * mexia no carimbo, entao a sessao seguia vencida e cada
                     * request refazia as tres tentativas do
                     * ResolvePermissionLevelAsync, com os atrasos entre elas,
                     * antes de responder qualquer coisa: uma lentidao do Discord
                     * virava lentidao de todo o painel, multiplicada pelo numero
                     * de requests. Com o adiamento curto, o custo da
                     * indisponibilidade e pago uma vez a cada 30 segundos.
                     */
                    session.NextPermissionCheckAt = DateTimeOffset.UtcNow.Add(PermissionRetryBackoff);
                }
                else
                {
                    session.PermissionLevel = current.PermissionLevel;
                    session.NextPermissionCheckAt = DateTimeOffset.UtcNow.Add(PermissionRefreshInterval);

                    if (current.PermissionLevel <= 0)
                    {
                        _sessions.TryRemove(session.Token, out _);
                        return null;
                    }
                }
            }

            return session.PermissionLevel >= minLevel ? session : null;
        }

        /// <summary>
        /// Conta a tentativa de login da origem. Devolve false quando a janela
        /// ja estourou o limite.
        /// </summary>
        private bool RegisterLoginAttempt(HttpListenerRequest request)
        {
            var key = ClientKey(request);
            var now = DateTimeOffset.UtcNow;

            var attempts = _loginAttempts.GetOrAdd(key, _ => new LoginAttempts { WindowStart = now });

            lock (attempts)
            {
                if (now - attempts.WindowStart >= LoginAttemptWindow)
                {
                    attempts.WindowStart = now;
                    attempts.Count = 0;
                }

                attempts.Count++;
                return attempts.Count <= MaxLoginAttemptsPerWindow;
            }
        }

        private void ClearLoginAttempts(HttpListenerRequest request) =>
            _loginAttempts.TryRemove(ClientKey(request), out _);

        /// <summary>
        /// Identifica o cliente para o rate limit de login.
        ///
        /// Nao pode usar RemoteEndPoint: a API so e alcancavel pelo tunnel do
        /// cloudflared rodando na mesma maquina, entao o peer TCP e sempre
        /// 127.0.0.1 e TODOS os clientes cairiam no mesmo balde - dez codigos
        /// errados de qualquer pessoa trancariam o dashboard para todo mundo
        /// por cinco minutos, e um login bem-sucedido de qualquer um limparia a
        /// contagem de todos.
        ///
        /// CF-Connecting-IP e definido pela Cloudflare e nao e forjavel: uma
        /// requisicao que traga esse header do cliente e recusada na borda com
        /// 403 "error code: 1000".
        ///
        /// X-Forwarded-For NAO serve. O valor enviado pelo cliente e mantido na
        /// frente da cadeia: quem manda "X-Forwarded-For: 8.8.8.8" faz o header
        /// chegar aqui como "8.8.8.8,&lt;ip real&gt;". Ler o primeiro elemento,
        /// que e a leitura convencional, entregaria justamente o valor escolhido
        /// pelo atacante, permitindo trocar de identidade a cada tentativa.
        /// </summary>
        private static string ClientKey(HttpListenerRequest request)
        {
            var cloudflareIp = request.Headers["CF-Connecting-IP"];
            if (!string.IsNullOrWhiteSpace(cloudflareIp))
            {
                return cloudflareIp.Trim();
            }

            return request.RemoteEndPoint?.Address?.ToString() ?? "desconhecido";
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

        /// <summary>
        /// Escreve sem estourar de novo. Usado no tratador de erro: se a falha
        /// aconteceu DEPOIS de a resposta ter sido fechada, tentar escrever ali
        /// lancaria uma segunda excecao, essa sem ninguem para pegar.
        /// </summary>
        private static async Task TryWriteJsonAsync(HttpListenerResponse response, int statusCode, object payload)
        {
            try
            {
                await WriteJsonAsync(response, statusCode, payload);
            }
            catch
            {
                // resposta ja enviada ou conexao caiu
            }
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

        /// <summary>
        /// Texto de um campo do corpo JSON.
        ///
        /// O cast direto `(string?)body?["x"]` do Newtonsoft LANCA quando o
        /// campo vem como objeto ou lista - e a excecao subia ate o tratador
        /// geral, que devolvia 500 "erro interno" para o que na verdade e um
        /// corpo malformado do cliente. Aqui um tipo inesperado vira
        /// simplesmente null, e a validacao de campo obrigatorio da rota
        /// responde o 400 que corresponde ao caso.
        /// </summary>
        private static string? ReadString(JObject? body, string field)
        {
            var token = body?[field];
            if (token is null || token.Type is JTokenType.Null or JTokenType.Undefined)
                return null;

            return token.Type switch
            {
                JTokenType.Object or JTokenType.Array => null,
                _ => token.ToString()
            };
        }

        /// <summary>Booleano do corpo JSON. Ausente ou ilegivel = <paramref name="fallback"/>.</summary>
        private static bool ReadBool(JObject? body, string field, bool fallback = false)
        {
            var token = body?[field];
            if (token is null)
                return fallback;

            return token.Type switch
            {
                JTokenType.Boolean => token.Value<bool>(),
                JTokenType.Integer => token.Value<long>() != 0,
                JTokenType.String => bool.TryParse(token.Value<string>()?.Trim(), out var parsed) ? parsed : fallback,
                _ => fallback
            };
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

            // A janela de tentativas tambem precisa de poda: sem isso o
            // dicionario cresceria um item por endereco que ja tentou logar.
            foreach (var kv in _loginAttempts)
            {
                if (now - kv.Value.WindowStart >= LoginAttemptWindow)
                    _loginAttempts.TryRemove(kv.Key, out _);
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

            /// <summary>
            /// Quando o nivel desta sessao deve ser reconferido no Discord.
            ///
            /// Guarda o PROXIMO horario, e nao o ultimo: e o que permite adiar a
            /// tentativa por um tempo diferente conforme a consulta anterior
            /// tenha dado certo (PermissionRefreshInterval) ou falhado
            /// (PermissionRetryBackoff).
            /// </summary>
            public DateTimeOffset NextPermissionCheckAt { get; set; }
        }

        private sealed class LoginAttempts
        {
            public int Count;
            public DateTimeOffset WindowStart;
        }
    }
}
