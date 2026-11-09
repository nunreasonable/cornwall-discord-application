use poise::{CreateReply, serenity_prelude as serenity};
use serenity::Mentionable;
use tracing::warn;

use crate::{Context, Error};

/// O token da interacao vive 15 minutos, e um envio para 500 pessoas passa
/// disso. Se a resposta falhar, ela vai para o canal marcando quem rodou o
/// comando, em vez de sumir.
pub async fn safe(ctx: Context<'_>, embed: serenity::CreateEmbed) -> Result<(), Error> {
    if let Err(cause) = ctx.send(CreateReply::default().embed(embed.clone())).await {
        warn!("resposta da interacao falhou, indo para o canal: {cause}");
        let message = serenity::CreateMessage::new()
            .content(ctx.author().mention().to_string())
            .embed(embed);
        ctx.channel_id().send_message(ctx, message).await?;
    }
    Ok(())
}
