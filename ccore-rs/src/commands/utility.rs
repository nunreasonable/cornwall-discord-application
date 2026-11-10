use poise::{CreateReply, serenity_prelude as serenity};

use crate::{Context, Error, random, staff, status};

/// Alinha "rotulo  valor" num bloco monoespacado.
fn block(rows: &[(&str, String)]) -> String {
    if rows.is_empty() {
        return "```\n(indisponível)\n```".to_owned();
    }
    let width = rows
        .iter()
        .map(|(l, _)| l.chars().count())
        .max()
        .unwrap_or(0);
    let lines: Vec<String> = rows
        .iter()
        .map(|(label, value)| format!("{label:<width$}  {value}"))
        .collect();
    format!("```\n{}\n```", lines.join("\n"))
}

/// Mostra informações do sistema, do processo e da conexão com o Discord.
#[poise::command(prefix_command)]
pub async fn osinfo(ctx: Context<'_>) -> Result<(), Error> {
    // Expoe dados da maquina: so para o staff.
    if let Some(denied) = staff::check(ctx).await {
        ctx.send(CreateReply::default().embed(denied).reply(true))
            .await?;
        return Ok(());
    }

    let shard = ctx.serenity_context().shard_id;
    let latency = status::latency(ctx.framework().shard_manager, shard).await;
    let discord = status::discord_lines(ctx.cache(), latency);
    let avatar = ctx.cache().current_user().face();

    let embed = serenity::CreateEmbed::new()
        .title("Informações do bot")
        .colour(serenity::Colour::BLURPLE)
        .field("Sistema", block(&status::system_lines().await), false)
        .field("Processo", block(&status::process_lines()), false)
        .field("Discord", block(&discord), false)
        .thumbnail(avatar)
        .timestamp(serenity::Timestamp::now());

    ctx.send(CreateReply::default().embed(embed).reply(true))
        .await?;
    Ok(())
}

#[poise::command(prefix_command, hide_in_help)]
pub async fn vsfdliliane(ctx: Context<'_>) -> Result<(), Error> {
    const ALLOWED_USER: u64 = 1402344358199689348;
    const TARGET: &str = "<@1072212634201505952>";

    if ctx.author().id.get() != ALLOWED_USER {
        ctx.send(
            CreateReply::default()
                .content("Você não tem permissão para usar este comando.")
                .reply(true),
        )
        .await?;
        return Ok(());
    }

    let responses = [
        format!("Vai se fuder {TARGET}"),
        format!("Vai tomar no cu,{TARGET} porra"),
        format!("Caralho, {TARGET} burrão hein."),
        format!("Vai pra casa do chapéu, {TARGET} fdppppp!!!!1!!1."),
    ];
    let pick = random::below(responses.len() as u64) as usize;

    ctx.send(
        CreateReply::default()
            .content(responses[pick].clone())
            .reply(true),
    )
    .await?;
    Ok(())
}
