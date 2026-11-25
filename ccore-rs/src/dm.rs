use std::{
    pin::pin,
    sync::atomic::{AtomicBool, Ordering},
    time::{Duration, Instant},
};

use poise::{futures_util::StreamExt, serenity_prelude as serenity};
use tokio::sync::Mutex;

use crate::embeds::truncate;

pub const MAX_MEMBERS: usize = 500;
const MIN_GAP: Duration = Duration::from_secs(3);
const SPACING: Duration = Duration::from_millis(1200);
const MAX_CONSECUTIVE_FAILURES: usize = 12;
const MAX_SAMPLES: usize = 25;

/// Pelo menos 3s entre duas DMs em todo o bot, para o Discord nao tratar como
/// spam. O Mutex do tokio atende por ordem de chegada e fica preso durante a
/// espera: e isso que impede duas tarefas de calcular a mesma folga.
#[derive(Default)]
pub struct DmLimiter {
    last: Mutex<Option<Instant>>,
}

impl DmLimiter {
    pub async fn wait(&self) {
        let mut last = self.last.lock().await;
        if let Some(wait) = last.and_then(|at| MIN_GAP.checked_sub(at.elapsed())) {
            tokio::time::sleep(wait).await;
        }
        *last = Some(Instant::now());
    }
}

pub struct Recipients {
    pub members: Vec<serenity::Member>,
    pub name: String,
}

/// O Err e a frase pronta para o usuario. Se vierem os dois, vale o usuario.
pub async fn resolve(
    ctx: &serenity::Context,
    guild_id: serenity::GuildId,
    role: Option<&serenity::Role>,
    user: Option<&serenity::User>,
) -> Result<Recipients, String> {
    if let Some(user) = user {
        let member = guild_id
            .member(ctx, user.id)
            .await
            .map_err(|_| "O usuário fornecido não é um membro deste servidor.".to_owned())?;
        let name = member.display_name().to_owned();
        return Ok(Recipients {
            members: vec![member],
            name,
        });
    }

    let Some(role) = role else {
        return Err(
            "Você deve fornecer **um cargo** ou **um usuário** para enviar a mensagem.".to_owned(),
        );
    };

    // O cache so tem parte dos membros: a lista inteira vem da API.
    let mut members = Vec::new();
    let mut all = pin!(guild_id.members_iter(ctx));
    while let Some(member) = all.next().await {
        let member = member.map_err(|e| {
            format!(
                "Não consegui listar os membros do servidor: {}",
                truncate(&e.to_string(), 150)
            )
        })?;
        if !member.user.bot && member.roles.contains(&role.id) {
            members.push(member);
        }
    }

    let name = role.name.clone();
    if members.is_empty() {
        Err(format!(
            "Não foi encontrado nenhum membro com o cargo **{name}** (excluindo bots)."
        ))
    } else if members.len() > MAX_MEMBERS {
        Err(format!(
            "O cargo **{name}** possui {} membros. Para evitar timeouts ou rate limits, limite o envio a {MAX_MEMBERS} membros por execução.",
            members.len()
        ))
    } else {
        Ok(Recipients { members, name })
    }
}

/// Por que uma DM nao chegou, agrupado no relatorio final.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub enum FailKind {
    DmsClosed,
    RateLimited,
    NotFound,
    Other,
}

impl FailKind {
    fn label(self) -> &'static str {
        match self {
            Self::DmsClosed => "DMs fechadas ou bot bloqueado",
            Self::RateLimited => "Rate limit do Discord (o envio parou aqui)",
            Self::NotFound => "Usuário ou membro não encontrado",
            Self::Other => "Outros erros",
        }
    }
}

pub struct Failure {
    pub name: String,
    pub kind: FailKind,
    /// Erro curto, so mostrado no grupo "Outros erros".
    pub detail: String,
}

pub struct SendResult {
    pub total: usize,
    pub sent: usize,
    pub failed: usize,
    /// Amostra curta "nome (motivo)" que o dashboard mostra.
    pub samples: Vec<String>,
    /// Todo mundo que recebeu e todo mundo que nao recebeu, para o relatorio.
    pub delivered: Vec<String>,
    pub failures: Vec<Failure>,
    /// Motivo do corte, quando o envio parou antes do fim.
    pub aborted: Option<String>,
    /// Parou porque alguem pediu (job do dashboard).
    pub cancelled: bool,
}

