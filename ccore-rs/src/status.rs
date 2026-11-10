//! Fonte unica dos numeros de estado do bot: o -osinfo e o /api/status do
//! dashboard leem daqui, para nao divergirem no primeiro ajuste.

use std::{
    sync::{Arc, OnceLock},
    time::{Duration, Instant},
};

use chrono::{DateTime, Utc};
use poise::serenity_prelude as serenity;
use serde::Serialize;

static STARTED: OnceLock<Instant> = OnceLock::new();

const LIBRARY: &str = "Serenity 0.12 + Poise 0.7";

/// Chamar no comeco do main: o uptime que interessa e o do BOT.
pub fn mark_started() {
    STARTED.get_or_init(Instant::now);
}

pub fn bot_uptime() -> Duration {
    STARTED.get().map_or(Duration::ZERO, Instant::elapsed)
}

pub fn version() -> &'static str {
    env!("CARGO_PKG_VERSION")
}

pub fn format_span(span: Duration) -> String {
    let total = span.as_secs();
    let (days, hours, minutes, seconds) = (
        total / 86400,
        total / 3600 % 24,
        total / 60 % 60,
        total % 60,
    );
    if days > 0 {
        format!("{days}d {hours}h {minutes}m")
    } else if hours > 0 {
        format!("{hours}h {minutes}m")
    } else {
        format!("{minutes}m {seconds}s")
    }
}

fn read(path: &str) -> Option<String> {
    std::fs::read_to_string(path).ok()
}

fn os_name() -> String {
    let pretty = read("/etc/os-release").and_then(|text| {
        text.lines()
            .find_map(|l| l.strip_prefix("PRETTY_NAME="))
            .map(|v| v.trim_matches('"').to_owned())
    });
    let kernel = read("/proc/sys/kernel/osrelease").map(|k| k.trim().to_owned());
    match (pretty, kernel) {
        (Some(os), Some(kernel)) => format!("{os} (kernel {kernel})"),
        (Some(os), None) => os,
        (None, Some(kernel)) => format!("Linux {kernel}"),
        (None, None) => std::env::consts::OS.to_owned(),
    }
}

fn cores() -> usize {
    std::thread::available_parallelism().map_or(1, usize::from)
}

fn load_average() -> Option<String> {
    let text = read("/proc/loadavg")?;
    let parts: Vec<&str> = text.split_whitespace().take(3).collect();
    (parts.len() == 3).then(|| parts.join(" "))
}

fn machine_uptime() -> Option<String> {
    let seconds: f64 = read("/proc/uptime")?
        .split_whitespace()
        .next()?
        .parse()
        .ok()?;
    Some(format_span(Duration::from_secs_f64(seconds)))
}

/// Espaco no disco da raiz, pelo `df`: evita codigo unsafe so para o statvfs.
async fn disk_usage() -> Option<String> {
    let out = tokio::process::Command::new("df")
        .args(["-B1", "--output=size,avail", "/"])
        .output()
        .await
        .ok()?;
    let text = String::from_utf8(out.stdout).ok()?;
    let mut numbers = text
        .lines()
        .nth(1)?
        .split_whitespace()
        .map(|n| n.parse::<f64>());
    let (Some(Ok(total)), Some(Ok(free))) = (numbers.next(), numbers.next()) else {
        return None;
    };

    let gib = 1024.0 * 1024.0 * 1024.0;
    let used = if total > 0.0 {
        (1.0 - free / total) * 100.0
    } else {
        0.0
    };
    Some(format!(
        "{:.1} GB livres de {:.1} GB ({used:.0}% em uso)",
        free / gib,
        total / gib
    ))
}

/// Campo do /proc/self/status, como "VmRSS" ou "Threads".
fn proc_status(field: &str) -> Option<String> {
    read("/proc/self/status")?.lines().find_map(|l| {
        l.strip_prefix(field)
            .and_then(|rest| rest.strip_prefix(':'))
            .map(|v| v.trim().to_owned())
    })
}

fn resident_memory() -> Option<String> {
    let kb: f64 = proc_status("VmRSS")?
        .split_whitespace()
        .next()?
        .parse()
        .ok()?;
    Some(format!("{:.1} MB", kb / 1024.0))
}

