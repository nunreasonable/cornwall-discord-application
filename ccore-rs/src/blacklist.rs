use std::{
    sync::{Arc, Mutex},
    time::{Duration, Instant},
};

use chrono::{DateTime, Utc};
use poise::serenity_prelude as serenity;
use serenity::Mentionable;
use tracing::{info, warn};
use unicode_normalization::{UnicodeNormalization, char::is_combining_mark};

use crate::{config::BlacklistConfig, dm::DmLimiter};

const MAX_PENDING: usize = 500;
const DEFAULT_COOLDOWN_MINUTES: u64 = 5;

struct Term {
    /// Como veio do config: e o que aparece no {term} e nos alertas.
    original: String,
    normalized: String,
}

struct Infraction {
    at: DateTime<Utc>,
    author: String,
    author_id: u64,
    term: String,
    channel: String,
    content: String,
}

#[derive(Default)]
struct Alerts {
    pending: Vec<Infraction>,
    next_allowed: Option<Instant>,
    flushing: bool,
    dropped: usize,
}

/// Responde a termos proibidos e avisa a moderacao por DM. E lombada, nao
/// cerca: quem quer burlar separa com ponto e passa, e a moderacao de verdade
/// e humana.
pub struct Blacklist {
    primary: Vec<Term>,
    secondary: Vec<Term>,
    response: String,
    response2: Option<String>,
    notify: Vec<serenity::UserId>,
    cooldown: Duration,
    limiter: Arc<DmLimiter>,
    alerts: Mutex<Alerts>,
}

/// Minuscula e sem acento: "desgracado" sem cedilha pega "desgraçado".
fn normalize(text: &str) -> String {
    let plain: String = text.nfd().filter(|c| !is_combining_mark(*c)).collect();
    plain.nfc().collect::<String>().to_lowercase()
}

/// Palavra inteira nas pontas que sao letra ou digito: com "fascista" na
/// lista, "antifascista" nao pode pegar. Ponta com pontuacao nao exige
/// fronteira, senao o termo nunca casaria.
fn contains_term(text: &str, term: &str) -> bool {
    let is_word = |c: char| c.is_alphanumeric() || c == '_';
    let start_bound = term.chars().next().is_some_and(char::is_alphanumeric);
    let end_bound = term.chars().next_back().is_some_and(char::is_alphanumeric);

    text.match_indices(term).any(|(i, found)| {
        let before = text[..i].chars().next_back();
        let after = text[i + found.len()..].chars().next();
        (!start_bound || !before.is_some_and(is_word))
            && (!end_bound || !after.is_some_and(is_word))
    })
}

fn first_hit<'t>(terms: &'t [Term], text: &str) -> Option<&'t str> {
    terms
        .iter()
        .find(|t| contains_term(text, &t.normalized))
        .map(|t| t.original.as_str())
}

/// Repetidos pela forma normalizada viram um so termo.
fn compile(terms: &[String]) -> Vec<Term> {
    let mut compiled: Vec<Term> = Vec::new();
    for term in terms.iter().map(|t| t.trim()).filter(|t| !t.is_empty()) {
        let normalized = normalize(term);
        if !normalized.is_empty() && !compiled.iter().any(|c| c.normalized == normalized) {
            compiled.push(Term {
                original: term.to_owned(),
                normalized,
            });
        }
    }
    compiled
}

/// Troca o marcador sem diferenciar caixa. Os marcadores sao ASCII.
fn replace_marker(text: &str, marker: &str, value: &str) -> String {
    let lower = text.to_ascii_lowercase();
    let mut out = String::with_capacity(text.len());
    let mut last = 0;
    for (i, _) in lower.match_indices(marker) {
        out.push_str(&text[last..i]);
        out.push_str(value);
        last = i + marker.len();
    }
    out.push_str(&text[last..]);
    out
}

fn cut(text: &str, max: usize) -> String {
    if text.chars().count() > max {
        format!("{}...", text.chars().take(max).collect::<String>())
    } else {
        text.to_owned()
    }
}

impl Blacklist {
    pub fn new(config: &BlacklistConfig, limiter: Arc<DmLimiter>) -> Option<Self> {
        if !config.enabled {
            return None;
        }

        let mut notify: Vec<serenity::UserId> = Vec::new();
        for &id in config.notify_user_ids.iter().filter(|&&id| id != 0) {
            let id = serenity::UserId::new(id);
            if !notify.contains(&id) && notify.len() < 2 {
                notify.push(id);
            }
        }

        let minutes = config
            .dm_alert_cooldown_minutes
            .map_or(DEFAULT_COOLDOWN_MINUTES, |m| m.clamp(1, 60));

        Some(Self {
            primary: compile(&config.blacklisted_terms),
            secondary: compile(&config.response_message2_terms),
            response: config.response_message.clone(),
            response2: config
                .response_message2
                .clone()
                .filter(|r| !r.trim().is_empty()),
            notify,
            cooldown: Duration::from_secs(60 * minutes),
            limiter,
            alerts: Mutex::default(),
        })
    }

