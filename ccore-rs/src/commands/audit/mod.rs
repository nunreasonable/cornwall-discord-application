use poise::{CreateReply, serenity_prelude as serenity};
use tracing::warn;

use crate::{Context, Data, Error, audit::model, embeds, staff};

mod add;
mod check;
mod edit;
mod import;
mod logs;
mod push;
mod setranks;

pub fn all() -> Vec<poise::Command<Data, Error>> {
    vec![
        add::audit_add(),
        check::audit_check(),
        edit::audit_edit(),
        import::audit_import(),
        logs::audit_logs(),
        push::audit_push(),
        setranks::audit_setranks(),
    ]
}

/// Sugere jogadores ja registrados: escolher alguem nao depende de paginar,
/// por maior que fique o efetivo.
async fn autocomplete_player(
    ctx: Context<'_>,
    partial: &str,
) -> serenity::CreateAutocompleteResponse {
    let none = serenity::CreateAutocompleteResponse::new;
    // O efetivo so aparece para o staff, como o resto da auditoria.
    if staff::check(ctx).await.is_some() {
        return none();
    }

    let audit = match ctx.data().audit.read_audit().await {
        Ok(audit) => audit,
        Err(cause) => {
            warn!("autocomplete da auditoria falhou: {cause}");
            return none();
        }
    };

    let choices = model::search(&audit.entries, partial, 25)
        .into_iter()
        .map(|e| {
            let label = format!(
                "{} ({}/{}/{} — {} bat.)",
                e.username, e.kills, e.deaths, e.assists, e.battles
            );
            serenity::AutocompleteChoice::new(embeds::truncate(&label, 100), e.username.clone())
        })
        .collect();
    none().set_choices(choices)
}

async fn reply(
    ctx: Context<'_>,
    embed: serenity::CreateEmbed,
    ephemeral: bool,
) -> Result<(), Error> {
    ctx.send(CreateReply::default().embed(embed).ephemeral(ephemeral))
        .await?;
    Ok(())
}

fn store_error(cause: String) -> serenity::CreateEmbed {
    embeds::error("Falha ao ler a auditoria", cause)
}

/// Adia a resposta do modal: o resto pode passar dos 3s do Discord.
async fn defer_modal(ctx: Context<'_>, modal: &serenity::ModalInteraction) -> Result<(), Error> {
    modal
        .create_response(
            ctx,
            serenity::CreateInteractionResponse::Defer(
                serenity::CreateInteractionResponseMessage::new().ephemeral(true),
            ),
        )
        .await?;
    Ok(())
}

async fn edit_modal(
    ctx: Context<'_>,
    modal: &serenity::ModalInteraction,
    embed: serenity::CreateEmbed,
) -> Result<(), Error> {
    modal
        .edit_response(ctx, serenity::EditInteractionResponse::new().embed(embed))
        .await?;
    Ok(())
}