pub async fn system_lines() -> Vec<(&'static str, String)> {
    let mut lines = vec![
        ("SO", os_name()),
        ("Arquitetura", std::env::consts::ARCH.to_owned()),
        ("Núcleos", cores().to_string()),
    ];
    lines.extend(load_average().map(|v| ("Carga média", v)));
    lines.extend(machine_uptime().map(|v| ("Uptime da máquina", v)));
    lines.extend(disk_usage().await.map(|v| ("Disco", v)));
    lines
}

pub fn process_lines() -> Vec<(&'static str, String)> {
    let mut lines = vec![
        ("Versão do bot", version().to_owned()),
        ("Uptime do bot", format_span(bot_uptime())),
    ];
    lines.extend(resident_memory().map(|v| ("Memória residente", v)));
    lines.extend(proc_status("Threads").map(|v| ("Threads", v)));
    lines
}

/// Latencia do heartbeat do shard, quando ja houve um.
pub async fn latency(
    shards: &Arc<serenity::ShardManager>,
    shard: serenity::ShardId,
) -> Option<Duration> {
    shards
        .runners
        .lock()
        .await
        .get(&shard)
        .and_then(|r| r.latency)
}

fn member_count(cache: &serenity::Cache) -> u64 {
    cache
        .guilds()
        .iter()
        .filter_map(|id| cache.guild(*id).map(|g| g.member_count))
        .sum()
}

pub fn discord_lines(
    cache: &serenity::Cache,
    latency: Option<Duration>,
) -> Vec<(&'static str, String)> {
    vec![
        (
            "Latência",
            latency.map_or_else(|| "—".to_owned(), |l| format!("{} ms", l.as_millis())),
        ),
        ("Servidores", cache.guild_count().to_string()),
        ("Membros", member_count(cache).to_string()),
        ("Biblioteca", LIBRARY.to_owned()),
    ]
}

/// Recorte seguro para pagina publica.
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PublicStatus {
    pub online: bool,
    pub uptime: String,
    pub uptime_seconds: u64,
    pub latency_ms: u128,
    pub guilds: usize,
    pub members: u64,
    pub version: String,
    pub library: String,
    pub measured_at_utc: DateTime<Utc>,
}

pub fn public(cache: &serenity::Cache, latency: Option<Duration>) -> PublicStatus {
    let uptime = bot_uptime();
    PublicStatus {
        online: true,
        uptime: format_span(uptime),
        uptime_seconds: uptime.as_secs(),
        latency_ms: latency.map_or(0, |l| l.as_millis()),
        guilds: cache.guild_count(),
        members: member_count(cache),
        version: version().to_owned(),
        library: LIBRARY.to_owned(),
        measured_at_utc: Utc::now(),
    }
}

/// Dados da MAQUINA: nunca vao para pagina publica, so nivel 2 no dashboard.
/// Os nomes dos campos sao os do bot em C#, que o painel ja le.
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct HostStatus {
    pub os: String,
    pub architecture: String,
    pub cores: usize,
    pub load_average: Option<String>,
    pub machine_uptime: String,
    pub disk: Option<String>,
    pub dotnet: String,
    pub gc_memory: String,
    pub resident_memory: Option<String>,
    pub threads: Option<String>,
}

pub async fn host() -> HostStatus {
    HostStatus {
        os: os_name(),
        architecture: std::env::consts::ARCH.to_owned(),
        cores: cores(),
        load_average: load_average(),
        machine_uptime: machine_uptime().unwrap_or_else(|| "—".to_owned()),
        disk: disk_usage().await,
        // O painel ainda le estas duas chaves; o bot agora e Rust, sem GC.
        dotnet: format!("Rust ({LIBRARY})"),
        gc_memory: "—".to_owned(),
        resident_memory: resident_memory(),
        threads: proc_status("Threads"),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn intervalo_igual_ao_do_csharp() {
        assert_eq!(format_span(Duration::from_secs(59)), "0m 59s");
        assert_eq!(format_span(Duration::from_secs(3 * 3600 + 120)), "3h 2m");
        assert_eq!(
            format_span(Duration::from_secs(2 * 86400 + 3600)),
            "2d 1h 0m"
        );
    }
}