    /// O termo achado e se veio da lista secundaria. A secundaria ganha: uma
    /// frase dela nao pode ser sequestrada por um termo primario na mesma
    /// mensagem.
    fn find(&self, content: &str) -> Option<(&str, bool)> {
        let text = normalize(content);

        if self.response2.is_some()
            && let Some(term) = first_hit(&self.secondary, &text)
        {
            return Some((term, true));
        }
        first_hit(&self.primary, &text).map(|term| (term, false))
    }

    pub async fn handle(self: &Arc<Self>, ctx: &serenity::Context, msg: &serenity::Message) {
        if msg.content.trim().is_empty() {
            return;
        }
        let Some((term, secondary)) = self.find(&msg.content) else {
            return;
        };

        let template = match (&self.response2, secondary) {
            (Some(second), true) => second,
            _ => &self.response,
        };
        let mut reply = template.clone();
        for (marker, value) in [
            ("{user}", msg.author.id.to_string()),
            ("{mention}", msg.author.mention().to_string()),
            ("{term}", term.to_owned()),
            ("{channel}", msg.channel_id.mention().to_string()),
        ] {
            reply = replace_marker(&reply, marker, &value);
        }

        if !reply.trim().is_empty() {
            // Sem mencoes: um @everyone no texto do config dispararia a cada
            // infracao.
            let message = serenity::CreateMessage::new()
                .content(cut(&reply, 1997))
                .reference_message(msg)
                .allowed_mentions(serenity::CreateAllowedMentions::new());
            if let Err(cause) = msg.channel_id.send_message(ctx, message).await {
                warn!("falha ao responder infracao da blacklist: {cause}");
            }
        }

        // O alerta sai mesmo sem texto de resposta: infracao nunca some calada.
        self.notify(ctx, msg, term).await;
        info!(
            "blacklist: mensagem {} de {} (termo \"{term}\")",
            msg.id, msg.author.name
        );
    }

    fn alerts(&self) -> std::sync::MutexGuard<'_, Alerts> {
        self.alerts.lock().unwrap_or_else(|e| e.into_inner())
    }

    /// O primeiro alerta sai na hora; o que vier durante o cooldown vira um
    /// resumo no fim dele. Sem isso, um flood de infracoes transformava o bot
    /// num spammer de DM, e e assim que bot e colocado em quarentena.
    async fn notify(
        self: &Arc<Self>,
        ctx: &serenity::Context,
        msg: &serenity::Message,
        term: &str,
    ) {
        if self.notify.is_empty() {
            return;
        }

        let content = if msg.content.trim().is_empty() {
            "[sem texto]".to_owned()
        } else {
            cut(&msg.content, 300)
        };
        let infraction = Infraction {
            at: Utc::now(),
            author: msg.author.name.clone(),
            author_id: msg.author.id.get(),
            term: term.to_owned(),
            channel: msg.channel_id.mention().to_string(),
            content,
        };

        let (send_now, start_flush) = {
            let mut alerts = self.alerts();
            let now = Instant::now();
            let send_now = alerts.next_allowed.is_none_or(|at| now >= at);
            let mut infraction = Some(infraction);

            if send_now {
                alerts.next_allowed = Some(now + self.cooldown);
            } else if alerts.pending.len() < MAX_PENDING {
                alerts.pending.extend(infraction.take());
            } else {
                alerts.dropped += 1;
            }

            let start = !alerts.flushing;
            alerts.flushing = true;
            (send_now.then(|| infraction.take()).flatten(), start)
        };

        if start_flush {
            let me = self.clone();
            let http = ctx.http.clone();
            tokio::spawn(async move { me.flush(&http).await });
        }

        if let Some(infraction) = send_now {
            self.send_alert(&ctx.http, &single_alert(&infraction)).await;
        }
    }

    /// Dorme ate a janela fechar e manda um resumo do que acumulou. Sai quando
    /// uma janela fecha sem nada pendente.
    async fn flush(&self, http: &serenity::Http) {
        loop {
            let next = self.alerts().next_allowed;
            if let Some(at) = next {
                tokio::time::sleep_until(at.into()).await;
            }

            let (batch, dropped) = {
                let mut alerts = self.alerts();
                if alerts.pending.is_empty() {
                    alerts.flushing = false;
                    return;
                }
                alerts.next_allowed = Some(Instant::now() + self.cooldown);
                (
                    std::mem::take(&mut alerts.pending),
                    std::mem::take(&mut alerts.dropped),
                )
            };

            self.send_alert(http, &bulk_alert(&batch, dropped)).await;
        }
    }

    async fn send_alert(&self, http: &serenity::Http, text: &str) {
        for &user in &self.notify {
            // O mesmo limitador dos comandos de DM: dois destinatarios seguidos
            // ainda sairiam em rajada.
            self.limiter.wait().await;
            let message = serenity::CreateMessage::new().content(text);
            if let Err(cause) = user.direct_message(http, message).await {
                warn!("falha ao enviar alerta da blacklist para {user}: {cause}");
            }
        }
    }
}

