use poise::{CreateReply, serenity_prelude as serenity};

use super::utility::block;
use crate::{Context, Error, embeds, staff};

/// Liga ou desliga a IA local. So operadores (llm.operatorUserIds).
#[poise::command(prefix_command, hide_in_help)]
pub async fn ia(ctx: Context<'_>, action: Option<String>) -> Result<(), Error> {
    let Some(llm) = ctx.data().llm.as_ref() else {
        ctx.say("A IA local não está configurada neste bot.")
            .await?;
        return Ok(());
    };
    if !llm.is_operator(ctx.author().id.get()) {
        ctx.say("Só os operadores da IA podem ligar ou desligar ela por comando.")
            .await?;
        return Ok(());
    }

    let reply = match action.as_deref().map(str::to_lowercase).as_deref() {
        Some("on") => match llm.turn_on(&ctx.author().name).await {
            Ok(()) => {
                "IA ligada. O modelo está carregando; use `/llm-status` para ver quando fica pronto."
            }
            Err(cause) => {
                tracing::warn!("ia local: nao consegui ligar: {cause}");
                "Não consegui subir o modelo. Veja o `/llm-status` e o log do bot."
            }
        },
        Some("off") => match llm
            .turn_off(&format!("-ia off de {}", ctx.author().name))
            .await
        {
            Ok(()) => "IA desligada. A placa e a RAM foram liberadas.",
            Err(cause) => {
                tracing::warn!("ia local: nao consegui parar o servico: {cause}");
                "A IA parou de responder, mas o serviço do modelo não parou. Veja o log do bot."
            }
        },
        _ => "Uso: `-ia on` ou `-ia off`.",
    };
    ctx.say(reply).await?;
    Ok(())
}

/// Mostra o estado da IA local: ligada, servico, modelo, memoria e uso
#[poise::command(slash_command, rename = "llm-status")]
pub async fn llm_status(ctx: Context<'_>) -> Result<(), Error> {
    ctx.defer_ephemeral().await?;
    if let Some(denied) = staff::check(ctx).await {
        ctx.send(CreateReply::default().embed(denied).ephemeral(true))
            .await?;
        return Ok(());
    }
    let Some(llm) = ctx.data().llm.as_ref() else {
        let embed = embeds::error(
            "IA local não configurada",
            "Falta a seção `llm` no config.jsonc.",
        );
        ctx.send(CreateReply::default().embed(embed).ephemeral(true))
            .await?;
        return Ok(());
    };

    let health = llm.health().await;
    let (title, colour) = match (health.enabled, health.server_ready, health.service_active) {
        (true, true, _) => (
            "IA local ligada e respondendo",
            serenity::Colour::DARK_GREEN,
        ),
        (true, false, true) => (
            "IA local ligada, carregando o modelo",
            serenity::Colour::GOLD,
        ),
        (true, false, false) => (
            "IA local ligada, mas o modelo não está rodando",
            serenity::Colour::RED,
        ),
        (false, _, true) => (
            "IA local desligada, mas o modelo ainda está rodando",
            serenity::Colour::ORANGE,
        ),
        (false, _, false) => ("IA local desligada", serenity::Colour::LIGHT_GREY),
    };
    let yes_no = |b: bool| if b { "sim" } else { "não" }.to_owned();
    let state = vec![
        ("Respondendo", yes_no(health.enabled)),
        ("Serviço ativo", yes_no(health.service_active)),
        ("Servidor pronto", yes_no(health.server_ready)),
        (
            "Modelo",
            health.model.clone().unwrap_or_else(|| "—".to_owned()),
        ),
    ];

    let embed = serenity::CreateEmbed::new()
        .title(title)
        .colour(colour)
        .field("Estado", block(&state), false)
        .field("Memória", block(&llm.memory_lines().await), false)
        .field("Uso", block(&llm.usage_lines()), false)
        .footer(serenity::CreateEmbedFooter::new(
            "Liga com -ia on (operadores).",
        ))
        .timestamp(serenity::Timestamp::now());
    ctx.send(CreateReply::default().embed(embed).ephemeral(true))
        .await?;
    Ok(())
}
