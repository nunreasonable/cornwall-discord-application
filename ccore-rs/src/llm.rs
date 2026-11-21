//! IA local: o ccore responde quem menciona ele (ou responde a uma mensagem
//! dele) usando um modelo que roda nesta maquina (llama.cpp, ccore-llm.service).
//!
//! Desligada por padrao, sempre: o bot sobe com ela desligada e o servico do
//! modelo nao tem [Install], entao nao sobe no boot. So um operador liga, com
//! "-ia on"; "-ia off" ou o tempo sem uso desligam. Se o bot reiniciar com o
//! modelo rodando, ela continua ligada. Desligada, o modelo nao ocupa placa nem
//! RAM.

use std::{
    collections::HashMap,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
    },
    time::{Duration, Instant},
};

use poise::serenity_prelude as serenity;
use serde::{Deserialize, Serialize};
use serde_json::json;
use tokio::process::Command;
use tracing::{info, warn};

use crate::{blacklist::Blacklist, config::LlmConfig, embeds::truncate};

/// O modelo leva um tempo para carregar; antes disso o servidor responde 503.
const REQUEST_TIMEOUT: Duration = Duration::from_secs(120);
const PROBE_TIMEOUT: Duration = Duration::from_secs(3);
/// Resposta do Discord tem teto de 2000; sobra margem para o aviso de corte.
const MAX_REPLY: usize = 1900;

#[derive(Default)]
struct State {
    enabled_by: Option<String>,
    enabled_at: Option<Instant>,
    last_used: Option<Instant>,
    answered: u64,
    failures: u64,
    last_latency: Option<Duration>,
    cooldowns: HashMap<u64, Instant>,
}

pub struct Llm {
    config: LlmConfig,
    enabled: AtomicBool,
    state: Mutex<State>,
    /// Cliente proprio: o do bot recusa 127.0.0.1 de proposito (redirect ou
    /// config adulterado nao podem levar o bot a servicos internos). Aqui o
    /// endereco local e exatamente o alvo, e vem do config.
    http: reqwest::Client,
}

/// O que o /llm-status e a pagina de status mostram.
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Health {
    pub enabled: bool,
    pub service_active: bool,
    pub server_ready: bool,
    pub model: Option<String>,
}

#[derive(Deserialize)]
struct Models {
    data: Vec<ModelEntry>,
}

#[derive(Deserialize)]
struct ModelEntry {
    id: String,
}

#[derive(Deserialize)]
struct Completion {
    choices: Vec<Choice>,
}

#[derive(Deserialize)]
struct Choice {
    message: ChoiceMessage,
}

#[derive(Deserialize)]
struct ChoiceMessage {
    content: Option<String>,
}

fn lock(state: &Mutex<State>) -> std::sync::MutexGuard<'_, State> {
    state.lock().unwrap_or_else(|e| e.into_inner())
}

impl Llm {
    pub fn new(config: LlmConfig) -> Arc<Self> {
        let http = reqwest::Client::builder()
            .timeout(REQUEST_TIMEOUT)
            .build()
            .unwrap_or_default();
        Arc::new(Self {
            config,
            enabled: AtomicBool::new(false),
            state: Mutex::default(),
            http,
        })
    }

    pub fn is_enabled(&self) -> bool {
        self.enabled.load(Ordering::Relaxed)
    }

    pub fn is_operator(&self, user: u64) -> bool {
        self.config.operator_user_ids.contains(&user)
    }

    async fn systemctl(&self, verb: &str) -> Result<String, String> {
        let output = Command::new("systemctl")
            .args(["--user", verb, &self.config.service])
            .output()
            .await
            .map_err(|e| format!("nao consegui rodar o systemctl: {e}"))?;
        let text = String::from_utf8_lossy(&output.stdout).trim().to_owned();
        if output.status.success() || verb == "is-active" {
            Ok(text)
        } else {
            Err(String::from_utf8_lossy(&output.stderr).trim().to_owned())
        }
    }

