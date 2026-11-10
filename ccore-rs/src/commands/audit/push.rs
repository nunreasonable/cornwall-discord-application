use poise::serenity_prelude as serenity;

use super::reply;
use crate::{
    Context, Error,
    audit::{
        embeds::{Draft, field_value},
        publish::{Outcome, publish},
        store::Action,
    },
    embeds::{self, truncate},
    staff,
};

/// Publica a auditoria no GitHub, consolidando os lotes pendentes se houver
#[poise::command(slash_command, rename = "audit-push")]
pub async fn audit_push(
    ctx: Context<'_>,
    #[description = "Só mostra o que seria consolidado, sem tocar no GitHub"] dry_run: Option<bool>,
) -> Result<(), Error> {
    ctx.defer().await?;

    if let Some(denied) = staff::check(ctx).await {
        return reply(ctx, denied, false).await;
    }

    let outcome = match publish(ctx.data().into(), dry_run.unwrap_or(false)).await {
        Ok(outcome) => outcome,
        Err(failure) => {
            let embed = embeds::error(
                "Falha ao publicar a auditoria",
                format!("A operação falhou durante: **{}**.", failure.step),
            )
            .field("Motivo", truncate(&failure.error.to_string(), 1000), false)
            .field(
                "O que acontece agora",
                "O arquivo pendente **NÃO** foi limpo; corrija o problema e execute `/audit-push` novamente. \
                 Lotes já publicados não são contados duas vezes.",
                false,
            );
            return reply(ctx, embed, false).await;
        }
    };

    if outcome.nothing_to_do {
        let embed = serenity::CreateEmbed::new()
            .title("Nada a publicar")
            .description(
                "Não há lote pendente e o GitHub já está igual ao arquivo local. \
                 Use `/audit-add` para registrar uma batalha.",
            )
            .colour(serenity::Colour::ORANGE)
            .field("Total no efetivo", outcome.total_entries.to_string(), true);
        return reply(ctx, embed, false).await;
    }

    if !outcome.dry_run {
        ctx.data()
            .audit
            .record(ctx.author(), Action::Push, &outcome.log_details())
            .await;
    }

    reply(ctx, report(&outcome), false).await
}

fn report(outcome: &Outcome) -> serenity::CreateEmbed {
    let description = match (outcome.dry_run, outcome.has_pending) {
        (true, true) => "Nada foi gravado nem enviado ao GitHub. Rode sem `dry_run` para publicar.",
        (true, false) => {
            "Não há lote pendente. Rodar sem `dry_run` publica o estado atual do arquivo local \
             (mudanças de `/audit-import`, `/audit-edit` ou `/audit-setranks`), se o GitHub estiver desatualizado."
        }
        (false, true) => "Os lotes pendentes foram consolidados e enviados ao GitHub.",
        (false, false) => {
            "Não havia lote pendente: o estado atual do arquivo local foi enviado ao GitHub."
        }
    };
    let (title, colour) = if outcome.dry_run {
        (
            "Prévia da consolidação (dry run)",
            serenity::Colour::BLURPLE,
        )
    } else {
        ("Auditoria publicada", serenity::Colour::DARK_GREEN)
    };

    let report = &outcome.report;
    let mut embed = Draft::new(title, description, colour)
        .field(
            "Lotes consolidados",
            report.batches_applied.to_string(),
            true,
        )
        .field(
            "Jogadores atualizados",
            report.players_updated.to_string(),
            true,
        )
        .field("Batalhas somadas", report.battles_added.to_string(), true)
        .field("Total no efetivo", outcome.total_entries.to_string(), true);

    if report.batches_skipped > 0 {
        embed = embed.field(
            "Lotes já contabilizados (ignorados)",
            report.batches_skipped.to_string(),
            true,
        );
    }
    if !report.new_players.is_empty() {
        embed = embed.field(
            format!("Novos jogadores ({})", report.new_players.len()),
            field_value(report.new_players.iter().take(30)),
            false,
        );
    }
    if let Some(path) = &outcome.archive_path {
        embed = embed.field("Arquivamento", format!("`{path}`"), false);
    }

    let mut links = Vec::new();
    if let Some(url) = &outcome.audit_commit_url {
        links.push(format!("[auditoria consolidada]({url})"));
    }
    if let Some(url) = &outcome.archive_commit_url {
        links.push(format!("[lote arquivado]({url})"));
    }
    if !links.is_empty() {
        embed = embed.field("Commits", links.join(" · "), false);
    }

    if outcome.dry_run {
        embed = embed.footer(format!("{} lote(s) pendente(s)", outcome.pending_batches));
    }

    embed.build()
}
