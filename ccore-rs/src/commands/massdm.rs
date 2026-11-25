use std::{
    sync::atomic::{AtomicBool, AtomicUsize, Ordering},
    time::{Duration, Instant},
};

use poise::{CreateReply, serenity_prelude as serenity};
use tracing::warn;

use crate::{
    Context, Error,
    dm::{self, Recipients},
    embeds::{self, truncate},
    reply, staff,
};

/// De quanto em quanto tempo o embed de andamento e atualizado.
const PROGRESS_EVERY: Duration = Duration::from_secs(10);

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
    let status = Status::start(ctx).await?;
    let Some(recipients) = prepare(ctx, &status, role.as_ref(), user.as_ref()).await? else {
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
    finish(ctx, &status, &recipients, &message).await
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
    let status = Status::start(ctx).await?;
    let Some(recipients) = prepare(ctx, &status, role.as_ref(), user.as_ref()).await? else {
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
    finish(ctx, &status, &recipients, &message).await
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
    status: &Status<'_>,
    role: Option<&serenity::Role>,
    user: Option<&serenity::User>,
) -> Result<Option<Recipients>, Error> {
    let Some(guild_id) = ctx.guild_id() else {
        status
            .show(embeds::error(
                "Erro",
                "Este comando só pode ser usado em servidores (guilds).",
            ))
            .await?;
        return Ok(None);
    };

    if let Some(denied) = staff::require_enlist_role(ctx).await {
        status.show(denied).await?;
        return Ok(None);
    }

    match dm::resolve(ctx.serenity_context(), guild_id, role, user).await {
        Ok(recipients) => Ok(Some(recipients)),
        Err(cause) => {
            status
                .show(embeds::error("Envio não realizado", cause))
                .await?;
            Ok(None)
        }
    }
}

/// Manda as DMs com o embed de andamento sendo trocado a cada poucos
/// segundos, e no fim troca pelo relatorio.
async fn finish(
    ctx: Context<'_>,
    status: &Status<'_>,
    recipients: &Recipients,
    message: &serenity::CreateMessage,
) -> Result<(), Error> {
    let total = recipients.members.len();
    let started = Instant::now();
    let (sent, failed) = (AtomicUsize::new(0), AtomicUsize::new(0));
    // Pelo comando nao ha cancelamento; so o job do dashboard tem.
    let never = AtomicBool::new(false);
    status
        .show(dm::progress_embed(&recipients.name, total, 0, 0))
        .await?;

    let send = dm::send_tracked(
        ctx.serenity_context(),
        &ctx.data().dm_limiter,
        &recipients.members,
        message,
        &never,
        |s, f| {
            sent.store(s, Ordering::Relaxed);
            failed.store(f, Ordering::Relaxed);
        },
    );
    // O andamento roda junto e e largado quando o envio termina.
    let ticker = async {
        loop {
            tokio::time::sleep(PROGRESS_EVERY).await;
            let embed = dm::progress_embed(
                &recipients.name,
                total,
                sent.load(Ordering::Relaxed),
                failed.load(Ordering::Relaxed),
            );
            if let Err(cause) = status.show(embed).await {
                warn!("dm: nao atualizei o andamento: {cause}");
            }
        }
    };
    let result = tokio::select! {
        result = send => result,
        () = ticker => unreachable!("o andamento nao termina sozinho"),
    };

    status
        .show(dm::report_embed(
            &recipients.name,
            &result,
            started.elapsed(),
        ))
        .await
}

/// A mensagem do comando, editada do "preparando" ate o relatorio.
///
/// O token da interacao vive 15 minutos e 500 DMs levam ~35: depois do
/// primeiro envio a mensagem e editada pelo canal, que nao expira. O token so
/// entra quando o bot nao pode editar ali, e o reply::safe quando nem ele vale.
struct Status<'a> {
    ctx: Context<'a>,
    handle: poise::ReplyHandle<'a>,
    message: Option<(serenity::ChannelId, serenity::MessageId)>,
}

impl<'a> Status<'a> {
    async fn start(ctx: Context<'a>) -> Result<Self, Error> {
        let preparing = serenity::CreateEmbed::new()
            .title("Preparando o envio…")
            .description("Conferindo permissão e listando os destinatários.")
            .colour(serenity::Colour::BLURPLE);
        let handle = ctx
            .send(
                CreateReply::default()
                    .embed(preparing)
                    .allowed_mentions(serenity::CreateAllowedMentions::new()),
            )
            .await?;
        let message = handle.message().await.ok().map(|m| (m.channel_id, m.id));
        Ok(Self {
            ctx,
            handle,
            message,
        })
    }

    async fn show(&self, embed: serenity::CreateEmbed) -> Result<(), Error> {
        if let Some((channel, id)) = self.message {
            let edit = serenity::EditMessage::new().embed(embed.clone());
            match channel.edit_message(self.ctx, id, edit).await {
                Ok(_) => return Ok(()),
                Err(cause) => warn!("dm: nao editei pelo canal, tentando pelo token: {cause}"),
            }
        }
        let edit = CreateReply::default().embed(embed.clone());
        if self.handle.edit(self.ctx, edit).await.is_ok() {
            return Ok(());
        }
        reply::safe(self.ctx, embed).await
    }
}