    /// Sobe o servico do modelo e passa a responder. O modelo termina de
    /// carregar sozinho; ate la, quem pingar recebe "ainda carregando".
    pub async fn turn_on(&self, by: &str) -> Result<(), String> {
        self.systemctl("start").await?;
        self.enabled.store(true, Ordering::Relaxed);
        let mut state = lock(&self.state);
        state.enabled_by = Some(by.to_owned());
        state.enabled_at = Some(Instant::now());
        state.last_used = Some(Instant::now());
        info!("ia local ligada por {by}");
        Ok(())
    }

    /// Bot reiniciado com o modelo rodando: alguem ligou e ninguem desligou,
    /// entao a IA continua ligada.
    pub async fn resume_if_running(&self) {
        if self
            .systemctl("is-active")
            .await
            .is_ok_and(|s| s == "active")
        {
            self.enabled.store(true, Ordering::Relaxed);
            let mut state = lock(&self.state);
            state.enabled_by = Some("antes do reinício do bot".to_owned());
            state.enabled_at = Some(Instant::now());
            state.last_used = Some(Instant::now());
            info!("ia local: modelo ja estava rodando, continua ligada");
        }
    }

    /// Para de responder e derruba o servico, liberando placa e RAM.
    pub async fn turn_off(&self, why: &str) -> Result<(), String> {
        self.enabled.store(false, Ordering::Relaxed);
        lock(&self.state).enabled_by = None;
        info!("ia local desligada: {why}");
        self.systemctl("stop").await.map(|_| ())
    }

    pub async fn health(&self) -> Health {
        let service_active = self
            .systemctl("is-active")
            .await
            .is_ok_and(|s| s == "active");
        let base = self.config.base_url.trim_end_matches('/');
        let server_ready = service_active
            && self
                .http
                .get(format!("{base}/health"))
                .timeout(PROBE_TIMEOUT)
                .send()
                .await
                .is_ok_and(|r| r.status().is_success());
        let model = if server_ready {
            match self
                .http
                .get(format!("{base}/v1/models"))
                .timeout(PROBE_TIMEOUT)
                .send()
                .await
            {
                Ok(r) => r
                    .json::<Models>()
                    .await
                    .ok()
                    .and_then(|m| m.data.into_iter().next())
                    .map(|m| m.id),
                Err(_) => None,
            }
        } else {
            None
        };
        Health {
            enabled: self.is_enabled(),
            service_active,
            server_ready,
            model,
        }
    }

    /// Linhas de uso para o /llm-status.
    pub fn usage_lines(&self) -> Vec<(&'static str, String)> {
        let state = lock(&self.state);
        let ago = |t: Option<Instant>| {
            t.map_or("—".to_owned(), |t| {
                format!("há {}", crate::status::format_span(t.elapsed()))
            })
        };
        vec![
            (
                "Ligada por",
                state.enabled_by.clone().unwrap_or_else(|| "—".to_owned()),
            ),
            ("Ligada", ago(state.enabled_at)),
            ("Último uso", ago(state.last_used)),
            ("Respostas", state.answered.to_string()),
            ("Falhas", state.failures.to_string()),
            (
                "Última resposta",
                state
                    .last_latency
                    .map_or("—".to_owned(), |l| format!("{:.1} s", l.as_secs_f32())),
            ),
            (
                "Desliga sozinha",
                if self.config.idle_off_minutes == 0 {
                    "nunca".to_owned()
                } else {
                    format!("após {} min sem uso", self.config.idle_off_minutes)
                },
            ),
        ]
    }

