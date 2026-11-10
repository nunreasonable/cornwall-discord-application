mod audit;
mod blacklist;
mod commands;
mod config;
mod dashboard;
mod deployment;
mod dm;
mod embeds;
mod enlistment;
mod events;
mod http;
mod log_buffer;
mod paginate;
mod random;
mod reply;
mod repost;
mod sheet;
mod staff;
mod status;
mod terminal;

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
    /// Arc porque o dashboard le e grava os mesmos arquivos, pelo mesmo lock.
    pub audit: Arc<audit::store::AuditStore>,
    /// Token do GitHub ja descoberto. Esvazia quando o GitHub responde 401.
    pub github_token: Arc<tokio::sync::Mutex<Option<String>>>,
    pub messages: Option<Arc<repost::MessageStore>>,
    pub blacklist: Option<Arc<blacklist::Blacklist>>,
    /// None quando o dashboard nao subiu: o bot segue sem ele.
    pub dashboard: Option<Arc<dashboard::Dashboard>>,
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
    if std::env::args().any(|arg| arg == "--terminal") {
        return terminal::attach().await;
    }
    status::mark_started();

    // Primeira coisa do main: o que for logado antes disto nao entra no buffer.
    let logs = Arc::new(LogBuffer::default());
    tracing_subscriber::registry()
        .with(EnvFilter::try_from_default_env().unwrap_or_else(|_| "info".into()))
        .with(tracing_subscriber::fmt::layer())
        .with(BufferLayer::new(logs.clone()))
        .init();

    let config = Arc::new(Config::load()?);
    let data_config = config.clone();
    // O setup sobe o servidor; o desligamento, la embaixo, precisa para-lo.
    let dashboard_server: Arc<Mutex<Option<dashboard::Server>>> = Arc::default();
    let setup_server = dashboard_server.clone();

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
                    // Um servidor sem acesso nao pode derrubar o setup: os
                    // comandos e o dashboard dos outros dependem dele.
                    match poise::builtins::register_in_guild(
                        ctx,
                        &framework.options().commands,
                        guild,
                    )
                    .await
                    {
                        Ok(()) => info!("comandos registrados no servidor {id}"),
                        Err(cause) => warn!("nao registrei os comandos no servidor {id}: {cause}"),
                    }
                }

                // Tudo e por servidor. O bot em C# registrava o /repost como
                // global, e ele ficaria duplicado ao lado do de servidor.
                if let Err(cause) = serenity::Command::set_global_commands(ctx, vec![]).await {
                    warn!("nao limpei os comandos globais: {cause}");
                }

                terminal::start(ctx.http.clone());

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

                let http = http::client()?;
                let audit = Arc::new(audit::store::AuditStore::new("data"));
                let github_token = Arc::new(tokio::sync::Mutex::default());

                // O dashboard e acessorio; o bot nao e. Porta ocupada ou config
                // ruim vira log, e o bot segue.
                let dashboard = match dashboard::start(dashboard::Deps {
                    ctx: ctx.clone(),
                    shards: framework.shard_manager().clone(),
                    config: data_config.clone(),
                    logs: logs.clone(),
                    http: http.clone(),
                    dm_limiter: dm_limiter.clone(),
                    audit: audit.clone(),
                    github_token: github_token.clone(),
                })
                .await
                {
                    Ok((dashboard, server)) => {
                        *setup_server.lock().unwrap_or_else(|e| e.into_inner()) = Some(server);
                        Some(dashboard)
                    }
                    Err(cause) => {
                        error!("dashboard nao subiu, o bot segue sem ele: {cause}");
                        None
                    }
                };

                Ok(Data {
                    config: data_config,
                    logs,
                    http,
                    enlist_attempts: Mutex::default(),
                    dm_limiter,
                    audit,
                    github_token,
                    messages,
                    blacklist,
                    dashboard,
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
        terminal::cleanup();

        // Primeiro o dashboard, com teto proprio: ele nao pode segurar a
        // desconexao do gateway.
        let server = dashboard_server
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .take();
        if let Some(server) = server {
            server.stop().await;
        }

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
