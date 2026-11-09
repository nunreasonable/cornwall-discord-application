use crate::{Context, Error};

/// Responde com sua latência em ms
#[poise::command(slash_command)]
pub async fn ping(ctx: Context<'_>) -> Result<(), Error> {
    let latency = ctx.ping().await.as_millis();
    ctx.say(format!("Pong! Latência: {latency}ms")).await?;
    Ok(())
}
