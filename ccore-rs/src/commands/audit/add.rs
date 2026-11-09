use std::time::Duration;

use chrono::Utc;
use poise::serenity_prelude as serenity;

use super::{defer_modal, edit_modal, reply};
use crate::{
    Context, Error,
    audit::{
        embeds::{Draft, field_value},
        model::{AuditEntry, PendingBatch, index, key},
        parse::parse_battle,
        store::Action,
    },
    embeds::{self, ERROR_COLOUR, fence_safe, truncate},
    staff,
};

/// Cola uma auditoria de batalha (nome k d a n) na fila pendente
#[poise::command(slash_command, rename = "audit-add")]
pub async fn audit_add(ctx: Context<'_>) -> Result<(), Error> {
    // O modal precisa ser a PRIMEIRA resposta da interacao: aqui nao ha defer.
    if let Some(denied) = staff::check(ctx).await {
        return reply(ctx, denied, true).await;
    }
    let poise::Context::Application(app) = ctx else {
        return Ok(());
    };

    let modal = serenity::CreateQuickModal::new("Nova auditoria de batalha")
        .timeout(Duration::from_secs(600))
        .field(
            serenity::CreateInputText::new(
                serenity::InputTextStyle::Paragraph,
                "Uma linha por jogador: nome k d a n",
                "",
            )
            .placeholder("RafaOdebrecht 12 3 5 0")
            .min_length(1)
            .max_length(4000),
        );
    let Some(response) = app
        .interaction
        .quick_modal(ctx.serenity_context(), modal)
        .await?
    else {
        return Ok(());
    };
    let modal = &response.interaction;
    defer_modal(ctx, modal).await?;

    let block = &response.inputs[0];
    let parsed = parse_battle(block);

    if parsed.entries.is_empty() {
        let mut empty = Draft::new(
            "Nada foi registrado",
            "Nenhuma linha válida foi encontrada. O formato esperado é `nome k d a n`, uma linha por jogador.",
            ERROR_COLOUR,
        );
        if !parsed.failures.is_empty() {
            empty = empty.field(
                "Linhas não reconhecidas",
                field_value(parsed.failures.iter().map(|f| f.describe(60))),
                false,
            );
        }
        return edit_modal(ctx, modal, empty.build()).await;
    }

    let batch = PendingBatch {
        batch_id: format!("{:x}", modal.id.get()),
        created_utc: Utc::now(),
        submitted_by_user_id: ctx.author().id.get(),
        submitted_by_username: ctx.author().name.clone(),
        entries: parsed
            .entries
            .iter()
            .map(|p| AuditEntry {
                username: p.username.clone(),
                kills: p.kills,
                deaths: p.deaths,
                assists: p.assists,
                battles: 1,
                rank: String::new(),
            })
            .collect(),
        raw_lines: block
            .lines()
            .map(str::trim)
            .filter(|l| !l.is_empty())
            .map(str::to_owned)
            .collect(),
    };

    // So o pendente muda; o audit.json espera o /audit-push.
    let stored = ctx
        .data()
        .audit
        .update(|audit, pending| {
            pending.batches.push(batch.clone());
            (index(&audit.entries), pending.batches.len())
        })
        .await;
    let (known, pending_count) = match stored {
        Ok(stored) => stored,
        Err(cause) => {
            return edit_modal(
                ctx,
                modal,
                embeds::error("Falha ao gravar a auditoria", cause),
            )
            .await;
        }
    };

    let (existing, new): (Vec<&str>, Vec<&str>) = batch
        .entries
        .iter()
        .map(|e| e.username.as_str())
        .partition(|name| known.contains_key(&key(name)));

    let preview = batch
        .entries
        .iter()
        .map(|e| {
            format!(
                "{:<18} {:<5} {:<5} {:<5}",
                truncate(&e.username, 18),
                e.kills,
                e.deaths,
                e.assists
            )
        })
        .collect::<Vec<_>>()
        .join("\n");
    let preview = if preview.chars().count() > 3500 {
        format!(
            "{}\n… (truncado)",
            preview.chars().take(3500).collect::<String>()
        )
    } else {
        preview
    };

    let has_problems =
        !parsed.failures.is_empty() || !parsed.warnings.is_empty() || !parsed.duplicates.is_empty();

    let mut embed = Draft::new(
        "Auditoria registrada na fila",
        format!(
            "```\n{:<18} {:<5} {:<5} {:<5}\n{}\n```",
            "Nome",
            "K",
            "D",
            "A",
            fence_safe(&preview)
        ),
        if has_problems {
            serenity::Colour::ORANGE
        } else {
            serenity::Colour::DARK_GREEN
        },
    )
    .field("Jogadores", batch.entries.len().to_string(), true)
    .field("Batalhas somadas", "+1 para cada jogador", true);

    if !new.is_empty() {
        embed = embed.field(
            format!("Novos jogadores ({})", new.len()),
            field_value(new.iter().take(30)),
            false,
        );
    }
    if !existing.is_empty() {
        embed = embed.field(
            format!("Já existentes ({})", existing.len()),
            field_value(existing.iter().take(30)),
            false,
        );
    }
    if !parsed.duplicates.is_empty() {
        embed = embed.field(
            "Repetidos no mesmo lote (K/D/A somados)",
            field_value(&parsed.duplicates),
            false,
        );
    }
    if !parsed.warnings.is_empty() {
        embed = embed.field(
            "Avisos",
            field_value(parsed.warnings.iter().take(10)),
            false,
        );
    }
    if !parsed.failures.is_empty() {
        embed = embed.field(
            format!("Linhas não reconhecidas ({})", parsed.failures.len()),
            field_value(parsed.failures.iter().take(10).map(|f| f.describe(50))),
            false,
        );
    }
    embed = embed.footer(format!(
        "Lote {} — {pending_count} lote(s) pendente(s). Use /audit-push para consolidar.",
        batch.batch_id
    ));

    let count = batch.entries.len();
    let names = batch
        .entries
        .iter()
        .take(15)
        .map(|e| e.username.as_str())
        .collect::<Vec<_>>()
        .join(", ");
    let more = if count > 15 {
        format!(" e mais {}", count - 15)
    } else {
        String::new()
    };
    ctx.data()
        .audit
        .record(
            ctx.author(),
            Action::Add,
            &format!(
                "Lote `{}` com {count} jogador(es): {names}{more}",
                batch.batch_id
            ),
        )
        .await;

    edit_modal(ctx, modal, embed.build()).await
}
