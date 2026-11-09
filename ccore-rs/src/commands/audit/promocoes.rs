use std::collections::HashMap;

use poise::serenity_prelude as serenity;

use super::{reply, store_error};
use crate::{
    Context, Error,
    audit::{
        embeds::field_value,
        ladder::{self, Evaluation, Rank, Status},
        model::{AuditFile, key, same_name},
    },
    embeds::{self, truncate},
    paginate, staff,
};

const LINES_PER_PAGE: usize = 20;

/// Emoji de cada patente, pelo nome em minusculas. Por nome, e nao por ID no
/// codigo: sobrevive a um emoji recriado, e sem ele a linha so perde o icone.
type Emojis = HashMap<String, String>;

/// Calcula quem está elegível a promoção segundo a escada do regimento
#[poise::command(slash_command)]
pub async fn promocoes(
    ctx: Context<'_>,
    #[description = "Ver apenas um jogador"]
    #[autocomplete = "super::autocomplete_player"]
    jogador: Option<String>,
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

    let audit = match ctx.data().audit.read_audit().await {
        Ok(audit) => audit,
        Err(cause) => return reply(ctx, store_error(cause), private).await,
    };
    if audit.entries.is_empty() {
        let embed = serenity::CreateEmbed::new()
            .title("Auditoria vazia")
            .description("Nenhum jogador registrado ainda. Use `/audit-add` primeiro.")
            .colour(serenity::Colour::ORANGE);
        return reply(ctx, embed, private).await;
    }

    let emojis: Emojis = ctx
        .guild()
        .map(|guild| {
            guild
                .emojis
                .values()
                .map(|e| (key(&e.name), format!("{e} ")))
                .collect()
        })
        .unwrap_or_default();

    if let Some(name) = jogador.as_deref().map(str::trim).filter(|n| !n.is_empty()) {
        return reply(ctx, single_player(&audit, &emojis, name), private).await;
    }

    paginate::send(ctx, roster_pages(&audit, &emojis), private).await
}

fn emoji<'a>(emojis: &'a Emojis, rank: Option<&Rank>) -> &'a str {
    rank.and_then(|r| emojis.get(&key(r.emoji)))
        .map_or("", String::as_str)
}

fn rank_label(emojis: &Emojis, rank: Option<&Rank>, fallback: &str) -> String {
    match rank {
        Some(rank) => format!("{}**{}**", emoji(emojis, Some(rank)), rank.name),
        None if fallback.is_empty() => "*sem cargo*".to_owned(),
        None => fallback.to_owned(),
    }
}

fn colour(status: Status) -> serenity::Colour {
    match status {
        Status::Eligible => serenity::Colour::new(0x00FF7F),
        Status::EligibleNeedsApproval => serenity::Colour::new(0xFFD700),
        Status::OutsideLadder => serenity::Colour::new(0x808080),
        Status::BelowFirstRank => serenity::Colour::ORANGE,
        Status::UpToDate => serenity::Colour::BLURPLE,
    }
}

fn steps_note(e: &Evaluation<'_>) -> String {
    if e.steps > 1 {
        format!(" *(salta {} degraus)*", e.steps)
    } else {
        String::new()
    }
}