    /// Memoria do servico (RAM) e da placa inteira, para o /llm-status.
    pub async fn memory_lines(&self) -> Vec<(&'static str, String)> {
        let mut rows = Vec::new();
        if let Ok(output) = Command::new("systemctl")
            .args([
                "--user",
                "show",
                "-p",
                "MemoryCurrent",
                "--value",
                &self.config.service,
            ])
            .output()
            .await
        {
            let raw = String::from_utf8_lossy(&output.stdout).trim().to_owned();
            if let Ok(bytes) = raw.parse::<u64>() {
                rows.push(("RAM do modelo", format!("{:.1} GB", bytes as f64 / 1e9)));
            }
        }
        if let Ok(output) = Command::new("nvidia-smi")
            .args([
                "--query-gpu=name,memory.used,memory.total",
                "--format=csv,noheader,nounits",
            ])
            .output()
            .await
        {
            let line = String::from_utf8_lossy(&output.stdout);
            let parts: Vec<&str> = line.trim().split(", ").collect();
            if let [name, used, total] = parts.as_slice() {
                rows.push(("Placa", (*name).to_owned()));
                rows.push(("VRAM em uso", format!("{used} / {total} MiB")));
            }
        }
        rows
    }

    /// A mensagem e para a IA: menciona o bot ou responde a uma mensagem dele,
    /// num canal liberado.
    pub fn wants(&self, msg: &serenity::Message, me: serenity::UserId) -> bool {
        if !self.is_enabled() || msg.guild_id.is_none() {
            return false;
        }
        let channels = &self.config.channel_ids;
        if !channels.is_empty() && !channels.contains(&msg.channel_id.get()) {
            return false;
        }
        msg.mentions_user_id(me)
            || msg
                .referenced_message
                .as_ref()
                .is_some_and(|r| r.author.id == me)
    }

    /// Responde a mensagem. Erros viram uma frase curta no canal e log; nunca
    /// derrubam o bot.
    pub async fn answer(
        &self,
        ctx: &serenity::Context,
        msg: &serenity::Message,
        blacklist: Option<&Arc<Blacklist>>,
    ) {
        let author = msg.author.id.get();
        if !self.is_operator(author) {
            let mut state = lock(&self.state);
            let wait = Duration::from_secs(self.config.cooldown_seconds);
            if let Some(last) = state.cooldowns.get(&author)
                && last.elapsed() < wait
            {
                return;
            }
            state.cooldowns.insert(author, Instant::now());
        }

        let reply_text = match self.complete(ctx, msg).await {
            Ok(text) => text,
            Err(Failure::Loading) => {
                "Ainda tô carregando o modelo, tenta de novo daqui a pouco.".to_owned()
            }
            Err(Failure::Other(cause)) => {
                warn!("ia local: {cause}");
                lock(&self.state).failures += 1;
                "Tô sem cabeça agora, tenta daqui a pouco.".to_owned()
            }
        };
        let reply_text = match blacklist {
            Some(list) if list.blocks(&reply_text) => "Não vou repetir isso.".to_owned(),
            _ => reply_text,
        };

        // Ninguem e pingado pela IA: nem cargo, nem @everyone, nem usuario.
        let message = serenity::CreateMessage::new()
            .content(reply_text)
            .reference_message(msg)
            .allowed_mentions(serenity::CreateAllowedMentions::new().replied_user(true));
        if let Err(cause) = msg.channel_id.send_message(ctx, message).await {
            warn!("ia local: falha ao responder: {cause}");
        }
    }

