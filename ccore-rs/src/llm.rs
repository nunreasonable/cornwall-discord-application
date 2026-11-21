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

use crate::{
    abuse::{self, Abuse, Verdict},
    blacklist::Blacklist,
    config::LlmConfig,
    embeds::truncate,
};

/// O modelo leva um tempo para carregar; antes disso o servidor responde 503.
const REQUEST_TIMEOUT: Duration = Duration::from_secs(120);
const PROBE_TIMEOUT: Duration = Duration::from_secs(3);
/// Resposta do Discord tem teto de 2000; sobra margem para o aviso de corte.
const MAX_REPLY: usize = 1900;
/// Conversa com a Sol esquecida aberta fecha sozinha depois disto.
const SOL_IDLE: Duration = Duration::from_secs(30 * 60);

#[derive(Default)]
struct State {
    enabled_by: Option<String>,
    enabled_at: Option<Instant>,
    last_used: Option<Instant>,
    answered: u64,
    failures: u64,
    last_latency: Option<Duration>,
    cooldowns: HashMap<u64, Instant>,
    abuse: HashMap<u64, Abuse>,
    /// Conversas com a Sol abertas, por (operador, canal), com a ultima mensagem.
    sol_sessions: HashMap<(u64, u64), Instant>,
    /// Ultimo id_task visto no /slots. O modelo e dividido com o Sollarety:
    /// se o numero andou, alguem usou, mesmo que nao tenha sido este bot.
    server_tasks: Option<i64>,
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
    pub fn wants(&self, ctx: &serenity::Context, msg: &serenity::Message) -> bool {
        if !self.is_enabled() || msg.guild_id.is_none() {
            return false;
        }
        let channels = &self.config.channel_ids;
        if !channels.is_empty() && !channels.contains(&msg.channel_id.get()) {
            return false;
        }
        let author = msg.author.id.get();
        if self.is_operator(author)
            && (sol_cue(&msg.content).is_some() || self.in_sol(author, msg.channel_id.get()))
        {
            return true;
        }
        self.addressed(ctx, msg)
    }

    /// O operador esta conversando com a Sol neste canal: toda mensagem dele
    /// ali e para ela, mesmo sem mencionar o bot.
    fn in_sol(&self, author: u64, channel: u64) -> bool {
        lock(&self.state)
            .sol_sessions
            .get(&(author, channel))
            .is_some_and(|last| last.elapsed() < SOL_IDLE)
    }

    /// Abre, fecha ou mantem a conversa com a Sol. true se esta mensagem e
    /// para ela (o "bye sol" ainda e, para ela se despedir).
    fn sol_turn(&self, author: u64, channel: u64, text: &str) -> bool {
        let key = (author, channel);
        let mut state = lock(&self.state);
        match sol_cue(text) {
            Some(true) => {
                state.sol_sessions.insert(key, Instant::now());
                true
            }
            Some(false) => state.sol_sessions.remove(&key).is_some(),
            None => match state.sol_sessions.get_mut(&key) {
                Some(last) if last.elapsed() < SOL_IDLE => {
                    *last = Instant::now();
                    true
                }
                Some(_) => {
                    state.sol_sessions.remove(&key);
                    false
                }
                None => false,
            },
        }
    }

    /// Persona da Sol, lida a cada vez dos arquivos do config.
    async fn sol_prompt(&self) -> Option<String> {
        let mut parts = Vec::new();
        for path in &self.config.sol_prompt_files {
            match tokio::fs::read_to_string(path).await {
                Ok(text) if !text.trim().is_empty() => parts.push(text.trim().to_owned()),
                Ok(_) => {}
                Err(cause) => warn!("ia local: persona da Sol em {path}: {cause}"),
            }
        }
        (!parts.is_empty()).then(|| parts.join("\n\n"))
    }