fn single_alert(i: &Infraction) -> String {
    format!(
        "⚠️ Aviso: conteúdo da blacklist foi dito.\nUsuário: {} ({})\nTermo detectado: {}\nCanal: {}\nMensagem: {}",
        i.author, i.author_id, i.term, i.channel, i.content
    )
}

fn bulk_alert(infractions: &[Infraction], dropped: usize) -> String {
    let mut text = format!(
        "⚠️ Aviso: {} novas infrações durante o cooldown.\nResumo:\n",
        infractions.len() + dropped
    );

    let mut included = 0;
    for i in infractions {
        let line = format!(
            "- [{}] {} ({}) | termo: {} | canal: {} | msg: {}\n",
            i.at.format("%H:%M:%S"),
            i.author,
            i.author_id,
            i.term,
            i.channel,
            i.content
        );
        if text.chars().count() + line.chars().count() > 1800 {
            break;
        }
        text.push_str(&line);
        included += 1;
    }

    let omitted = infractions.len() - included + dropped;
    if omitted > 0 {
        text.push_str(&format!("... e mais {omitted} infrações.\n"));
    }
    text
}

#[cfg(test)]
mod tests {
    use super::*;

    fn blacklist(primary: &[&str], secondary: &[&str], response2: Option<&str>) -> Blacklist {
        let config = BlacklistConfig {
            enabled: true,
            blacklisted_terms: primary.iter().map(|t| (*t).to_owned()).collect(),
            response_message2_terms: secondary.iter().map(|t| (*t).to_owned()).collect(),
            response_message2: response2.map(str::to_owned),
            ..BlacklistConfig::default()
        };
        Blacklist::new(&config, Arc::default()).unwrap()
    }

    #[test]
    fn so_casa_palavra_inteira_e_ignora_acento() {
        let b = blacklist(&["fascista", "Desgraçado"], &[], None);
        assert_eq!(b.find("que FASCISTA!"), Some(("fascista", false)));
        assert_eq!(b.find("sou antifascista"), None);
        assert_eq!(b.find("seu desgracado"), Some(("Desgraçado", false)));
        assert_eq!(b.find("fascista_x"), None);
    }

    #[test]
    fn termo_com_pontuacao_na_ponta_ainda_casa() {
        let b = blacklist(&["!ban"], &[], None);
        assert!(b.find("ok!ban agora").is_some());
    }

    #[test]
    fn secundaria_ganha_so_com_resposta_propria() {
        let with = blacklist(&["foo"], &["foo bar"], Some("outra"));
        assert_eq!(with.find("foo bar"), Some(("foo bar", true)));
        let without = blacklist(&["foo"], &["foo bar"], None);
        assert_eq!(without.find("foo bar"), Some(("foo", false)));
    }

    #[test]
    fn marcador_troca_sem_diferenciar_caixa() {
        assert_eq!(
            replace_marker("oi {Mention}, {mention}", "{mention}", "@a"),
            "oi @a, @a"
        );
    }

    #[test]
    fn resumo_cabe_numa_dm() {
        let many: Vec<Infraction> = (0..50)
            .map(|n| Infraction {
                at: Utc::now(),
                author: format!("user{n}"),
                author_id: n,
                term: "x".to_owned(),
                channel: "<#1>".to_owned(),
                content: "y".repeat(80),
            })
            .collect();
        let text = bulk_alert(&many, 3);
        assert!(text.chars().count() < 2000);
        assert!(text.contains("53 novas") && text.contains("... e mais"));
    }
}
