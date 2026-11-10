use poise::CreateReply;

use crate::{Context, Error};

async fn say(ctx: Context<'_>, text: impl Into<String>) -> Result<(), Error> {
    ctx.send(CreateReply::default().content(text).ephemeral(true))
        .await?;
    Ok(())
}

/// Gera um código de login único para o dashboard administrativo.
#[poise::command(slash_command)]
pub async fn dashboardlink(ctx: Context<'_>) -> Result<(), Error> {
    let Some(dashboard) = ctx.data().dashboard.clone() else {
        return say(ctx, "❌ Dashboard indisponível no momento.").await;
    };

    // A consulta de nivel pode levar as tres tentativas: mais que os 3s que o
    // Discord da para a primeira resposta.
    ctx.defer_ephemeral().await?;

    // Recusa de cara quem nao teria nivel no painel. Se o Discord falhou, nao
    // da para afirmar que a pessoa NAO tem acesso: emite e o login decide.
    let level = dashboard
        .resolve_level(ctx.serenity_context(), ctx.author().id.get())
        .await;
    if !level.lookup_failed && level.level <= 0 {
        return say(ctx, "Você não tem acesso ao dashboard administrativo.").await;
    }

    let (code, expires_at) = dashboard.link_code(ctx.author()).await;
    say(
        ctx,
        format!(
            "Seu código de login: **{code}**\nExpira em: <t:{}:R>\nUse no site do dashboard.",
            expires_at.timestamp()
        ),
    )
    .await
}
