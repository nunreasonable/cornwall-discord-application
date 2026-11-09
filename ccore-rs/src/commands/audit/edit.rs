use std::time::Duration;

use poise::{CreateReply, serenity_prelude as serenity};

use super::{defer_modal, edit_modal, reply, store_error};
use crate::{
    Context, Error,
    audit::{
        embeds::{entry_block, field_value},
        model::{AuditEntry, PendingBatch, key, same_name},
        store::Action,
    },
    embeds::{self, truncate},
    staff,
};

#[derive(Clone, Copy, PartialEq, poise::ChoiceParameter)]
pub enum Scope {
    #[name = "Auditoria consolidada"]
    Audit,
    #[name = "Lotes pendentes"]
    Pending,
}

impl Scope {
    fn code(self) -> &'static str {
        match self {
            Self::Audit => "audit",
            Self::Pending => "pending",
        }
    }

    fn label(self) -> &'static str {
        match self {
            Self::Audit => "auditoria",
            Self::Pending => "pendente",
        }
    }
}

/// Edita ou remove um jogador da auditoria
#[poise::command(slash_command, rename = "audit-edit")]
pub async fn audit_edit(
    ctx: Context<'_>,
    #[description = "Nome do jogador na auditoria"]
    #[autocomplete = "super::autocomplete_player"]
    jogador: String,
    #[description = "Onde editar"] escopo: Option<Scope>,
    #[description = "Remover o jogador em vez de editar"] remover: Option<bool>,
) -> Result<(), Error> {
    // Sem defer: a edicao responde com um modal, e o modal precisa ser a
    // primeira resposta.
    if let Some(denied) = staff::check(ctx).await {
        return reply(ctx, denied, true).await;
    }

    let scope = escopo.unwrap_or(Scope::Audit);
    let (audit, pending) = match ctx.data().audit.read_both().await {
        Ok(files) => files,
        Err(cause) => return reply(ctx, store_error(cause), true).await,
    };

    let pool: Vec<&AuditEntry> = match scope {
        Scope::Audit => audit.entries.iter().collect(),
        Scope::Pending => {
            // O lote mais novo primeiro: e o numero que a pessoa acabou de colar.
            let mut batches: Vec<&PendingBatch> = pending.batches.iter().collect();
            batches.sort_by_key(|b| std::cmp::Reverse(b.created_utc));
            batches.into_iter().flat_map(|b| &b.entries).collect()
        }
    };

    let Some(current) = pool.iter().find(|e| same_name(&e.username, &jogador)) else {
        let wanted = key(&jogador);
        let mut suggestions: Vec<&str> = Vec::new();
        for e in &pool {
            let found = key(&e.username).contains(&wanted);
            if found && !suggestions.iter().any(|s| same_name(s, &e.username)) {
                suggestions.push(&e.username);
            }
        }
        suggestions.truncate(10);

        let mut embed = embeds::error(
            "Jogador não encontrado",
            format!(
                "**{}** não existe em `{}`.",
                truncate(&jogador, 60),
                scope.code()
            ),
        );
        if !suggestions.is_empty() {
            embed = embed.field("Você quis dizer", field_value(suggestions), false);
        }
        return reply(ctx, embed, true).await;
    };
    let current = (*current).clone();

    if remover.unwrap_or(false) {
        confirm_delete(ctx, current, scope).await
    } else {
        edit_entry(ctx, current, scope).await
    }
}

