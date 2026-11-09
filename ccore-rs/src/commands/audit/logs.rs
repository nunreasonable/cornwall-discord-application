use poise::serenity_prelude as serenity;
use serenity::Mentionable;

use super::{reply, store_error};
use crate::{
    Context, Error,
    audit::{
        embeds::chunk,
        store::{Action, LogEntry},
    },
    embeds::truncate,
    paginate, staff,
};

/// Mostra quem mexeu na auditoria e o que foi feito
#[poise::command(slash_command, rename = "audit-logs")]
pub async fn audit_logs(
    ctx: Context<'_>,
    #[description = "Ver apenas as ações de uma pessoa"] usuario: Option<serenity::User>,
    #[description = "Ver apenas um tipo de ação"] acao: Option<Action>,
    #[description = "Mostrar apenas para você"] privado: Option<bool>,
) -> Result<(), Error> {
    let private = privado.unwrap_or(true);
    if private {
        ctx.defer_ephemeral().await?;
    } else {
        ctx.defer().await?;
    }

    if let Some(denied) = staff::check(ctx).await {
        return reply(ctx, denied, private).await;
    }

    let all = match ctx.data().audit.read_log().await {
        Ok(all) => all,
        Err(cause) => return reply(ctx, store_error(cause), private).await,
    };

    // Mais recente primeiro: e o que se quer ver ao abrir o historico.
    let mut filtered: Vec<&LogEntry> = all
        .iter()
        .filter(|e| usuario.as_ref().is_none_or(|u| e.user_id == u.id.get()))
        .filter(|e| acao.is_none_or(|a| e.action.eq_ignore_ascii_case(a.code())))
        .collect();
    filtered.sort_by_key(|e| std::cmp::Reverse(e.timestamp_utc));

    let scope = scope_text(usuario.as_ref(), acao, all.len(), filtered.len());

    if filtered.is_empty() {
        let description = if all.is_empty() {
            "Nenhuma ação registrada ainda. O histórico começa a ser preenchido no próximo `/audit-add`, `/audit-edit`, `/audit-setranks`, `/audit-import` ou `/audit-push`.".to_owned()
        } else {
            format!("Nenhuma ação corresponde ao filtro.\n{scope}")
        };
        let embed = serenity::CreateEmbed::new()
            .title("Histórico da auditoria")
            .description(description)
            .colour(serenity::Colour::ORANGE);
        return reply(ctx, embed, private).await;
    }

    // Texto normal, e nao bloco de codigo: o carimbo <t:> do Discord mostra a
    // hora no fuso de quem le.
    let lines = filtered.iter().map(|e| {
        format!(
            "<t:{}:f> — **{}** (<@{}>)\n`{}` {}\n",
            e.timestamp_utc.timestamp(),
            truncate(&e.username, 32),
            e.user_id,
            Action::label(&e.action),
            truncate(&e.details, 200)
        )
    });
    let chunks = chunk(lines, 3200);

    let pages = chunks
        .iter()
        .enumerate()
        .map(|(i, chunk)| {
            serenity::CreateEmbed::new()
                .title("Histórico da auditoria")
                .description(format!("{scope}\n\n{chunk}"))
                .colour(serenity::Colour::BLURPLE)
                .footer(serenity::CreateEmbedFooter::new(format!(
                    "Página {}/{} — mais recente primeiro",
                    i + 1,
                    chunks.len()
                )))
                .timestamp(serenity::Timestamp::now())
        })
        .collect();

    paginate::send(ctx, pages, private).await
}

fn scope_text(
    user: Option<&serenity::User>,
    action: Option<Action>,
    total: usize,
    shown: usize,
) -> String {
    let mut parts = Vec::new();
    if let Some(user) = user {
        parts.push(format!("pessoa: {}", user.mention()));
    }
    if let Some(action) = action {
        parts.push(format!("ação: `{}`", action.code()));
    }

    if parts.is_empty() {
        format!("{total} ação(ões) registrada(s).")
    } else {
        format!(
            "Filtro — {} · {shown} de {total} ação(ões).",
            parts.join(" · ")
        )
    }
}
