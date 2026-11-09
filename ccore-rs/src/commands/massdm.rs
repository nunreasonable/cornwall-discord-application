use poise::{CreateReply, serenity_prelude as serenity};

use crate::{
    Context, Error,
    dm::{self, Recipients},
    embeds::{self, truncate},
    reply, staff,
};

const FALLBACK_GAME_LINK: &str = "https://example.com";
/// Teto do Discord para o valor de um field.
const MAX_FIELD: usize = 1024;

/// Envia uma DM para um usuário ou para todos os membros de um cargo (sem código/link).
#[poise::command(slash_command)]
pub async fn dmreminder(
    ctx: Context<'_>,
    #[description = "Cargo para enviar a mensagem (opcional)"] role: Option<serenity::Role>,
    #[description = "Usuário para enviar a mensagem (opcional)"] user: Option<serenity::User>,
    #[description = "Mensagem personalizada a ser enviada"] message: Option<String>,
) -> Result<(), Error> {
    ctx.defer().await?;

    let Some(recipients) = prepare(ctx, role.as_ref(), user.as_ref()).await? else {
        return Ok(());
    };

    // Texto no content para links terem preview; o embed so leva o titulo
    // para nao repetir o texto.
    let body = message.unwrap_or_default();
    let content = if body.trim().is_empty() {
        "(nenhuma mensagem fornecida)".to_owned()
    } else {
        truncate(&body, 2000)
    };

    let embed = serenity::CreateEmbed::new()
        .title(format!("Mensagem para {}", recipients.name))
        .colour(serenity::Colour::BLURPLE)
        .timestamp(serenity::Timestamp::now());

    let message = serenity::CreateMessage::new().content(content).embed(embed);
    finish(ctx, &recipients, &message).await
}

/// Envia uma DM para um usuário ou para todos os membros de um cargo.
#[poise::command(slash_command)]
pub async fn dmdeployment(
    ctx: Context<'_>,
    #[description = "Cargo para enviar a mensagem (opcional)"] role: Option<serenity::Role>,
    #[description = "Usuário para enviar a mensagem (opcional)"] user: Option<serenity::User>,
    #[description = "Código a ser incluído na mensagem (pode ser um código do jogo)"] code: Option<
        String,
    >,
    #[description = "Mensagem personalizada a ser enviada (pode conter instruções)"]
    message: Option<String>,
) -> Result<(), Error> {
    ctx.defer().await?;

    let Some(recipients) = prepare(ctx, role.as_ref(), user.as_ref()).await? else {
        return Ok(());
    };

    // Link so no content, para o Discord mostrar o preview.
    let link = ctx
        .data()
        .config
        .default_game_link
        .as_deref()
        .filter(|link| !link.trim().is_empty())
        .unwrap_or(FALLBACK_GAME_LINK);

    let embed = deployment_dm(
        &recipients.name,
        code.as_deref().unwrap_or_default(),
        message.as_deref().unwrap_or_default(),
    );

    let message = serenity::CreateMessage::new().content(link).embed(embed);
    finish(ctx, &recipients, &message).await
}

/// Embed da DM de deployment. Os campos sao cortados em 1024: texto maior faz
/// o Discord recusar TODAS as DMs do envio.
pub fn deployment_dm(target: &str, code: &str, body: &str) -> serenity::CreateEmbed {
    let or = |text: &str, empty: &str| {
        if text.trim().is_empty() {
            empty.to_owned()
        } else {
            truncate(text, MAX_FIELD)
        }
    };

    serenity::CreateEmbed::new()
        .title(format!("Mensagem para {target}"))
        .colour(serenity::Colour::BLURPLE)
        .field("Código", or(code, "(nenhum)"), true)
        .field("Mensagem", or(body, "(nenhuma mensagem extra)"), false)
}

/// Checa servidor e cargo e resolve quem recebe. None quando ja respondeu com
/// o motivo da recusa.
async fn prepare(
    ctx: Context<'_>,
    role: Option<&serenity::Role>,
    user: Option<&serenity::User>,
) -> Result<Option<Recipients>, Error> {
    let Some(guild_id) = ctx.guild_id() else {
        fail(
            ctx,
            embeds::error(
                "Erro",
                "Este comando só pode ser usado em servidores (guilds).",
            ),
        )
        .await?;
        return Ok(None);
    };

    if let Some(denied) = staff::require_enlist_role(ctx).await {
        fail(ctx, denied).await?;
        return Ok(None);
    }

    match dm::resolve(ctx.serenity_context(), guild_id, role, user).await {
        Ok(recipients) => Ok(Some(recipients)),
        Err(cause) => {
            fail(ctx, embeds::error("Envio não realizado", cause)).await?;
            Ok(None)
        }
    }
}

async fn finish(
    ctx: Context<'_>,
    recipients: &Recipients,
    message: &serenity::CreateMessage,
) -> Result<(), Error> {
    let result = dm::send_all(
        ctx.serenity_context(),
        &ctx.data().dm_limiter,
        &recipients.members,
        message,
    )
    .await;

    let (title, colour) = if result.aborted.is_some() {
        ("Envio interrompido", serenity::Colour::ORANGE)
    } else {
        ("Envio finalizado", serenity::Colour::DARK_GREEN)
    };

    let mut summary = serenity::CreateEmbed::new()
        .title(title)
        .colour(colour)
        .field("Alvo", &recipients.name, true)
        .field("Total de destinatários", result.total.to_string(), true)
        .field("Mensagens enviadas", result.sent.to_string(), true)
        .field("Falhas", result.failed.to_string(), true)
        .timestamp(serenity::Timestamp::now());

    let text: Vec<String> = result
        .aborted
        .clone()
        .into_iter()
        .chain(dm::describe_failures(&result))
        .collect();
    if !text.is_empty() {
        summary = summary.description(truncate(&text.join("\n\n"), 4000));
    }

    reply::safe(ctx, summary).await
}

async fn fail(ctx: Context<'_>, embed: serenity::CreateEmbed) -> Result<(), Error> {
    ctx.send(CreateReply::default().embed(embed)).await?;
    Ok(())
}
