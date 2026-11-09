mod audit;
mod blacklist;
mod commands;
mod config;
mod deployment;
mod dm;
mod embeds;
mod enlistment;
mod events;
mod http;
mod log_buffer;
mod paginate;
mod reply;
mod repost;
mod sheet;
mod staff;

use std::{
    collections::HashMap,
    sync::{Arc, Mutex},
    time::{Duration, Instant},
};

use config::Config;
use log_buffer::{BufferLayer, LogBuffer};
use poise::serenity_prelude as serenity;
use tracing::{error, info, warn};
use tracing_subscriber::{EnvFilter, layer::SubscriberExt, util::SubscriberInitExt};

/// Estado compartilhado pelos comandos.
pub struct Data {
    pub config: Arc<Config>,
    pub logs: Arc<LogBuffer>,
    pub http: reqwest::Client,
    /// Ultima tentativa de /alistar-se de cada pessoa.
    pub enlist_attempts: Mutex<HashMap<u64, Instant>>,
    pub dm_limiter: Arc<dm::DmLimiter>,
    pub audit: audit::store::AuditStore,
    /// Token do GitHub ja descoberto. Esvazia quando o GitHub responde 401.
    pub github_token: tokio::sync::Mutex<Option<String>>,
    pub messages: Option<Arc<repost::MessageStore>>,
    pub blacklist: Option<Arc<blacklist::Blacklist>>,
}

pub type Error = Box<dyn std::error::Error + Send + Sync>;
pub type Context<'a> = poise::Context<'a, Data, Error>;

/// Os mesmos intents do bot em C#.
const INTENTS: serenity::GatewayIntents = serenity::GatewayIntents::GUILDS
    .union(serenity::GatewayIntents::GUILD_MEMBERS)
    .union(serenity::GatewayIntents::GUILD_MESSAGES)
    .union(serenity::GatewayIntents::DIRECT_MESSAGES)
    .union(serenity::GatewayIntents::MESSAGE_CONTENT);

const SHUTDOWN_LIMIT: Duration = Duration::from_secs(5);

#[tokio::main]
async fn main() -> Result<(), Error> {
    // Primeira coisa do main: o que for logado antes disto nao entra no buffer.
    let logs = Arc::new(LogBuffer::default());
    tracing_subscriber::registry()
        .with(EnvFilter::try_from_default_env().unwrap_or_else(|_| "info".into()))
        .with(tracing_subscriber::fmt::layer())
        .with(BufferLayer::new(logs.clone()))
        .init();

    let config = Arc::new(Config::load()?);
    let data_config = config.clone();

    let framework = poise::Framework::builder()
        .options(poise::FrameworkOptions {
            commands: commands::all(),
            prefix_options: poise::PrefixFrameworkOptions {
                prefix: Some(config.prefix.clone().into()),
                mention_as_prefix: true,
                ..Default::default()
            },
            on_error: |err| Box::pin(on_error(err)),
            event_handler: |framework, event| {
                Box::pin(events::handle(
                    framework.serenity_context,
                    event,
                    framework.user_data,
                ))
            },
            ..Default::default()
        })
        .setup(move |ctx, ready, framework| {
            Box::pin(async move {
                info!("conectado como {}", ready.user.name);

                if data_config.guild_ids.is_empty() {
                    warn!("guildIds vazio: nenhum comando foi registrado");
                }
                for &id in &data_config.guild_ids {
                    let guild = serenity::GuildId::new(id);
                    poise::builtins::register_in_guild(ctx, &framework.options().commands, guild)
                        .await?;
                    info!("comandos registrados no servidor {id}");
                }

                let dm_limiter = Arc::new(dm::DmLimiter::default());

                let messages = data_config
                    .message_reposting
                    .as_ref()
                    .and_then(repost::MessageStore::new)
                    .map(Arc::new);
                if let Some(store) = &messages {
                    store.spawn(ctx.http.clone());
                    info!("repost de mensagens ligado");
                }

                let blacklist = data_config
                    .message_blacklist
                    .as_ref()
                    .and_then(|c| blacklist::Blacklist::new(c, dm_limiter.clone()))
                    .map(Arc::new);
                if blacklist.is_some() {
                    info!("blacklist de mensagens ligada");
                }

                Ok(Data {
                    config: data_config,
                    logs,
                    http: http::client()?,
                    enlist_attempts: Mutex::default(),
                    dm_limiter,
                    audit: audit::store::AuditStore::new("data"),
                    github_token: tokio::sync::Mutex::default(),
                    messages,
                    blacklist,
                })
            })
        })
        .build();

    let mut client = serenity::ClientBuilder::new(&config.token, INTENTS)
        .framework(framework)
        .await?;

    let shard_manager = client.shard_manager.clone();
    tokio::spawn(async move {
        wait_for_signal().await;
        info!("encerrando");

        // Com prazo: um socket meio aberto nao pode prender o processo ate o
        // SIGKILL do systemd.
        if tokio::time::timeout(SHUTDOWN_LIMIT, shard_manager.shutdown_all())
            .await
            .is_err()
        {
            warn!("o gateway nao fechou em {SHUTDOWN_LIMIT:?}, saindo mesmo assim");
            std::process::exit(0);
        }
    });

    client.start().await?;
    Ok(())
}

async fn wait_for_signal() {
    use tokio::signal::unix::{SignalKind, signal};

    let mut sigint = signal(SignalKind::interrupt()).expect("handler de SIGINT");
    let mut sigterm = signal(SignalKind::terminate()).expect("handler de SIGTERM");

    tokio::select! {
        _ = sigint.recv() => {}
        _ = sigterm.recv() => {}
    }
}

async fn on_error(err: poise::FrameworkError<'_, Data, Error>) {
    if let poise::FrameworkError::Command {
        error: cause, ctx, ..
    } = err
    {
        error!("/{} falhou: {cause}", ctx.command().name);
        let _ = ctx.say("Algo deu errado ao rodar esse comando.").await;
        return;
    }

    if let Err(cause) = poise::builtins::on_error(err).await {
        error!("falha ao tratar erro: {cause}");
    }
}
