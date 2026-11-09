use std::cmp::Ordering;

use poise::serenity_prelude as serenity;

use super::{reply, store_error};
use crate::{
    Context, Error,
    audit::{
        embeds::{audit_pages, field_value},
        model::{AuditEntry, AuditFile, PendingFile, key, same_name},
    },
    embeds::{self, truncate},
    paginate, staff,
};

#[derive(Clone, Copy, PartialEq, poise::ChoiceParameter)]
pub enum SortBy {
    #[name = "Nome"]
    Name,
    #[name = "Batalhas"]
    Battles,
    #[name = "Kills"]
    Kills,
    #[name = "K/D"]
    Kd,
}

/// Mostra o conteúdo do arquivo de auditoria
#[poise::command(slash_command, rename = "audit-check")]
pub async fn audit_check(
    ctx: Context<'_>,
    #[description = "Ver apenas um jogador"]
    #[autocomplete = "super::autocomplete_player"]
    jogador: Option<String>,
    #[description = "Como ordenar a lista"] ordenar: Option<SortBy>,
    #[description = "Mostrar apenas para você"] privado: Option<bool>,
) -> Result<(), Error> {
    let private = privado.unwrap_or(false);
    if private {
        ctx.defer_ephemeral().await?;
    } else {
        ctx.defer().await?;
    }

    if let Some(denied) = staff::check(ctx).await {
        return reply(ctx, denied, private).await;
    }

    let (audit, pending) = match ctx.data().audit.read_both().await {
        Ok(files) => files,
        Err(cause) => return reply(ctx, store_error(cause), private).await,
    };

    if let Some(name) = jogador.as_deref().map(str::trim).filter(|n| !n.is_empty()) {
        return reply(ctx, single_player(&audit, &pending, name), private).await;
    }

    let mut rows: Vec<&AuditEntry> = audit.entries.iter().collect();
    sort(&mut rows, ordenar.unwrap_or(SortBy::Battles));

    let note = if pending.batches.is_empty() {
        "Todos os lotes estão consolidados.".to_owned()
    } else {
        format!(
            "⚠️ {} lote(s) ainda não consolidado(s) — use `/audit-push`.",
            pending.batches.len()
        )
    };

    paginate::send(ctx, audit_pages(&audit, &rows, &note), private).await
}

fn single_player(audit: &AuditFile, pending: &PendingFile, name: &str) -> serenity::CreateEmbed {
    let Some(entry) = audit.entries.iter().find(|e| same_name(&e.username, name)) else {
        let wanted = key(name);
        let suggestions: Vec<&str> = audit
            .entries
            .iter()
            .filter(|e| key(&e.username).contains(&wanted))
            .map(|e| e.username.as_str())
            .take(10)
            .collect();

        let mut embed = embeds::error(
            "Jogador não encontrado",
            format!("**{}** não está na auditoria.", truncate(name, 60)),
        );
        if !suggestions.is_empty() {
            embed = embed.field("Você quis dizer", field_value(suggestions), false);
        }
        return embed;
    };

    let rank = if entry.rank.trim().is_empty() {
        "*sem cargo*"
    } else {
        &entry.rank
    };

    let mut embed = serenity::CreateEmbed::new()
        .title(&entry.username)
        .colour(serenity::Colour::BLURPLE)
        .timestamp(audit.timestamp())
        .field("Cargo", rank, false)
        .field("Kills", entry.kills.to_string(), true)
        .field("Deaths", entry.deaths.to_string(), true)
        .field("Assists", entry.assists.to_string(), true)
        .field("Batalhas", entry.battles.to_string(), true)
        .field("K/D", entry.kd_ratio(), true);

    // Quantas batalhas dele ainda estao na fila.
    let pending_battles = pending
        .batches
        .iter()
        .filter(|b| {
            b.entries
                .iter()
                .any(|e| same_name(&e.username, &entry.username))
        })
        .count();
    if pending_battles > 0 {
        embed = embed.field(
            "Pendente",
            format!("{pending_battles} batalha(s) ainda não consolidada(s)."),
            false,
        );
    }
    embed
}

fn sort(rows: &mut [&AuditEntry], by: SortBy) {
    rows.sort_by(|a, b| {
        let first = match by {
            SortBy::Name => Ordering::Equal,
            SortBy::Battles => b.battles.cmp(&a.battles),
            SortBy::Kills => b.kills.cmp(&a.kills),
            SortBy::Kd => b.kd().total_cmp(&a.kd()),
        };
        first.then_with(|| key(&a.username).cmp(&key(&b.username)))
    });
}
