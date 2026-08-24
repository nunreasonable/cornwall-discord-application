using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using DisCatSharp;

namespace CornwallUtilities.Services
{
    /// <summary>Dados de saude que podem aparecer numa pagina publica.</summary>
    internal sealed record PublicStatus(
        bool online,
        string uptime,
        long uptimeSeconds,
        int latencyMs,
        int guilds,
        long members,
        string version,
        string library,
        DateTimeOffset measuredAtUtc);

    /// <summary>
    /// Dados da MAQUINA que hospeda o bot. Nunca vao para a pagina publica:
    /// e o mesmo motivo pelo qual o -osinfo e restrito a staff.
    /// </summary>
    internal sealed record HostStatus(
        string os,
        string architecture,
        int cores,
        string? loadAverage,
        string machineUptime,
        string? disk,
        string dotnet,
        string gcMemory,
        string? residentMemory,
        string? threads);

    /// <summary>
    /// Fonte unica dos dados de estado do bot.
    ///
    /// Vive aqui, e nao dentro do -osinfo, porque agora tem dois consumidores: o
    /// comando de prefixo e o endpoint /api/status. Duplicar a coleta faria os
    /// dois divergirem no primeiro ajuste.
    /// </summary>
    internal static class BotStatusSnapshot
    {
        public static IEnumerable<(string, string)> SystemLines()
        {
            yield return ("SO", RuntimeInformation.OSDescription);
            yield return ("Arquitetura", $"{RuntimeInformation.OSArchitecture} (processo: {RuntimeInformation.ProcessArchitecture})");
            yield return ("Núcleos", Environment.ProcessorCount.ToString());

            var load = LoadAverage();
            if (load is not null)
                yield return ("Carga média", load);

            yield return ("Uptime da máquina", FormatSpan(TimeSpan.FromMilliseconds(Environment.TickCount64)));

            var disk = DiskUsage();
            if (disk is not null)
                yield return ("Disco", disk);
        }

        public static IEnumerable<(string, string)> ProcessLines()
        {
            yield return (".NET", RuntimeInformation.FrameworkDescription);

            var version = BotVersion();
            if (version is not null)
                yield return ("Versão do bot", version);

            // O uptime que interessa e o do BOT, nao o da maquina: o processo
            // pode ter reiniciado ha um minuto numa maquina ligada ha semanas.
            //
            // A coleta acontece antes dos yields porque C# nao permite yield
            // dentro de um try com catch.
            foreach (var row in CurrentProcessLines())
                yield return row;

            yield return ("Memória do GC", GcMemory());
        }

        public static IEnumerable<(string, string)> DiscordLines(DiscordClient client)
        {
            yield return ("Latência", $"{client.Ping} ms");
            yield return ("Servidores", client.Guilds.Count.ToString());
            yield return ("Membros", MemberCount(client).ToString("N0"));
            yield return ("Biblioteca", $"DisCatSharp {client.VersionString}");
        }

        /// <summary>Recorte seguro para exibicao publica.</summary>
        public static PublicStatus Public(DiscordClient client)
        {
            var uptime = BotUptime();

            return new PublicStatus(
                online: true,
                uptime: FormatSpan(uptime),
                uptimeSeconds: (long)uptime.TotalSeconds,
                latencyMs: client.Ping,
                guilds: client.Guilds.Count,
                members: MemberCount(client),
                version: BotVersion() ?? "desconhecida",
                library: $"DisCatSharp {client.VersionString}",
                measuredAtUtc: DateTimeOffset.UtcNow);
        }

        /// <summary>Recorte sensivel. Exige nivel 2 na API (`GET /api/status?detail=host`).</summary>
        public static HostStatus Host()
        {
            var process = CurrentProcessLines().ToDictionary(r => r.Item1, r => r.Item2);

            return new HostStatus(
                os: RuntimeInformation.OSDescription,
                architecture: $"{RuntimeInformation.OSArchitecture} (processo: {RuntimeInformation.ProcessArchitecture})",
                cores: Environment.ProcessorCount,
                loadAverage: LoadAverage(),
                machineUptime: FormatSpan(TimeSpan.FromMilliseconds(Environment.TickCount64)),
                disk: DiskUsage(),
                dotnet: RuntimeInformation.FrameworkDescription,
                gcMemory: GcMemory(),
                residentMemory: process.GetValueOrDefault("Memória residente"),
                threads: process.GetValueOrDefault("Threads"));
        }

        private static long MemberCount(DiscordClient client) =>
            client.Guilds.Values.Sum(g => (long)(g.MemberCount ?? 0));

        private static string? BotVersion() =>
            Assembly.GetEntryAssembly()?.GetName().Version?.ToString();

        private static string GcMemory() =>
            $"{GC.GetTotalMemory(false) / (1024.0 * 1024.0):F1} MB";

        /// <summary>Ha quanto tempo o PROCESSO esta no ar. Zero se nao der para ler.</summary>
        public static TimeSpan BotUptime()
        {
            try
            {
                using var proc = Process.GetCurrentProcess();
                // UTC nos dois lados: DateTime.Now - StartTime (ambos locais) erra
                // em 1 hora numa transicao de horario de verao.
                var span = DateTime.UtcNow - proc.StartTime.ToUniversalTime();
                return span < TimeSpan.Zero ? TimeSpan.Zero : span;
            }
            catch (Exception)
            {
                return TimeSpan.Zero;
            }
        }

        private static List<(string, string)> CurrentProcessLines()
        {
            try
            {
                using var proc = Process.GetCurrentProcess();
                return new List<(string, string)>
                {
                    ("Uptime do bot", FormatSpan(DateTime.UtcNow - proc.StartTime.ToUniversalTime())),
                    ("Memória residente", $"{proc.WorkingSet64 / (1024.0 * 1024.0):F1} MB"),
                    ("Threads", proc.Threads.Count.ToString())
                };
            }
            catch (Exception)
            {
                // Alguns ambientes restringem a leitura do proprio processo; o
                // resto continua util sem esses campos.
                return new List<(string, string)>();
            }
        }

        /// <summary>Carga média do Linux. Null nos sistemas que não expõem /proc.</summary>
        private static string? LoadAverage()
        {
            try
            {
                if (!File.Exists("/proc/loadavg"))
                    return null;

                var parts = File.ReadAllText("/proc/loadavg").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return parts.Length >= 3 ? $"{parts[0]} {parts[1]} {parts[2]}" : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string? DiskUsage()
        {
            try
            {
                var path = Path.GetPathRoot(AppContext.BaseDirectory);
                if (string.IsNullOrWhiteSpace(path))
                    return null;

                var drive = new DriveInfo(path);
                if (!drive.IsReady)
                    return null;

                var totalGb = drive.TotalSize / (1024.0 * 1024 * 1024);
                var freeGb = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
                var usedPct = totalGb <= 0 ? 0 : (1 - freeGb / totalGb) * 100;
                return $"{freeGb:F1} GB livres de {totalGb:F1} GB ({usedPct:F0}% em uso)";
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static string FormatSpan(TimeSpan span)
        {
            if (span < TimeSpan.Zero)
                span = TimeSpan.Zero;

            if (span.TotalDays >= 1)
                return $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes}m";

            return span.TotalHours >= 1
                ? $"{span.Hours}h {span.Minutes}m"
                : $"{span.Minutes}m {span.Seconds}s";
        }
    }
}