fn single_player(audit: &AuditFile, emojis: &Emojis, name: &str) -> serenity::CreateEmbed {
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

    let e = ladder::evaluate(entry);
    let target = rank_label(emojis, e.target, "");

    let description = match e.status {
        Status::OutsideLadder => {
            "Patente de NCO/oficial: a progressão não é calculada por contagem de eventos."
                .to_owned()
        }
        Status::BelowFirstRank => "Ainda não alcançou o primeiro degrau da escada.".to_owned(),
        Status::Eligible => format!(
            "✅ Elegível a {target}{}\nO critério é apenas numérico e já está cumprido.",
            steps_note(&e)
        ),
        Status::EligibleNeedsApproval => format!(
            "🟡 Elegível a {target}{} — **requer aprovação**\n\
             A contagem de eventos está cumprida, mas esta patente também depende de avaliação do comando.",
            steps_note(&e)
        ),
        Status::UpToDate => "Está no degrau que a contagem de eventos justifica.".to_owned(),
    };

    let mut embed = serenity::CreateEmbed::new()
        .title(&entry.username)
        .description(description)
        .colour(colour(e.status))
        .timestamp(audit.timestamp())
        .field(
            "Patente atual",
            rank_label(emojis, e.current, e.current_name),
            true,
        )
        .field("Eventos", entry.battles.to_string(), true);

    if let Some(target) = e.target {
        embed = embed.field("Critério", target.criteria, false);
    }

    if e.status != Status::OutsideLadder {
        let next = match e.next {
            Some(next) => format!(
                "{} — faltam **{}** evento(s)\n*{}*",
                rank_label(emojis, Some(next), ""),
                e.to_next,
                next.criteria
            ),
            None => format!(
                "Chegou ao topo da parte calculável da escada. {}**Lance Serjeant** é concedida manualmente.",
                emoji(emojis, ladder::find("Lance Serjeant"))
            ),
        };
        embed = embed.field("Próximo degrau", next, false);
    }

    embed
}

fn roster_pages(audit: &AuditFile, emojis: &Emojis) -> Vec<serenity::CreateEmbed> {
    let mut results: Vec<Evaluation<'_>> = audit
        .entries
        .iter()
        .map(ladder::evaluate)
        .filter(Evaluation::is_promotable)
        .collect();

    let total = audit.entries.len();
    if results.is_empty() {
        return vec![
            serenity::CreateEmbed::new()
                .title("Nenhuma promoção pendente")
                .description(format!(
                    "Os {total} jogadores da auditoria estão no degrau que a contagem de eventos justifica."
                ))
                .colour(colour(Status::Eligible)),
        ];
    }

    // Alvo mais alto primeiro e, no mesmo alvo, quem tem mais eventos.
    results.sort_by_cached_key(|r| {
        let required = r.target.and_then(|t| t.required).unwrap_or_default();
        (
            std::cmp::Reverse(required),
            std::cmp::Reverse(r.battles),
            key(r.username),
        )
    });

    let promotable = results.len();
    let blocks = [
        ("✅ Critério numérico cumprido", Status::Eligible),
        (
            "🟡 Elegíveis — requerem aprovação do comando",
            Status::EligibleNeedsApproval,
        ),
    ];

    let mut pages: Vec<Vec<String>> = Vec::new();
    let mut lines = Vec::new();

    for (header, status) in blocks {
        let items: Vec<&Evaluation<'_>> = results.iter().filter(|r| r.status == status).collect();
        if items.is_empty() {
            continue;
        }

        lines.push(format!("__{header}__"));
        for (i, r) in items.iter().enumerate() {
            lines.push(line(emojis, r));

            let used = i + 1;
            if used % LINES_PER_PAGE == 0 && used < items.len() {
                pages.push(std::mem::take(&mut lines));
                // Repete o cabecalho para nao perder qual bloco esta listado.
                lines.push(format!("__{header} (continuação)__"));
            }
        }
        lines.push(String::new());
    }
    if !lines.is_empty() {
        pages.push(lines);
    }

    pages
        .iter()
        .enumerate()
        .map(|(i, lines)| {
            serenity::CreateEmbed::new()
                .title("Promoções pendentes")
                .description(lines.join("\n"))
                .colour(colour(Status::EligibleNeedsApproval))
                .footer(serenity::CreateEmbedFooter::new(format!(
                    "{promotable} de {total} jogadores · página {}",
                    i + 1
                )))
                .timestamp(serenity::Timestamp::now())
        })
        .collect()
}

fn line(emojis: &Emojis, r: &Evaluation<'_>) -> String {
    let current = match r.current {
        Some(rank) => rank.name.to_owned(),
        None if r.current_name.is_empty() => "*sem cargo*".to_owned(),
        None => truncate(r.current_name, 20),
    };
    let jump = if r.steps > 1 {
        format!(" *(+{})*", r.steps)
    } else {
        String::new()
    };
    let target = r.target.map_or("?", |t| t.name);

    format!(
        "**{}** · {} ev. · {current} → {}{target}{jump}",
        truncate(r.username, 24),
        r.battles,
        emoji(emojis, r.target)
    )
}