/// Remover e destrutivo: sempre passa por confirmacao.
async fn confirm_delete(ctx: Context<'_>, current: AuditEntry, scope: Scope) -> Result<(), Error> {
    let token = ctx.id().to_string();
    let confirm = format!("{token}:confirm");

    let prompt = serenity::CreateEmbed::new()
        .title("Confirmar remoção")
        .description(format!(
            "Remover **{}** de `{}`?\n{}",
            current.username,
            scope.label(),
            entry_block(&current)
        ))
        .colour(serenity::Colour::ORANGE);
    let buttons = serenity::CreateActionRow::Buttons(vec![
        serenity::CreateButton::new(&confirm)
            .label("Remover")
            .style(serenity::ButtonStyle::Danger),
        serenity::CreateButton::new(format!("{token}:cancel"))
            .label("Cancelar")
            .style(serenity::ButtonStyle::Secondary),
    ]);

    let handle = ctx
        .send(
            CreateReply::default()
                .embed(prompt)
                .components(vec![buttons])
                .ephemeral(true),
        )
        .await?;

    let press = serenity::ComponentInteractionCollector::new(ctx)
        .author_id(ctx.author().id)
        .filter(move |p| p.data.custom_id.starts_with(&token))
        .timeout(Duration::from_secs(60))
        .await;
    if let Some(press) = &press {
        press
            .create_response(ctx, serenity::CreateInteractionResponse::Acknowledge)
            .await?;
    }

    let show =
        |embed: serenity::CreateEmbed| CreateReply::default().embed(embed).components(vec![]);

    if !press.is_some_and(|p| p.data.custom_id == confirm) {
        let cancelled = serenity::CreateEmbed::new()
            .title("Remoção cancelada")
            .colour(serenity::Colour::BLURPLE);
        handle.edit(ctx, show(cancelled)).await?;
        return Ok(());
    }

    let name = current.username.clone();
    let removed = ctx
        .data()
        .audit
        .update(|audit, pending| {
            let drop = |entries: &mut Vec<AuditEntry>| {
                let before = entries.len();
                entries.retain(|e| !same_name(&e.username, &name));
                before - entries.len()
            };
            match scope {
                Scope::Audit => drop(&mut audit.entries),
                Scope::Pending => pending
                    .batches
                    .iter_mut()
                    .map(|b| drop(&mut b.entries))
                    .sum(),
            }
        })
        .await;

    let removed = match removed {
        Ok(removed) => removed,
        Err(cause) => {
            handle
                .edit(ctx, show(embeds::error("Remoção não aplicada", cause)))
                .await?;
            return Ok(());
        }
    };

    ctx.data()
        .audit
        .record(
            ctx.author(),
            Action::Remove,
            &format!(
                "Removeu **{name}** de `{}` ({removed} registro(s)).",
                scope.label()
            ),
        )
        .await;

    let done = serenity::CreateEmbed::new()
        .title("Jogador removido")
        .description(format!("**{name}** removido ({removed} registro(s))."))
        .colour(serenity::Colour::DARK_GREEN);
    handle.edit(ctx, show(done)).await?;
    Ok(())
}

