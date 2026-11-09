use poise::serenity_prelude as serenity;
use tracing::warn;

use crate::{Data, Error};

/// O Serenity ja roda cada evento numa task propria: o trabalho com REST aqui
/// nao segura o gateway, entao a fila que o bot em C# precisava sobra.
pub async fn handle(
    ctx: &serenity::Context,
    event: &serenity::FullEvent,
    data: &Data,
) -> Result<(), Error> {
    let serenity::FullEvent::Message { new_message: msg } = event else {
        return Ok(());
    };
    if msg.author.bot || msg.author.system {
        return Ok(());
    }

    if let Some(store) = &data.messages {
        store.store(msg);
    }

    if msg
        .content
        .trim()
        .eq_ignore_ascii_case("oi voce quer ir pra LLL")
        && let Err(cause) = msg.reply(ctx, "sai maluco todo dia isso si fude").await
    {
        warn!("falha ao responder mensagem: {cause}");
    }

    if let Some(blacklist) = &data.blacklist {
        blacklist.handle(ctx, msg).await;
    }

    Ok(())
}