/// Manda um por um. Para no primeiro sinal de rate limit, ou depois de falhas
/// demais em sequencia: insistir so piora o limite e faz o Discord marcar o bot
/// como spam. DM fechada sozinha e normal e nao para nada.
///
/// `progress` recebe (enviadas, falhas) depois de cada tentativa, e `cancel` e
/// olhado antes de cada uma.
pub async fn send_tracked(
    ctx: &serenity::Context,
    limiter: &DmLimiter,
    members: &[serenity::Member],
    message: &serenity::CreateMessage,
    cancel: &AtomicBool,
    progress: impl Fn(usize, usize),
) -> SendResult {
    let mut result = SendResult {
        total: members.len(),
        sent: 0,
        failed: 0,
        samples: Vec::new(),
        delivered: Vec::new(),
        failures: Vec::new(),
        aborted: None,
        cancelled: false,
    };
    let mut streak = 0;

    for (i, member) in members.iter().enumerate() {
        if i > 0 {
            tokio::time::sleep(SPACING).await;
        }
        if cancel.load(Ordering::Relaxed) {
            result.cancelled = true;
            break;
        }
        limiter.wait().await;

        let Err(cause) = member.user.direct_message(ctx, message.clone()).await else {
            result.delivered.push(member.display_name().to_owned());
            result.sent += 1;
            streak = 0;
            progress(result.sent, result.failed);
            continue;
        };

        result.failed += 1;
        streak += 1;
        progress(result.sent, result.failed);
        let reason = failure_reason(&cause);
        if result.samples.len() < MAX_SAMPLES {
            result
                .samples
                .push(format!("{} ({reason})", member.display_name()));
        }
        result.failures.push(Failure {
            name: member.display_name().to_owned(),
            kind: failure_kind(&cause),
            detail: reason.clone(),
        });

        if is_rate_limit(&cause) {
            result.aborted = Some(format!(
                "Interrompido por sinal de rate limit do Discord após {} envio(s): {reason}.",
                result.sent
            ));
            break;
        }

        if streak >= MAX_CONSECUTIVE_FAILURES {
            result.aborted = Some(format!(
                "Interrompido após {streak} falhas seguidas ({} envio(s) concluídos).",
                result.sent
            ));
            break;
        }
    }

    result
}

/// Cada DM custa ~4,2s (o limitador de 3s mais o espacamento).
const SECONDS_PER_DM: u64 = 4;
/// Teto do Discord para o valor de um field.
const FIELD_MAX: usize = 1024;

/// Embed de andamento, trocado a cada poucos segundos durante o envio.
pub fn progress_embed(
    target: &str,
    total: usize,
    sent: usize,
    failed: usize,
) -> serenity::CreateEmbed {
    let done = sent + failed;
    let left = total.saturating_sub(done);
    serenity::CreateEmbed::new()
        .title("Enviando DMs…")
        .colour(serenity::Colour::BLURPLE)
        .description(format!(
            "{} {done}/{total}\nFaltam ~{} (o Discord não deixa ir mais rápido sem marcar o bot como spam).",
            bar(done, total),
            eta(left)
        ))
        .field("Alvo", target, true)
        .field("Enviadas", sent.to_string(), true)
        .field("Falhas", failed.to_string(), true)
        .timestamp(serenity::Timestamp::now())
}

/// O relatorio final, no lugar do embed de andamento: quem recebeu e, por
/// motivo, quem nao recebeu.
pub fn report_embed(
    target: &str,
    result: &SendResult,
    took: std::time::Duration,
) -> serenity::CreateEmbed {
    let (title, colour) = if result.cancelled {
        ("Envio cancelado", serenity::Colour::LIGHT_GREY)
    } else if result.sent == 0 && result.total > 0 {
        ("Ninguém recebeu", serenity::Colour::RED)
    } else if result.aborted.is_some() || result.failed > 0 {
        ("Envio concluído com falhas", serenity::Colour::ORANGE)
    } else {
        ("Envio concluído", serenity::Colour::DARK_GREEN)
    };

    let mut notes = Vec::new();
    if let Some(reason) = &result.aborted {
        notes.push(reason.clone());
    }
    let never = result.total.saturating_sub(result.sent + result.failed);
    if never > 0 {
        notes.push(format!("{never} pessoa(s) nem chegaram a ser tentadas."));
    }
    if result.sent == 0
        && result
            .failures
            .iter()
            .any(|f| f.kind == FailKind::DmsClosed)
    {
        notes.push(
            "Verifique se os destinatários permitem DMs de membros do servidor \
             (Configurações do usuário > Privacidade)."
                .to_owned(),
        );
    }

    let mut embed = serenity::CreateEmbed::new()
        .title(title)
        .colour(colour)
        .field("Alvo", target, true)
        .field("Total", result.total.to_string(), true)
        .field("Duração", duration(took), true)
        .timestamp(serenity::Timestamp::now());
    if !notes.is_empty() {
        embed = embed.description(truncate(&notes.join("\n"), 4000));
    }
    if !result.delivered.is_empty() {
        embed = embed.field(
            format!("Receberam ({})", result.sent),
            names(result.delivered.iter().map(String::as_str)),
            false,
        );
    }
    for (kind, group) in grouped(&result.failures) {
        let list = names(
            group
                .iter()
                .map(|f| {
                    if kind == FailKind::Other {
                        format!("{} ({})", f.name, f.detail)
                    } else {
                        f.name.clone()
                    }
                })
                .collect::<Vec<_>>()
                .iter()
                .map(String::as_str),
        );
        embed = embed.field(format!("{} ({})", kind.label(), group.len()), list, false);
    }
    embed
}