async fn edit_entry(ctx: Context<'_>, before: AuditEntry, scope: Scope) -> Result<(), Error> {
    let poise::Context::Application(app) = ctx else {
        return Ok(());
    };

    let field = |label: &str, value: String| {
        serenity::CreateInputText::new(serenity::InputTextStyle::Short, label, "")
            .value(value)
            .max_length(32)
    };

    // Exatamente 5 campos, o teto do Discord. O cargo fica de fora: ele e do
    // /audit-setranks.
    let modal =
        serenity::CreateQuickModal::new(format!("Editar {}", truncate(&before.username, 30)))
            .timeout(Duration::from_secs(600))
            .field(field("Nome de usuário (Roblox)", before.username.clone()))
            .field(field("Kills", before.kills.to_string()))
            .field(field("Deaths", before.deaths.to_string()))
            .field(field("Assists", before.assists.to_string()))
            .field(field("Batalhas", before.battles.to_string()));

    let Some(response) = app
        .interaction
        .quick_modal(ctx.serenity_context(), modal)
        .await?
    else {
        return Ok(());
    };
    let modal = &response.interaction;
    defer_modal(ctx, modal).await?;

    let fail =
        |reason: String| edit_modal(ctx, modal, embeds::error("Edição não aplicada", reason));

    let [name, kills, deaths, assists, battles] = &response.inputs[..] else {
        return fail("O formulário voltou incompleto.".to_owned()).await;
    };

    let new_name = name.trim().to_owned();
    if new_name.is_empty() || new_name.chars().count() > 32 || new_name.contains(',') {
        return fail(
            "O nome precisa ter entre 1 e 32 caracteres e não pode conter vírgula.".to_owned(),
        )
        .await;
    }

    let (Some(kills), Some(deaths), Some(assists), Some(battles)) =
        (count(kills), count(deaths), count(assists), count(battles))
    else {
        return fail(
            "Kills, deaths, assists e batalhas precisam ser números inteiros não negativos."
                .to_owned(),
        )
        .await;
    };

    let renamed = !same_name(&before.username, &new_name);
    let outcome = ctx
        .data()
        .audit
        .update(|audit, pending| -> Option<String> {
            match scope {
                Scope::Pending => {
                    let mut entries: Vec<&mut AuditEntry> =
                        pending.batches.iter_mut().flat_map(|b| &mut b.entries).collect();

                    if renamed && entries.iter().any(|e| same_name(&e.username, &new_name)) {
                        return Some(format!(
                            "Já existe **{new_name}** nos lotes pendentes. Renomeie para outro nome ou remova a duplicata."
                        ));
                    }

                    let mut found = false;
                    for entry in entries.iter_mut().filter(|e| same_name(&e.username, &before.username)) {
                        entry.username = new_name.clone();
                        entry.kills = kills;
                        entry.deaths = deaths;
                        entry.assists = assists;
                        found = true;
                    }
                    (!found).then(|| "O jogador não está mais nos lotes pendentes.".to_owned())
                }
                Scope::Audit => {
                    if renamed && audit.entries.iter().any(|e| same_name(&e.username, &new_name)) {
                        return Some(format!(
                            "Já existe **{new_name}** na auditoria. Renomear aqui juntaria dois jogadores sem aviso, então a operação foi cancelada."
                        ));
                    }

                    let Some(target) = audit
                        .entries
                        .iter_mut()
                        .find(|e| same_name(&e.username, &before.username))
                    else {
                        return Some("O jogador não está mais na auditoria.".to_owned());
                    };
                    target.username = new_name.clone();
                    target.kills = kills;
                    target.deaths = deaths;
                    target.assists = assists;
                    target.battles = battles;
                    None
                }
            }
        })
        .await;

    match outcome {
        Err(cause) | Ok(Some(cause)) => return fail(cause).await,
        Ok(None) => {}
    }

    let after = AuditEntry {
        username: new_name,
        kills,
        deaths,
        assists,
        battles: if scope == Scope::Pending {
            before.battles
        } else {
            battles
        },
        rank: before.rank.clone(),
    };

    let mut embed = serenity::CreateEmbed::new()
        .title("Registro atualizado")
        .description(format!("Escopo: `{}`", scope.label()))
        .colour(serenity::Colour::DARK_GREEN)
        .timestamp(serenity::Timestamp::now())
        .field("Antes", entry_block(&before), false)
        .field("Depois", entry_block(&after), false);
    if scope == Scope::Pending {
        embed = embed.footer(serenity::CreateEmbedFooter::new(
            "Batalhas não são editáveis no escopo pendente: elas são contadas por lote no /audit-push.",
        ));
    }

    ctx.data()
        .audit
        .record(
            ctx.author(),
            Action::Edit,
            &format!(
                "Editou **{}** em `{}`: {}/{}/{} ({} bat.) → {} {}/{}/{} ({} bat.)",
                before.username,
                scope.label(),
                before.kills,
                before.deaths,
                before.assists,
                before.battles,
                after.username,
                after.kills,
                after.deaths,
                after.assists,
                after.battles
            ),
        )
        .await;

    edit_modal(ctx, modal, embed).await
}

fn count(raw: &str) -> Option<i64> {
    raw.trim().parse().ok().filter(|v: &i64| *v >= 0)
}