    /// Falaram com o bot: mencao a ele (ou ao cargo dele, que o Discord
    /// oferece no autocompletar) ou reply numa mensagem dele.
    pub fn addressed(&self, ctx: &serenity::Context, msg: &serenity::Message) -> bool {
        let me = ctx.cache.current_user().id;
        if msg.mentions_user_id(me) {
            return true;
        }
        let bot_role = msg.guild_id.is_some_and(|guild| {
            ctx.cache.guild(guild).is_some_and(|g| {
                msg.mention_roles.iter().any(|id| {
                    g.roles
                        .get(id)
                        .is_some_and(|role| role.tags.bot_id == Some(me))
                })
            })
        });
        if bot_role {
            return true;
        }
        msg.referenced_message
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
            let text = strip_mention(&msg.content, ctx.cache.current_user().id);
            let verdict = lock(&self.state)
                .abuse
                .entry(author)
                .or_default()
                .judge(&text, Instant::now());
            if verdict == Verdict::Ignore {
                return;
            }
            if let Some(text) = abuse::telling_off(&verdict, is_english(&text)) {
                send_reply(ctx, msg, text.to_owned()).await;
                return;
            }

            let mut state = lock(&self.state);
            let wait = Duration::from_secs(self.config.cooldown_seconds);
            if let Some(last) = state.cooldowns.get(&author)
                && last.elapsed() < wait
            {
                return;
            }
            state.cooldowns.insert(author, Instant::now());
        }

        let sol =
            self.is_operator(author) && self.sol_turn(author, msg.channel_id.get(), &msg.content);
        let system_prompt = if sol {
            match self.sol_prompt().await {
                Some(prompt) => prompt,
                None => {
                    send_reply(
                        ctx,
                        msg,
                        "Não achei a persona da Sol nesta máquina.".to_owned(),
                    )
                    .await;
                    return;
                }
            }
        } else {
            self.config.system_prompt.clone()
        };

        let reply_text = match self.complete(ctx, msg, &system_prompt).await {
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
        // Ordem perigosa de verdade: o modelo so devolve a marca.
        let reply_text = if reply_text.contains(abuse::MARKER) {
            if !self.is_operator(author) {
                lock(&self.state)
                    .abuse
                    .entry(author)
                    .or_default()
                    .mute(Instant::now());
            }
            let english = is_english(&strip_mention(&msg.content, ctx.cache.current_user().id));
            abuse::telling_off(&Verdict::Order, english)
                .unwrap_or_default()
                .to_owned()
        } else {
            reply_text
        };
        let reply_text = match blacklist {
            Some(list) if list.blocks(&reply_text) => "Não vou repetir isso.".to_owned(),
            _ => reply_text,
        };

        send_reply(ctx, msg, reply_text).await;
    }

