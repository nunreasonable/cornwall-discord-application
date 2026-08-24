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

        /*
         * Cache por carimbo de modificacao, no mesmo espirito do
         * JSONReader.LoadAsync.
         *
         * Cada ReadAsync lia TRES arquivos do disco e desserializava o principal
         * duas vezes - e o GetConfigAsync e chamado por praticamente toda rota
         * que muda alguma coisa (mensagem, cargo, punicao, deployment, DM,
         * alistamento) e tambem pelo login. Como tudo isso e serializado atras de
         * um SemaphoreSlim unico, era gargalo de vazao alem de I/O desperdicado.
         *
         * A chave e o trio de LastWriteTimeUtc: qualquer escrita - inclusive a
         * do proprio AppendAuditAsync - invalida sozinha.
         */
        private DashboardConfigStructure? _cache;
        private (DateTime Core, DateTime Whitelist, DateTime Audit) _cacheStamp;

        // Verdadeiro quando a ULTIMA leitura do arquivo principal caiu no default
        // por parse falho (nao quando o arquivo simplesmente nao existia). Nesse
        // estado o nucleo em memoria esta VAZIO mas o arquivo em disco ainda tem
        // a config real; deixar WriteAsync gravar o nucleo por cima apagaria
        // guildId, os niveis e o tunnelSecret de forma irreversivel - e como toda
        // acao do painel (login, auditoria) muta a whitelist e chama WriteAsync,
        // uma unica virgula sobrando no JSON derrubava o painel para sempre.
        // Enquanto degradado, WriteAsync preserva o arquivo principal e grava so
        // os auxiliares. Uma leitura bem-sucedida (arquivo corrigido a mao) zera a
        // flag e a gravacao do nucleo volta ao normal.
        private bool _coreDegraded;

        private (DateTime, DateTime, DateTime) CurrentStamps() => (
            File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue,
            File.Exists(_whitelistPath) ? File.GetLastWriteTimeUtc(_whitelistPath) : DateTime.MinValue,
            File.Exists(_auditLogPath) ? File.GetLastWriteTimeUtc(_auditLogPath) : DateTime.MinValue);

        public async Task<DashboardConfigStructure> ReadAsync()
        {
            await _fileLock.WaitAsync();
            try
            {
                var stamps = CurrentStamps();
                if (_cache is not null && stamps == _cacheStamp)
                    return Detach(_cache);

                var legacyConfig = new DashboardConfigStructure();
                DashboardCoreConfigStructure coreConfig;
                var rewriteCore = false;

                if (!File.Exists(_path))
                {
                    // Arquivo ausente e primeira execucao legitima, nao corrupcao:
                    // nao ha nucleo real para preservar, entao gravar o default
                    // vazio e correto e a flag de degradado deve ficar limpa.
                    coreConfig = new DashboardCoreConfigStructure();
                    _coreDegraded = false;
                    await WriteJsonAsync(_path, coreConfig);
                }
                else
                {
                    // Config principal corrompido nao pode derrubar o dashboard
                    // inteiro: sem este catch, um JSON truncado subia daqui ate
                    // GetConfigAsync e toda rota - login inclusive - passava a
                    // responder 500.
                    //
                    // Cair no default aqui e conservador de proposito: o default
                    // tem allowedOrigins e grantableRoleIds VAZIOS, entao o
                    // servico recusa subir (ver DashboardHttpService.StartAsync)
                    // em vez de subir com permissao ampla.
                    try
                    {
                        var json = await File.ReadAllTextAsync(_path);
                        coreConfig = JsonConvert.DeserializeObject<DashboardCoreConfigStructure>(json) ?? new DashboardCoreConfigStructure();
                        legacyConfig = JsonConvert.DeserializeObject<DashboardConfigStructure>(json) ?? new DashboardConfigStructure();
                        rewriteCore = json.Contains("\"whitelistedUsers\"", StringComparison.Ordinal)
                            || json.Contains("\"auditLog\"", StringComparison.Ordinal);
                        _coreDegraded = false;
                    }
                    catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                    {
                        Console.WriteLine($"[dashboard] {_path} ilegivel ({ex.Message}); seguindo com o padrao. O arquivo NAO foi sobrescrito e as gravacoes do nucleo ficam suspensas ate ele ser corrigido.");
                        coreConfig = new DashboardCoreConfigStructure();
                        legacyConfig = new DashboardConfigStructure();
                        rewriteCore = false;
                        _coreDegraded = true;
                    }
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

                var merged = MergeConfig(coreConfig, whitelistedUsers, auditLog);

                // Relê os carimbos DEPOIS de tudo: o rewriteCore acima pode ter
                // reescrito o arquivo principal, e guardar o carimbo de antes
                // deixaria o cache preso a uma versao que ja nao existe.
                _cacheStamp = CurrentStamps();
                _cache = merged;
                return Detach(merged);
            }
            finally
            {
                _fileLock.Release();
            }
        }

        /// <summary>Descarta o cache. Usado depois de gravar por este mesmo processo.</summary>
        private void InvalidateCache() => _cache = null;

        public async Task WriteAsync(DashboardConfigStructure data)
        {
            await _fileLock.WaitAsync();
            try
            {
                // Nao reescreve o nucleo enquanto a ultima leitura estiver
                // degradada: os campos em memoria estao vazios porque o parse
                // falhou, e grava-los apagaria a config real que ainda esta em
                // disco. Whitelist e log de auditoria continuam sendo gravados -
                // eles vem das listas mutadas pelo chamador, nao do nucleo.
                if (_coreDegraded)
                {
                    Console.WriteLine($"[dashboard] nucleo em estado degradado; {_path} preservado e apenas whitelist/auditoria gravados. Corrija o arquivo a mao para reativar a gravacao do nucleo.");
                }
                else
                {
                    var coreConfig = ToCoreConfig(data);
                    await WriteJsonAsync(_path, coreConfig);
                }

                await WriteJsonAsync(_whitelistPath, data.whitelistedUsers ?? new List<DashboardWhitelistedUser>());
                await WriteJsonAsync(_auditLogPath, data.auditLog ?? new List<DashboardAuditEntry>());
            }
            finally
            {
                // Nao basta confiar no carimbo: dois writes dentro do mesmo tick
                // do relogio de arquivo dariam o mesmo LastWriteTimeUtc, e a
                // segunda leitura devolveria a versao velha. Invalidar aqui
                // fecha essa janela.
                //
                // No finally, e nao no fim do try: os chamadores mutam o objeto
                // cacheado ANTES de chamar WriteAsync, entao um write que falha
                // (disco cheio, permissao) deixava o cache com uma entrada que
                // nunca chegou ao disco - e a API seguia servindo esse fantasma
                // pelo resto da vida do processo.
                InvalidateCache();
                _fileLock.Release();
            }
        }

        /// <summary>
        /// Devolve um retrato que nao compartilha as duas listas mutaveis com o
        /// cache.
        ///
        /// Sem isto, ReadAsync entregava SEMPRE a mesma instancia: um
        /// GetAuditAsync enumerando config.auditLog enquanto um AppendAuditAsync
        /// fazia Add na mesma lista dava "Collection was modified" e virava 500
        /// no meio de um GET /api/audit. Copiar aqui protege todo leitor, e nao
        /// so o que a gente lembrou de trancar.
        ///
        /// Os elementos nao sao clonados - ninguem muta DashboardAuditEntry nem
        /// DashboardWhitelistedUser depois de criado; quem escreve troca a lista.
        /// Os arrays de config (level*RoleIds etc.) tambem sao compartilhados de
        /// proposito: sao substituidos, nunca alterados no lugar.
        /// </summary>
        private static DashboardConfigStructure Detach(DashboardConfigStructure source)
        {
            return MergeConfig(
                ToCoreConfig(source),
                new List<DashboardWhitelistedUser>(source.whitelistedUsers ?? new List<DashboardWhitelistedUser>()),
                new List<DashboardAuditEntry>(source.auditLog ?? new List<DashboardAuditEntry>()));
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
                grantableRoleIds = coreConfig.grantableRoleIds,
                allowedSendChannelIds = coreConfig.allowedSendChannelIds,
                linkCodeLifetimeMinutes = coreConfig.linkCodeLifetimeMinutes,
                sessionLifetimeMinutes = coreConfig.sessionLifetimeMinutes,
                tunnelSecret = coreConfig.tunnelSecret,
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
                grantableRoleIds = data.grantableRoleIds,
                allowedSendChannelIds = data.allowedSendChannelIds,
                linkCodeLifetimeMinutes = data.linkCodeLifetimeMinutes,
                sessionLifetimeMinutes = data.sessionLifetimeMinutes,
                tunnelSecret = data.tunnelSecret
            };
        }

        /// <summary>
        /// Le um dos arquivos auxiliares, caindo no default quando ele esta
        /// corrompido.
        ///
        /// Sem o catch, um JSON truncado aqui subia por ReadAsync ate
        /// GetConfigAsync - que TODA rota chama - e o dashboard inteiro passava a
        /// responder 500, inclusive o login. Mesma escolha do AuditStore.
        ///
        /// O arquivo ruim NAO e reescrito: sobrescrever apagaria a unica copia do
        /// que estava la, e o que sobrou dele ainda pode ser recuperado a mao.
        /// </summary>
        private static async Task<T> ReadJsonAsync<T>(string path, T defaultValue)
        {
            if (!File.Exists(path))
            {
                await WriteJsonAsync(path, defaultValue);
                return defaultValue;
            }

            try
            {
                var json = await File.ReadAllTextAsync(path);
                if (string.IsNullOrWhiteSpace(json))
                    return defaultValue;

                var data = JsonConvert.DeserializeObject<T>(json);
                return data ?? defaultValue;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"[dashboard] {path} ilegivel ({ex.Message}); seguindo com o padrao. O arquivo NAO foi sobrescrito.");
                return defaultValue;
            }
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

            // FlushToDisk garante durabilidade: sem o fsync do conteudo, uma queda
            // de energia pode persistir o rename antes dos dados e deixar um
            // arquivo do tamanho certo com conteudo nulo. Aqui estao a whitelist,
            // o log de auditoria e - em dashboard_auth.json - o tunnelSecret.
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            await using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }

            // 0600 ANTES do move: o dashboard_auth.json guarda o tunnelSecret, e o
            // .tmp nascia com o umask do processo (tipicamente 0644). Como o
            // arquivo e reescrito a cada login/auditoria, um chmod manual seria
            // desfeito na proxima acao - o modo precisa ser imposto aqui.
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(tmp, path, overwrite: true);
        }
    }

    internal sealed class DashboardCoreConfigStructure
    {
        public string listenUrl { get; set; } = "http://127.0.0.1:5056/";
        /// <summary>
        /// Vazio de proposito. O default era new[] { "*" }, e a rede de seguranca
        /// do DashboardHttpService so olhava para lista VAZIA - entao um deploy
        /// novo, que faz o reader gravar este default em disco, nascia com
        /// Access-Control-Allow-Origin: * e so era percebido depois.
        /// </summary>
        public string[] allowedOrigins { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Segredo que o Worker da Cloudflare manda no header X-Ccore-Tunnel.
        /// So com ele batendo o bot confia no CF-Connecting-IP para limitar
        /// login por IP. Vazio desliga a checagem e o limite volta a ser global.
        /// </summary>
        public string tunnelSecret { get; set; } = string.Empty;
        public ulong guildId { get; set; }
        public ulong[] level4UserIds { get; set; } = Array.Empty<ulong>();
        public ulong[] level3RoleIds { get; set; } = Array.Empty<ulong>();
        public ulong[] level2RoleIds { get; set; } = Array.Empty<ulong>();
        public ulong[] level1RoleIds { get; set; } = Array.Empty<ulong>();
        public ulong[] regimentRoleIds { get; set; } = Array.Empty<ulong>();

        /// <summary>
        /// Unicos cargos que POST /api/roles/add e /remove podem tocar.
        /// Vazio recusa tudo: conceder cargo e a operacao mais perigosa do
        /// dashboard, porque nada impedia um nivel 2 de conceder a si mesmo um
        /// cargo de level3RoleIds e virar nivel 3.
        /// </summary>
        public ulong[] grantableRoleIds { get; set; } = Array.Empty<ulong>();

        /// <summary>
        /// Canais onde o nivel 1 pode fazer o bot escrever por
        /// POST /api/messages/send. Vazio nao restringe nada (comportamento
        /// antigo); preenchido, canal de fora passa a exigir nivel 2.
        /// </summary>
        public ulong[] allowedSendChannelIds { get; set; } = Array.Empty<ulong>();
        public int linkCodeLifetimeMinutes { get; set; } = 10;
        public int sessionLifetimeMinutes { get; set; } = 60;
    }

    internal sealed class DashboardConfigStructure
    {
        public string listenUrl { get; set; } = "http://127.0.0.1:5056/";
        /// <summary>
        /// Vazio de proposito. O default era new[] { "*" }, e a rede de seguranca
        /// do DashboardHttpService so olhava para lista VAZIA - entao um deploy
        /// novo, que faz o reader gravar este default em disco, nascia com
        /// Access-Control-Allow-Origin: * e so era percebido depois.
        /// </summary>
        public string[] allowedOrigins { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Segredo que o Worker da Cloudflare manda no header X-Ccore-Tunnel.
        /// So com ele batendo o bot confia no CF-Connecting-IP para limitar
        /// login por IP. Vazio desliga a checagem e o limite volta a ser global.
        /// </summary>
        public string tunnelSecret { get; set; } = string.Empty;
        public ulong guildId { get; set; }
        public ulong[] level4UserIds { get; set; } = Array.Empty<ulong>();
        public ulong[] level3RoleIds { get; set; } = Array.Empty<ulong>();
        public ulong[] level2RoleIds { get; set; } = Array.Empty<ulong>();
        public ulong[] level1RoleIds { get; set; } = Array.Empty<ulong>();
        public ulong[] regimentRoleIds { get; set; } = Array.Empty<ulong>();

        /// <summary>
        /// Unicos cargos que POST /api/roles/add e /remove podem tocar.
        /// Vazio recusa tudo: conceder cargo e a operacao mais perigosa do
        /// dashboard, porque nada impedia um nivel 2 de conceder a si mesmo um
        /// cargo de level3RoleIds e virar nivel 3.
        /// </summary>
        public ulong[] grantableRoleIds { get; set; } = Array.Empty<ulong>();

        /// <summary>
        /// Canais onde o nivel 1 pode fazer o bot escrever por
        /// POST /api/messages/send. Vazio nao restringe nada (comportamento
        /// antigo); preenchido, canal de fora passa a exigir nivel 2.
        /// </summary>
        public ulong[] allowedSendChannelIds { get; set; } = Array.Empty<ulong>();
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
