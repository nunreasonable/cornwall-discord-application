using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace CornwallUtilities.config
{
    internal class JSONReader
    {
        public string? token { get; private set; }
        public string? prefix { get; private set; }

        /// <summary>
        /// Servidores onde os comandos de guild sao registrados. Antes eram dois
        /// literais dentro do Program.Main, com um comentario dizendo que vinham
        /// do config - o que nunca foi verdade.
        /// </summary>
        public ulong[]? guildIds { get; private set; }
        public SpreadsheetInfoTab[]? spreadsheetInfoTabs { get; private set; }
        public string? defaultGameLink { get; private set; }

        // Config used by the /enlistuser slash command
        public ulong? enlistPermissionRoleId { get; private set; }
        public ulong? enlistLogChannelId { get; private set; }
        public ulong? enlistWelcomeChannelId { get; private set; }
        public ulong[]? enlistTargetRoleIds { get; private set; }
        public ulong? enlistSocialRoleId { get; private set; }

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

        // Config used by message blacklist detection
        public MessageBlacklistConfig? messageBlacklist { get; private set; }

        // Config used by the audit commands (/audit-*)
        public AuditConfig? audit { get; private set; }

        private const string ConfigPath = "config/config.jsonc";

        // Cache do arquivo lido, invalidado pela data de modificacao.
        //
        // Cada comando cria um leitor novo e chamava ReadJSON, o que significava
        // ler e desserializar o config inteiro do disco a cada interacao - dentro
        // do caminho que precisa responder ao Discord em 3 segundos. Editar o
        // config continua valendo na hora: o carimbo de modificacao muda e a
        // proxima leitura recarrega, sem precisar reiniciar o bot.
        private static readonly SemaphoreSlim s_cacheLock = new(1, 1);
        private static JSONStructure? s_cache;
        private static DateTime s_cacheStamp;

        public async Task ReadJSON()
        {
            var data = await LoadAsync();

            token = data?.token;
            prefix = data?.prefix;
            spreadsheetInfoTabs = data?.spreadsheetInfoTabs;
            defaultGameLink = data?.defaultGameLink;

            guildIds = data?.guildIds;

            enlistPermissionRoleId = data?.enlistPermissionRoleId;
            enlistLogChannelId = data?.enlistLogChannelId;
            enlistWelcomeChannelId = data?.enlistWelcomeChannelId;
            enlistTargetRoleIds = data?.enlistTargetRoleIds;
            enlistSocialRoleId = data?.enlistSocialRoleId;

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

            messageBlacklist = data?.messageBlacklist;
            audit = data?.audit;
        }

        /// <summary>
        /// Devolve o config desserializado, relendo do disco apenas quando o
        /// arquivo mudou. O objeto e compartilhado entre os leitores - ninguem
        /// escreve nele, e por isso os campos aqui sao todos `private set`.
        /// </summary>
        private static async Task<JSONStructure?> LoadAsync()
        {
            var stamp = File.GetLastWriteTimeUtc(ConfigPath);
            if (s_cache is not null && stamp == s_cacheStamp)
                return s_cache;

            await s_cacheLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // Confere de novo: outra chamada pode ter recarregado enquanto
                // esta esperava o lock.
                stamp = File.GetLastWriteTimeUtc(ConfigPath);
                if (s_cache is not null && stamp == s_cacheStamp)
                    return s_cache;

                var json = await File.ReadAllTextAsync(ConfigPath).ConfigureAwait(false);
                s_cache = JsonConvert.DeserializeObject<JSONStructure>(json);
                s_cacheStamp = stamp;
                return s_cache;
            }
            finally
            {
                s_cacheLock.Release();
            }
        }
    }

    internal sealed class JSONStructure
    {
        public string? token { get; set; }
        public string? prefix { get; set; }
        public ulong[]? guildIds { get; set; }
        public SpreadsheetInfoTab[]? spreadsheetInfoTabs { get; set; }
        public string? defaultGameLink { get; set; }

        // Config for /enlistuser
        public ulong? enlistPermissionRoleId { get; set; }
        public ulong? enlistLogChannelId { get; set; }
        public ulong? enlistWelcomeChannelId { get; set; }
        public ulong[]? enlistTargetRoleIds { get; set; }
        public ulong? enlistSocialRoleId { get; set; }

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

        // Config for the audit commands
        public AuditConfig? audit { get; set; }
    }

    internal sealed class MessageRepostingConfig
    {
        public bool enabled { get; set; }
        public ulong targetChannelId { get; set; }
        public int repostIntervalMinutes { get; set; }
        public int messageRetentionHours { get; set; }
        public int minimumMessagesForRepost { get; set; }
    }

    internal sealed class MessageBlacklistConfig
    {
        public bool enabled { get; set; }
        public string[]? blacklistedTerms { get; set; }
        public string? responseMessage { get; set; }
        public string[]? responseMessage2Terms { get; set; }
        public string? responseMessage2 { get; set; }
        public ulong[]? notifyUserIds { get; set; }
        public int? dmAlertCooldownMinutes { get; set; }
    }

    /// <summary>
    /// Uma aba da planilha regimental que o /checkspreadsheetinfo varre.
    ///
    /// So o nome e a URL sao necessarios: a coluna do nome de usuario e achada
    /// pelo cabecalho, nao por indice fixo, porque as abas nao a tem na mesma
    /// posicao - PROMOCOES comeca na coluna K.
    /// </summary>
    internal sealed class SpreadsheetInfoTab
    {
        /// <summary>Nome mostrado no embed, ex.: "CENTRO".</summary>
        public string? name { get; set; }
        /// <summary>URL de export CSV da aba.</summary>
        public string? csvUrl { get; set; }
        /// <summary>
        /// Aba de historico de promocoes, que tem colunas proprias (patente
        /// antiga e nova) e por isso vira uma secao separada do embed em vez de
        /// mais um bloco de dados atuais.
        /// </summary>
        public bool promotions { get; set; }
    }

    internal sealed class AuditConfig
    {
        /// <summary>Personal Access Token do GitHub com permissao de escrita no repositorio.</summary>
        public string? githubToken { get; set; }
        public string? githubOwner { get; set; }
        public string? githubRepo { get; set; }
        /// <summary>Branch dedicada aos dados da auditoria.</summary>
        public string? githubBranch { get; set; }
        /// <summary>Caminho do arquivo consolidado dentro da branch.</summary>
        public string? auditFilePath { get; set; }
        /// <summary>Pasta onde cada lote bruto e arquivado.</summary>
        public string? archiveDirectory { get; set; }
        /// <summary>URL de export CSV da aba da planilha com a auditoria atual.</summary>
        public string? auditCsvUrl { get; set; }
        public AuditCsvColumns? csvColumns { get; set; }
        public int csvHeaderRows { get; set; } = 1;
    }

    /// <summary>
    /// Indices (base 0) das colunas da planilha usada pelo /audit-import.
    ///
    /// Use -1 para uma coluna que a planilha NAO tem. Os padroes abaixo seguem a
    /// planilha atual do regimento - "NOME DE USUARIO, PATENTE, BATALHAS, ULTIMO
    /// DIA ATUALIZADO" - que nao traz kills/deaths/assists: esses numeros passam a
    /// vir apenas do /audit-add.
    /// </summary>
    internal sealed class AuditCsvColumns
    {
        public int username { get; set; }
        public int rank { get; set; } = 1;
        public int battles { get; set; } = 2;
        public int kills { get; set; } = -1;
        public int deaths { get; set; } = -1;
        public int assists { get; set; } = -1;
    }
}
