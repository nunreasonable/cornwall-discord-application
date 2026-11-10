use std::{
    collections::VecDeque,
    sync::{Arc, Mutex},
    time::{Duration, Instant},
};

use poise::serenity_prelude as serenity;
use tracing::{info, warn};

use crate::{config::RepostConfig, random};

const MAX_STORED: usize = 1000;
const CLEANUP_EVERY: Duration = Duration::from_secs(3600);

/// So o que o repost usa. Fica so em memoria: reiniciar o bot zera a fila.
#[derive(Clone)]
struct Stored {
    content: String,
    author: String,
    at: Instant,
    attachments: Vec<String>,
}

impl Stored {
    fn is_repostable(&self) -> bool {
        !self.content.trim().is_empty() || !self.attachments.is_empty()
    }
}

pub enum RepostError {
    NotEnough(usize),
    NothingRepostable,
    Send(serenity::Error),
}

/// Guarda as mensagens recentes de um canal e, de tempos em tempos, reenvia
/// uma delas ao acaso.
pub struct MessageStore {
    channel: serenity::ChannelId,
    interval: Duration,
    retention: Duration,
    minimum: usize,
    messages: Mutex<VecDeque<Stored>>,
    /// Depois de um repost, o automatico espera um intervalo inteiro.
    next_allowed: Mutex<Instant>,
}

impl MessageStore {
    pub fn new(config: &RepostConfig) -> Option<Self> {
        if !config.enabled || config.target_channel_id == 0 {
            return None;
        }

        Some(Self {
            channel: serenity::ChannelId::new(config.target_channel_id),
            // Piso de 1 minuto: 0 no config faria o sorteio do intervalo quebrar.
            interval: Duration::from_secs(60 * config.repost_interval_minutes.max(1)),
            retention: Duration::from_secs(3600 * config.message_retention_hours),
            minimum: config.minimum_messages_for_repost,
            messages: Mutex::default(),
            next_allowed: Mutex::new(Instant::now()),
        })
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, VecDeque<Stored>> {
        self.messages.lock().unwrap_or_else(|e| e.into_inner())
    }

    pub fn store(&self, msg: &serenity::Message) {
        if msg.channel_id != self.channel {
            return;
        }

        let mut messages = self.lock();
        messages.push_back(Stored {
            content: msg.content.clone(),
            author: msg.author.name.clone(),
            at: Instant::now(),
            attachments: msg.attachments.iter().map(|a| a.url.clone()).collect(),
        });
        while messages.len() > MAX_STORED {
            messages.pop_front();
        }
    }

    pub fn count(&self) -> usize {
        self.lock().len()
    }

    pub fn minimum(&self) -> usize {
        self.minimum
    }

    /// Descarta da memoria o que passou da retencao. Nao apaga nada do Discord.
    /// As mais antigas estao sempre na frente.
    fn prune(&self) {
        let mut messages = self.lock();
        let before = messages.len();
        while messages
            .front()
            .is_some_and(|m| m.at.elapsed() >= self.retention)
        {
            messages.pop_front();
        }
        info!(
            "limpeza do repost: {} mensagem(ns) antiga(s) removida(s), {} na fila",
            before - messages.len(),
            messages.len()
        );
    }

    /// Reenvia uma mensagem ao acaso, exatamente como foi escrita.
    pub async fn repost(&self, http: &serenity::Http) -> Result<(), RepostError> {
        let picked = {
            let messages = self.lock();
            if messages.len() < self.minimum {
                return Err(RepostError::NotEnough(messages.len()));
            }
            let candidates: Vec<&Stored> = messages.iter().filter(|m| m.is_repostable()).collect();
            if candidates.is_empty() {
                return Err(RepostError::NothingRepostable);
            }
            let i = random::below(candidates.len() as u64) as usize;
            candidates[i].clone()
        };

        // Mensagem de quem tem Nitro pode passar dos 2000 que o bot envia.
        let content = if picked.content.chars().count() > 2000 {
            format!(
                "{}...",
                picked.content.chars().take(1997).collect::<String>()
            )
        } else {
            picked.content.clone()
        };

        // Sem mencoes: o texto volta tal e qual, e um @everyone ou cargo da
        // mensagem original seria pingado de novo.
        let send = |text: String| {
            serenity::CreateMessage::new()
                .content(text)
                .allowed_mentions(serenity::CreateAllowedMentions::new())
        };

        if !content.trim().is_empty() {
            self.channel
                .send_message(http, send(content))
                .await
                .map_err(RepostError::Send)?;
        }

        // O link do CDN faz o Discord mostrar a imagem de novo.
        let mut seen = Vec::new();
        for url in picked.attachments {
            if seen.contains(&url) {
                continue;
            }
            seen.push(url.clone());
            self.channel
                .send_message(http, send(url))
                .await
                .map_err(RepostError::Send)?;
        }

        info!("mensagem de {} repostada", picked.author);
        *self.next_allowed.lock().unwrap_or_else(|e| e.into_inner()) =
            Instant::now() + self.interval;
        Ok(())
    }

    /// Duas tarefas separadas: juntas num select, a limpeza de hora em hora
    /// reiniciaria a espera do repost, que pode ser maior que uma hora.
    pub fn spawn(self: &Arc<Self>, http: Arc<serenity::Http>) {
        let store = self.clone();
        tokio::spawn(async move {
            loop {
                // Entre o intervalo e o dobro dele: um periodo exato deixaria
                // obvio que e um bot.
                let extra = random::below(store.interval.as_secs() + 1);
                tokio::time::sleep(store.interval + Duration::from_secs(extra)).await;
                store.auto_repost(&http).await;
            }
        });

        let store = self.clone();
        tokio::spawn(async move {
            loop {
                tokio::time::sleep(CLEANUP_EVERY).await;
                store.prune();
            }
        });
    }

    async fn auto_repost(&self, http: &serenity::Http) {
        let next = *self.next_allowed.lock().unwrap_or_else(|e| e.into_inner());
        if Instant::now() < next {
            info!("repost automatico em espera depois de um repost recente");
            return;
        }

        match self.repost(http).await {
            Ok(()) => {}
            Err(RepostError::NotEnough(count)) => {
                info!(
                    "repost automatico: poucas mensagens ({count}/{})",
                    self.minimum
                );
            }
            Err(RepostError::NothingRepostable) => {
                info!("repost automatico: nenhuma mensagem com texto ou anexo");
            }
            Err(RepostError::Send(cause)) => warn!("repost automatico falhou: {cause}"),
        }
    }
}
