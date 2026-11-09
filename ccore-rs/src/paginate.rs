use std::time::Duration;

use poise::{CreateReply, serenity_prelude as serenity};

use crate::{Context, Error};

const IDLE_LIMIT: Duration = Duration::from_secs(180);

/// Mostra as paginas com setas. So quem rodou o comando troca de pagina: numa
/// resposta publica, o clique de outra pessoa ganha uma explicacao em vez do
/// "interacao falhou" do Discord.
pub async fn send(
    ctx: Context<'_>,
    pages: Vec<serenity::CreateEmbed>,
    ephemeral: bool,
) -> Result<(), Error> {
    let prefix = ctx.id().to_string();
    let prev_id = format!("{prefix}:prev");
    let next_id = format!("{prefix}:next");

    let buttons = |disabled: bool| {
        vec![serenity::CreateActionRow::Buttons(vec![
            serenity::CreateButton::new(&prev_id)
                .emoji('◀')
                .disabled(disabled),
            serenity::CreateButton::new(&next_id)
                .emoji('▶')
                .disabled(disabled),
        ])]
    };

    let reply = |page: usize, disabled: bool| {
        let mut reply = CreateReply::default()
            .embed(pages[page].clone())
            .ephemeral(ephemeral);
        if pages.len() > 1 {
            reply = reply.components(buttons(disabled));
        }
        reply
    };

    let handle = ctx.send(reply(0, false)).await?;
    if pages.len() == 1 {
        return Ok(());
    }

    let mut page = 0;
    while let Some(press) = serenity::collector::ComponentInteractionCollector::new(ctx)
        .filter({
            let prefix = prefix.clone();
            move |press| press.data.custom_id.starts_with(&prefix)
        })
        .timeout(IDLE_LIMIT)
        .await
    {
        if press.user.id != ctx.author().id {
            let _ = press
                .create_response(
                    ctx,
                    serenity::CreateInteractionResponse::Message(
                        serenity::CreateInteractionResponseMessage::new()
                            .content("Só quem rodou o comando pode trocar de página.")
                            .ephemeral(true),
                    ),
                )
                .await;
            continue;
        }

        page = if press.data.custom_id == next_id {
            (page + 1) % pages.len()
        } else {
            page.checked_sub(1).unwrap_or(pages.len() - 1)
        };

        press
            .create_response(
                ctx,
                serenity::CreateInteractionResponse::UpdateMessage(
                    serenity::CreateInteractionResponseMessage::new()
                        .embed(pages[page].clone())
                        .components(buttons(false)),
                ),
            )
            .await?;
    }

    // Se o token da interacao ja expirou nao ha o que fazer; o conteudo fica.
    let _ = handle.edit(ctx, reply(page, true)).await;
    Ok(())
}