    async fn complete(
        &self,
        ctx: &serenity::Context,
        msg: &serenity::Message,
        system_prompt: &str,
    ) -> Result<String, Failure> {
        let me = ctx.cache.current_user().id;
        let _typing = msg.channel_id.start_typing(&ctx.http);

        // O idioma e decidido aqui, nao pelo modelo: com o prompt em portugues
        // ele puxava para o portugues e misturava os dois.
        let language = if is_english(&msg.content) {
            "Reply entirely in English, using only English slang (lol, ngl, fr, bruh...). \
             Do not use any Portuguese words or Brazilian slang like kkk, slk or mano."
        } else {
            "Responda inteiramente em português do Brasil, sem misturar palavras em inglês."
        };
        let system = format!("{system_prompt}\n\n{language}");
        let mut messages = vec![json!({ "role": "system", "content": system })];
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

    /// Maior id_task entre os slots do servidor: cresce a cada pedido, venha
    /// de qual bot vier.
    async fn server_tasks(&self) -> Option<i64> {
        let base = self.config.base_url.trim_end_matches('/');
        let slots: Vec<Slot> = self
            .http
            .get(format!("{base}/slots"))
            .timeout(PROBE_TIMEOUT)
            .send()
            .await
            .ok()?
            .json()
            .await
            .ok()?;
        slots.iter().filter_map(|s| s.id_task).max().or(Some(-1))
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
                if let Some(tasks) = llm.server_tasks().await {
                    let mut state = lock(&llm.state);
                    if state.server_tasks.is_some_and(|seen| seen != tasks) {
                        state.last_used = Some(Instant::now());
                    }
                    state.server_tasks = Some(tasks);
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

#[derive(Deserialize)]
struct Slot {
    /// None enquanto o slot nao atendeu nenhum pedido.
    #[serde(default)]
    id_task: Option<i64>,
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

/// Ninguem e pingado pela IA: nem cargo, nem @everyone, nem usuario.
async fn send_reply(ctx: &serenity::Context, msg: &serenity::Message, text: String) {
    let message = serenity::CreateMessage::new()
        .content(text)
        .reference_message(msg)
        .allowed_mentions(serenity::CreateAllowedMentions::new().replied_user(true));
    if let Err(cause) = msg.channel_id.send_message(ctx, message).await {
        warn!("ia local: falha ao responder: {cause}");
    }
}

fn strip_mention(text: &str, me: serenity::UserId) -> String {
    text.replace(&format!("<@{me}>"), "")
        .replace(&format!("<@!{me}>"), "")
        .trim()
        .to_owned()
}

/// A mensagem tem ingles (mesmo misturado com portugues)? Conta palavras que
/// so aparecem num dos dois idiomas.
fn is_english(text: &str) -> bool {
    const EN: &[&str] = &[
        "the", "you", "your", "ur", "u", "is", "are", "was", "were", "what", "whats", "that",
        "this", "it", "its", "i", "im", "dont", "can", "cant", "how", "why", "who", "where",
        "when", "with", "and", "of", "to", "my", "me", "do", "does", "did", "have", "has", "be",
        "lol", "lmao", "bro", "bruh", "ngl", "fr", "yo", "wtf", "idk", "kinda", "gonna", "wanna",
        "dude", "man", "hey", "hi", "hello", "yes", "yeah", "nah", "no", "not", "really", "insane",
        "good", "bad", "game", "play",
    ];
    const PT: &[&str] = &[
        "o", "os", "as", "que", "nao", "não", "tu", "voce", "você", "vc", "mano", "eu", "ele",
        "ela", "de", "do", "da", "pra", "com", "um", "uma", "isso", "ta", "tá", "slk", "hj", "mlk",
        "tmj", "pdp", "crl", "krl", "porra", "caralho", "foi", "jogo", "ontem", "hoje", "sim",
        "aqui", "agora", "muito", "tbm", "kkk", "kkkk", "kkkkk",
    ];
    let lower = text.to_lowercase().replace(['\'', '’'], "");
    let words: Vec<&str> = lower
        .split(|c: char| !c.is_alphanumeric())
        .filter(|w| !w.is_empty())
        .collect();
    let en = words.iter().filter(|w| EN.contains(w)).count();
    let pt = words
        .iter()
        .filter(|w| PT.contains(w) || w.starts_with("kkk"))
        .count();
    en > 0 && (en >= 2 || pt == 0)
}

/// "hey sol" abre a conversa com a Sol e "bye sol" fecha, no comeco da
/// mensagem. Numeros sao pulados: a mencao ao bot vira "<@123>".
fn sol_cue(text: &str) -> Option<bool> {
    let lower = text.to_lowercase();
    let words: Vec<&str> = lower
        .split(|c: char| !c.is_alphanumeric())
        .filter(|w| !w.is_empty() && !w.chars().all(|c| c.is_ascii_digit()))
        .take(2)
        .collect();
    match words.as_slice() {
        ["hey", "sol"] => Some(true),
        ["bye", "sol"] => Some(false),
        _ => None,
    }
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
    fn decide_o_idioma() {
        assert!(is_english("yo ccore ngl ur kinda mid fr fr 💀"));
        assert!(is_english("bro tu viu o jogo ontem? that was insane lmao"));
        assert!(is_english("what's a deployment?"));
        assert!(!is_english("slk mano, ganhamo os crl a 4 kkkkk"));
        assert!(!is_english("tu é mó feio kkkkk"));
        assert!(!is_english("alguém sabe que horas começa o treino hoje?"));
    }

    #[test]
    fn reconhece_hey_sol_e_bye_sol() {
        assert_eq!(sol_cue("Hey sol"), Some(true));
        assert_eq!(
            sol_cue("<@1482173872848507062> hey, sol! tudo bem?"),
            Some(true)
        );
        assert_eq!(sol_cue("BYE SOL"), Some(false));
        assert_eq!(sol_cue("hey solange"), None);
        assert_eq!(sol_cue("o sol tá forte hoje"), None);
    }

    #[test]
    fn tira_o_raciocinio_e_a_mencao() {
        assert_eq!(strip_thinking("<think>hmm</think>\n oi"), "oi");
        assert_eq!(strip_thinking("só oi"), "só oi");
        let me = serenity::UserId::new(42);
        assert_eq!(strip_mention("<@42> fala tu", me), "fala tu");
        assert_eq!(strip_mention("ei <@!42>", me), "ei");
    }
}
