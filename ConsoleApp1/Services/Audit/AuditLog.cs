using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DisCatSharp.ApplicationCommands.Context;
using Newtonsoft.Json;

namespace CornwallUtilities.Services.Audit
{
    /// <summary>Uma acao registrada no historico da auditoria.</summary>
    internal sealed class AuditLogEntry
    {
        public DateTimeOffset timestampUtc { get; set; }
        public ulong userId { get; set; }
        public string username { get; set; } = string.Empty;
        public string action { get; set; } = string.Empty;
        public string details { get; set; } = string.Empty;
    }

    internal sealed class AuditLogFile
    {
        public List<AuditLogEntry> entries { get; set; } = new();
    }

    /// <summary>
    /// Historico de "quem fez o que" na auditoria (data/audit_log.json).
    ///
    /// Fica separado do audit.json de proposito: o consolidado e um retrato do
    /// estado atual e e sobrescrito a cada publicacao, enquanto o historico so
    /// cresce e precisa sobreviver a isso.
    ///
    /// Registrar NUNCA pode derrubar um comando: qualquer falha aqui vira uma
    /// linha de log no console e o comando segue normalmente.
    /// </summary>
    internal static class AuditLog
    {
        /// <summary>Teto do arquivo. Ao estourar, as entradas mais antigas saem.</summary>
        private const int MaxEntries = 2000;

        /// <summary>Nomes de acao usados pelos comandos - mantenha em sincronia com as opcoes de /audit-logs.</summary>
        public const string ActionAdd = "ADD";
        public const string ActionEdit = "EDIT";
        public const string ActionRemove = "REMOVE";
        public const string ActionSetRanks = "SETRANKS";
        public const string ActionImport = "IMPORT";
        public const string ActionPush = "PUSH";

        private static readonly SemaphoreSlim s_lock = new(1, 1);
        private static readonly string s_path = Path.Combine("data", "audit_log.json");

        /// <summary>Registra uma acao executada por quem rodou o comando.</summary>
        public static Task RecordAsync(InteractionContext ctx, string action, string details) =>
            RecordAsync(ctx.User.Id, ctx.User.Username, action, details);

        public static async Task RecordAsync(ulong userId, string username, string action, string details)
        {
            var entry = new AuditLogEntry
            {
                timestampUtc = DateTimeOffset.UtcNow,
                userId = userId,
                username = username,
                action = action,
                // O detalhe vai para dentro de um embed: cortar aqui evita que uma
                // entrada gigante inutilize a pagina inteira do /audit-logs.
                details = AuditEmbeds.Trim(details, 300)
            };

            await s_lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var file = await ReadUnlockedAsync().ConfigureAwait(false);
                file.entries.Add(entry);

                if (file.entries.Count > MaxEntries)
                    file.entries.RemoveRange(0, file.entries.Count - MaxEntries);

                await WriteUnlockedAsync(file).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[audit] falha ao registrar no historico: {ex.Message}");
            }
            finally
            {
                s_lock.Release();
            }
        }

        /// <summary>Le o historico inteiro, do mais antigo para o mais recente.</summary>
        public static async Task<List<AuditLogEntry>> ReadAsync()
        {
            await s_lock.WaitAsync().ConfigureAwait(false);
            try
            {
                return (await ReadUnlockedAsync().ConfigureAwait(false)).entries;
            }
            finally
            {
                s_lock.Release();
            }
        }

        private static async Task<AuditLogFile> ReadUnlockedAsync()
        {
            try
            {
                if (!File.Exists(s_path))
                    return new AuditLogFile();

                var json = await File.ReadAllTextAsync(s_path).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                    return new AuditLogFile();

                return JsonConvert.DeserializeObject<AuditLogFile>(json) ?? new AuditLogFile();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[audit] falha ao ler {s_path}: {ex.Message}");
                return new AuditLogFile();
            }
        }

        // Mesma escrita atomica do AuditStore: uma queda no meio de um
        // WriteAllText truncaria o historico inteiro.
        private static async Task WriteUnlockedAsync(AuditLogFile file)
        {
            Directory.CreateDirectory("data");

            var json = JsonConvert.SerializeObject(file, Formatting.Indented);
            var tmp = s_path + ".tmp";

            await File.WriteAllTextAsync(tmp, json).ConfigureAwait(false);
            File.Move(tmp, s_path, overwrite: true);
        }
    }
}
