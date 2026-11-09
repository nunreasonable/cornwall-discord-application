use poise::{CreateReply, serenity_prelude as serenity};

use crate::{Context, Error, repost::RepostError, staff};

async fn say(ctx: Context<'_>, text: impl Into<String>) -> Result<(), Error> {
    ctx.send(CreateReply::default().content(text).ephemeral(true))
        .await?;
    Ok(())
}

/// Reposts a random stored message
#[poise::command(slash_command)]
pub async fn repost(ctx: Context<'_>) -> Result<(), Error> {
    // Sem o portao, qualquer membro disparava um repost no canal configurado.
    if let Some(denied) = staff::check(ctx).await {
        ctx.send(CreateReply::default().embed(denied).ephemeral(true))
            .await?;
        return Ok(());
    }

    let Some(store) = &ctx.data().messages else {
        return say(ctx, "❌ Message reposting service is not available.").await;
    };
    if store.count() == 0 {
        return say(ctx, "📭 No messages are available to repost.").await;
    }

    ctx.defer_ephemeral().await?;

    let text = match store.repost(&ctx.serenity_context().http).await {
        Ok(()) => {
            "✅ Message reposted successfully! The automatic reposting service is now on cooldown."
                .to_owned()
        }
        Err(RepostError::NotEnough(count)) => format!(
            "📭 Cannot repost message. Need at least {} messages stored, but only have {count}.",
            store.minimum()
        ),
        Err(RepostError::NothingRepostable) => {
            "📭 No stored message has text or attachments to repost.".to_owned()
        }
        Err(RepostError::Send(cause)) => {
            tracing::warn!("repost manual falhou: {cause}");
            "❌ An error occurred while reposting the message.".to_owned()
        }
    };
    say(ctx, text).await
}

/// Check message storage status and statistics
#[poise::command(slash_command)]
pub async fn messages(ctx: Context<'_>) -> Result<(), Error> {
    // Mostra estado interno do bot e leva ao /repost: so para o staff.
    if let Some(denied) = staff::check(ctx).await {
        ctx.send(CreateReply::default().embed(denied).ephemeral(true))
            .await?;
        return Ok(());
    }

    let Some(store) = &ctx.data().messages else {
        return say(ctx, "❌ Message reposting service is not available.").await;
    };

    let count = store.count();
    let minimum = store.minimum();
    let ready = count >= minimum;
    let percent = if ready {
        100
    } else {
        count * 100 / minimum.max(1)
    };
    let bar = format!(
        "{}{}",
        "█".repeat(percent / 10),
        "░".repeat(10 - percent / 10)
    );

    let info = if ready {
        "✅ Ready to repost messages! Use `/repost` to manually trigger a repost.".to_owned()
    } else {
        format!(
            "⏳ Need {} more messages before reposting is available.",
            minimum - count
        )
    };

    let embed = serenity::CreateEmbed::new()
        .title("📊 Message Storage Status")
        .colour(if ready {
            serenity::Colour::DARK_GREEN
        } else {
            serenity::Colour::ORANGE
        })
        .timestamp(serenity::Timestamp::now())
        .field("📝 Stored Messages", count.to_string(), true)
        .field("⚠️ Minimum Required", minimum.to_string(), true)
        .field("✅ Can Repost", if ready { "Yes" } else { "No" }, true)
        .field("📈 Progress", format!("`{bar}` {percent}%"), false)
        .field("ℹ️ Information", info, false);

    ctx.send(CreateReply::default().embed(embed).ephemeral(true))
        .await?;
    Ok(())
}
