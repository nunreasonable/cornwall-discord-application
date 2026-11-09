use std::{
    pin::pin,
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

pub struct SendResult {
    pub total: usize,
    pub sent: usize,
    pub failed: usize,
    pub samples: Vec<String>,
    /// Motivo do corte, quando o envio parou antes do fim.
    pub aborted: Option<String>,
}

/// Manda um por um. Para no primeiro sinal de rate limit, ou depois de falhas
/// demais em sequencia: insistir so piora o limite e faz o Discord marcar o bot
/// como spam. DM fechada sozinha e normal e nao para nada.
pub async fn send_all(
    ctx: &serenity::Context,
    limiter: &DmLimiter,
    members: &[serenity::Member],
    message: &serenity::CreateMessage,
) -> SendResult {
    let mut result = SendResult {
        total: members.len(),
        sent: 0,
        failed: 0,
        samples: Vec::new(),
        aborted: None,
    };
    let mut streak = 0;

    for (i, member) in members.iter().enumerate() {
        if i > 0 {
            tokio::time::sleep(SPACING).await;
        }
        limiter.wait().await;

        let Err(cause) = member.user.direct_message(ctx, message.clone()).await else {
            result.sent += 1;
            streak = 0;
            continue;
        };

        result.failed += 1;
        streak += 1;
        let reason = failure_reason(&cause);
        if result.samples.len() < MAX_SAMPLES {
            result
                .samples
                .push(format!("{} ({reason})", member.display_name()));
        }

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

/// Frase de ajuda quando alguem nao recebeu.
pub fn describe_failures(result: &SendResult) -> Option<String> {
    if result.failed == 0 {
        return None;
    }

    let sample = result
        .samples
        .iter()
        .take(10)
        .cloned()
        .collect::<Vec<_>>()
        .join(", ");
    let more = if result.failed > 10 { "..." } else { "" };
    let hint = if result.failed == result.total {
        " Ninguém recebeu: verifique se os destinatários permitem DMs de membros do servidor (Configurações do usuário > Privacidade)."
    } else {
        ""
    };

    Some(format!(
        "Falha ao enviar para {} usuário(s). Exemplo: {sample}{more}.{hint}",
        result.failed
    ))
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

    fn result(total: usize, failed: usize) -> SendResult {
        SendResult {
            total,
            sent: total - failed,
            failed,
            samples: (0..failed).map(|i| format!("u{i}")).collect(),
            aborted: None,
        }
    }

    #[test]
    fn sem_falha_nao_ha_frase() {
        assert!(describe_failures(&result(3, 0)).is_none());
    }

    #[test]
    fn so_avisa_da_privacidade_quando_ninguem_recebeu() {
        let all = describe_failures(&result(2, 2)).unwrap();
        let some = describe_failures(&result(3, 2)).unwrap();
        assert!(all.contains("Ninguém recebeu"));
        assert!(!some.contains("Ninguém recebeu"));
    }

    #[test]
    fn exemplo_mostra_no_maximo_dez() {
        let text = describe_failures(&result(20, 12)).unwrap();
        assert!(text.contains("u9") && !text.contains("u10"));
        assert!(text.contains("u9..."));
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
