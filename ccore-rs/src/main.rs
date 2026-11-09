mod commands;
mod config;

use std::time::Duration;

use config::Config;
use poise::serenity_prelude as serenity;
use tracing::{error, info, warn};
use tracing_subscriber::EnvFilter;

/// Estado compartilhado pelos comandos. Vazio por enquanto.
pub struct Data;

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
    tracing_subscriber::fmt()
        .with_env_filter(EnvFilter::try_from_default_env().unwrap_or_else(|_| "info".into()))
        .init();

    let config = Config::load()?;
    let guild_ids = config.guild_ids.clone();

    let framework = poise::Framework::builder()
        .options(poise::FrameworkOptions {
            commands: commands::all(),
            prefix_options: poise::PrefixFrameworkOptions {
                prefix: Some(config.prefix.clone().into()),
                mention_as_prefix: true,
                ..Default::default()
            },
            on_error: |err| Box::pin(on_error(err)),
            ..Default::default()
        })
        .setup(move |ctx, ready, framework| {
            Box::pin(async move {
                info!("conectado como {}", ready.user.name);

                if guild_ids.is_empty() {
                    warn!("guildIds vazio: nenhum comando foi registrado");
                }
                for id in guild_ids {
                    let guild = serenity::GuildId::new(id);
                    poise::builtins::register_in_guild(ctx, &framework.options().commands, guild)
                        .await?;
                    info!("comandos registrados no servidor {id}");
                }

                Ok(Data)
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