/// Falhas por motivo, na ordem do enum.
fn grouped(failures: &[Failure]) -> Vec<(FailKind, Vec<&Failure>)> {
    let mut groups: Vec<(FailKind, Vec<&Failure>)> = Vec::new();
    for failure in failures {
        match groups.iter_mut().find(|(kind, _)| *kind == failure.kind) {
            Some((_, group)) => group.push(failure),
            None => groups.push((failure.kind, vec![failure])),
        }
    }
    groups.sort_by_key(|(kind, _)| *kind);
    groups
}

/// Nomes separados por virgula, cortados para caber num field com "e mais N".
fn names<'a>(list: impl Iterator<Item = &'a str>) -> String {
    let list: Vec<&str> = list.collect();
    let mut out = String::new();
    for (i, name) in list.iter().enumerate() {
        let piece = if i == 0 {
            (*name).to_owned()
        } else {
            format!(", {name}")
        };
        let rest = list.len() - i;
        // Reserva espaco para o " e mais N" caso este nome nao caiba.
        let tail = format!(" … e mais {rest}");
        if out.chars().count() + piece.chars().count() + tail.chars().count() > FIELD_MAX {
            out.push_str(&tail);
            return out;
        }
        out.push_str(&piece);
    }
    if out.is_empty() {
        "—".to_owned()
    } else {
        out
    }
}

fn bar(done: usize, total: usize) -> String {
    const WIDTH: usize = 20;
    let filled = (done * WIDTH)
        .checked_div(total)
        .unwrap_or(WIDTH)
        .min(WIDTH);
    format!("`{}{}`", "█".repeat(filled), "░".repeat(WIDTH - filled))
}

fn eta(left: usize) -> String {
    duration(std::time::Duration::from_secs(left as u64 * SECONDS_PER_DM))
}

fn duration(d: std::time::Duration) -> String {
    let secs = d.as_secs();
    match (secs / 60, secs % 60) {
        (0, s) => format!("{s}s"),
        (m, s) => format!("{m}min {s:02}s"),
    }
}

/// Status HTTP e codigo JSON do Discord, quando o erro veio da API.
fn discord_error(error: &serenity::Error) -> Option<(u16, isize)> {
    match error {
        serenity::Error::Http(serenity::HttpError::UnsuccessfulRequest(response)) => {
            Some((response.status_code.as_u16(), response.error.code))
        }
        _ => None,
    }
}

fn failure_kind(error: &serenity::Error) -> FailKind {
    match discord_error(error) {
        Some((403, _) | (_, 50007)) => FailKind::DmsClosed,
        Some((429, _) | (_, 40003)) => FailKind::RateLimited,
        Some((404, _) | (_, 10013 | 10007)) => FailKind::NotFound,
        _ => FailKind::Other,
    }
}

fn failure_reason(error: &serenity::Error) -> String {
    match discord_error(error) {
        Some((403, _) | (_, 50007)) => "DMs desativadas ou bot bloqueado".to_owned(),
        _ => truncate(&error.to_string(), 80),
    }
}

/// 40003 e o "opening direct messages too fast".
fn is_rate_limit(error: &serenity::Error) -> bool {
    matches!(discord_error(error), Some((429, _) | (_, 40003)))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn failure(name: &str, kind: FailKind) -> Failure {
        Failure {
            name: name.to_owned(),
            kind,
            detail: "erro".to_owned(),
        }
    }

    #[test]
    fn agrupa_falhas_por_motivo_na_ordem() {
        let failures = vec![
            failure("a", FailKind::Other),
            failure("b", FailKind::DmsClosed),
            failure("c", FailKind::DmsClosed),
        ];
        let groups = grouped(&failures);
        assert_eq!(groups.len(), 2);
        assert_eq!(groups[0].0, FailKind::DmsClosed);
        assert_eq!(groups[0].1.len(), 2);
        assert_eq!(groups[1].0, FailKind::Other);
    }

    #[test]
    fn lista_de_nomes_cabe_no_field() {
        let many: Vec<String> = (0..500).map(|i| format!("pessoa{i}")).collect();
        let text = names(many.iter().map(String::as_str));
        assert!(text.chars().count() <= FIELD_MAX);
        assert!(text.contains("… e mais"));
        assert_eq!(names(["a", "b"].into_iter()), "a, b");
        assert_eq!(names(std::iter::empty()), "—");
    }

    #[test]
    fn barra_e_tempo() {
        assert_eq!(bar(0, 10), format!("`{}`", "░".repeat(20)));
        assert_eq!(bar(10, 10), format!("`{}`", "█".repeat(20)));
        assert_eq!(bar(0, 0), format!("`{}`", "█".repeat(20)));
        assert_eq!(duration(std::time::Duration::from_secs(42)), "42s");
        assert_eq!(duration(std::time::Duration::from_secs(125)), "2min 05s");
    }

    #[tokio::test(start_paused = true)]
    async fn limitador_espaca_tres_segundos() {
        let limiter = DmLimiter::default();
        let start = tokio::time::Instant::now();
        limiter.wait().await;
        limiter.wait().await;
        assert!(start.elapsed() >= Duration::from_secs(3));
    }
}
