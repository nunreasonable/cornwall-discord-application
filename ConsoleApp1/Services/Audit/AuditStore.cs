using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace CornwallUtilities.Services.Audit
{
    /// <summary>
    /// Persistencia local da auditoria (data/audit.json e data/audit_pending.json).
    ///
    /// Segue o padrao de DashboardJSONReader, com duas diferencas deliberadas:
    ///
    /// 1. O semaforo e ESTATICO. O codebase cria uma instancia nova de leitor a
    ///    cada comando; um semaforo de instancia nao protegeria nada entre um
    ///    /audit-add e um /audit-push simultaneos. Por isso os comandos usam
    ///    sempre AuditStore.Instance.
    /// 2. A escrita e atomica (arquivo .tmp + File.Move). Uma queda no meio de um
    ///    File.WriteAllText trunca o arquivo, e aqui esta o historico inteiro do
    ///    regimento.
    /// </summary>
    internal sealed class AuditStore
    {
        public static AuditStore Instance { get; } = new();

        private static readonly SemaphoreSlim s_lock = new(1, 1);

        private readonly string _auditPath = Path.Combine("data", "audit.json");
        private readonly string _pendingPath = Path.Combine("data", "audit_pending.json");

        private AuditStore()
        {
            Directory.CreateDirectory("data");
        }

        public async Task<AuditFile> ReadAuditAsync()
        {
            await s_lock.WaitAsync().ConfigureAwait(false);
            try
            {
                return await ReadJsonAsync(_auditPath, () => new AuditFile()).ConfigureAwait(false);
            }
            finally
            {
                s_lock.Release();
            }
        }

        public async Task<PendingFile> ReadPendingAsync()
        {
            await s_lock.WaitAsync().ConfigureAwait(false);
            try
            {
                return await ReadJsonAsync(_pendingPath, () => new PendingFile()).ConfigureAwait(false);
            }
            finally
            {
                s_lock.Release();
            }
        }

        public async Task<(AuditFile Audit, PendingFile Pending)> ReadBothAsync()
        {
            await s_lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var audit = await ReadJsonAsync(_auditPath, () => new AuditFile()).ConfigureAwait(false);
                var pending = await ReadJsonAsync(_pendingPath, () => new PendingFile()).ConfigureAwait(false);
                return (audit, pending);
            }
            finally
            {
                s_lock.Release();
            }
        }

        /// <summary>
        /// Unica forma de escrever. Le os dois arquivos, aplica a mutacao e grava
        /// os dois - tudo sob o mesmo lock, para que nunca fiquem divergentes.
        /// </summary>
        public async Task<T> UpdateAsync<T>(Func<AuditFile, PendingFile, T> mutate)
        {
            await s_lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var audit = await ReadJsonAsync(_auditPath, () => new AuditFile()).ConfigureAwait(false);
                var pending = await ReadJsonAsync(_pendingPath, () => new PendingFile()).ConfigureAwait(false);

                var result = mutate(audit, pending);

                audit.lastUpdatedUtc = DateTimeOffset.UtcNow;
                await WriteJsonAsync(_auditPath, audit).ConfigureAwait(false);
                await WriteJsonAsync(_pendingPath, pending).ConfigureAwait(false);

                return result;
            }
            finally
            {
                s_lock.Release();
            }
        }

        private static async Task<T> ReadJsonAsync<T>(string path, Func<T> fallback)
        {
            try
            {
                if (!File.Exists(path))
                    return fallback();

                var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                    return fallback();

                return JsonConvert.DeserializeObject<T>(json) ?? fallback();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[audit] falha ao ler {path}: {ex.Message}");
                return fallback();
            }
        }

        private static async Task WriteJsonAsync<T>(string path, T value)
        {
            var json = JsonConvert.SerializeObject(value, Formatting.Indented);
            var tmp = path + ".tmp";

            await File.WriteAllTextAsync(tmp, json).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
        }

        /// <summary>Serializa o arquivo consolidado do jeito que vai para o GitHub.</summary>
        public static string Serialize<T>(T value) => JsonConvert.SerializeObject(value, Formatting.Indented);

        /// <summary>
        /// Le de volta um arquivo serializado (por exemplo o que esta publicado no
        /// GitHub). Devolve null se o conteudo estiver corrompido: quem chama trata
        /// isso como "diferente do local" em vez de quebrar.
        /// </summary>
        public static T? Deserialize<T>(string json) where T : class
        {
            try
            {
                return JsonConvert.DeserializeObject<T>(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