    async fn complete(
        &self,
        ctx: &serenity::Context,
        msg: &serenity::Message,
    ) -> Result<String, Failure> {
        let me = ctx.cache.current_user().id;
        let _typing = msg.channel_id.start_typing(&ctx.http);

        let mut messages = vec![json!({ "role": "system", "content": self.config.system_prompt })];
        // Contexto: as ultimas mensagens do canal antes desta, da mais velha
        // para a mais nova. Nada disso e gravado.
        if self.config.history > 0 {
            let builder = serenity::GetMessages::new()
                .before(msg.id)
                .limit(self.config.history.min(50) as u8);
            if let Ok(mut earlier) = msg.channel_id.messages(&ctx.http, builder).await {
                earlier.reverse();
                for m in earlier.iter().filter(|m| !m.content.trim().is_empty()) {
                    messages.push(turn(m, me));
                }
            }
        }
        messages.push(turn(msg, me));

        let base = self.config.base_url.trim_end_matches('/');
        let started = Instant::now();
        let response = self
            .http
            .post(format!("{base}/v1/chat/completions"))
            .json(&json!({
                "model": "ccore",
                "messages": messages,
                "max_tokens": self.config.max_tokens,
                "temperature": 0.7,
                "stream": false,
            }))
            .send()
            .await
            .map_err(|e| {
                if e.is_connect() {
                    Failure::Loading
                } else {
                    Failure::Other(e.without_url().to_string())
                }
            })?;
        if response.status() == reqwest::StatusCode::SERVICE_UNAVAILABLE {
            return Err(Failure::Loading);
        }
        let response = response
            .error_for_status()
            .map_err(|e| Failure::Other(e.without_url().to_string()))?;
        let body: Completion = response
            .json()
            .await
            .map_err(|e| Failure::Other(e.without_url().to_string()))?;
        let text = body
            .choices
            .into_iter()
            .next()
            .and_then(|c| c.message.content)
            .map(|t| strip_thinking(&t))
            .unwrap_or_default();
        if text.is_empty() {
            return Err(Failure::Other("o modelo devolveu texto vazio".to_owned()));
        }

        let mut state = lock(&self.state);
        state.answered += 1;
        state.last_used = Some(Instant::now());
        state.last_latency = Some(started.elapsed());
        Ok(truncate(&text, MAX_REPLY))
    }

    /// Desliga sozinha depois de um tempo sem uso, para nao ficar ocupando a
    /// placa se ninguem lembrar do "-ia off".
    pub fn spawn_idle_watch(self: &Arc<Self>) {
        if self.config.idle_off_minutes == 0 {
            return;
        }
        let llm = Arc::clone(self);
        let idle = Duration::from_secs(self.config.idle_off_minutes * 60);
        tokio::spawn(async move {
            loop {
                tokio::time::sleep(Duration::from_secs(60)).await;
                if !llm.is_enabled() {
                    continue;
                }
                let last = lock(&llm.state).last_used;
                if last.is_some_and(|t| t.elapsed() >= idle)
                    && let Err(cause) = llm.turn_off("sem uso").await
                {
                    warn!("ia local: nao consegui desligar o servico: {cause}");
                }
            }
        });
    }
}

enum Failure {
    Loading,
    Other(String),
}

/// Mensagem do Discord no formato do chat: o bot e o "assistant", o resto e
/// "user" com o nome na frente, para o modelo saber quem fala.
fn turn(m: &serenity::Message, me: serenity::UserId) -> serde_json::Value {
    let content = strip_mention(&m.content, me);
    if m.author.id == me {
        json!({ "role": "assistant", "content": content })
    } else {
        let name = m.author.global_name.as_deref().unwrap_or(&m.author.name);
        json!({ "role": "user", "content": format!("{name}: {content}") })
    }
}

fn strip_mention(text: &str, me: serenity::UserId) -> String {
    text.replace(&format!("<@{me}>"), "")
        .replace(&format!("<@!{me}>"), "")
        .trim()
        .to_owned()
}

/// Alguns modelos ainda mandam o raciocinio entre <think></think> mesmo com ele
/// desligado: so a resposta vai para o canal.
fn strip_thinking(text: &str) -> String {
    let text = match text.rfind("</think>") {
        Some(end) => &text[end + "</think>".len()..],
        None => text,
    };
    text.trim().to_owned()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn tira_o_raciocinio_e_a_mencao() {
        assert_eq!(strip_thinking("<think>hmm</think>\n oi"), "oi");
        assert_eq!(strip_thinking("só oi"), "só oi");
        let me = serenity::UserId::new(42);
        assert_eq!(strip_mention("<@42> fala tu", me), "fala tu");
        assert_eq!(strip_mention("ei <@!42>", me), "ei");
    }
}
