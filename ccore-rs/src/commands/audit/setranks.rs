use std::{collections::BTreeMap, pin::pin, time::Duration};

use poise::{futures_util::StreamExt, serenity_prelude as serenity};

use super::{reply, store_error};
use crate::{
    Context, Error,
    audit::{
        model::{AuditEntry, index, key, same_name, search},
        parse::{RankParse, build_rank_block, parse_ranks},
        store::Action,
    },
    embeds::truncate,
    staff,
};

/// Teto de opcoes de um select do Discord.
const SELECT_SIZE: usize = 25;
/// Jogadores por leva: so uma escolha de usabilidade, o bloco "nome = cargo"
/// cabe num campo unico do modal.
const MAX_PER_BATCH: usize = 10;
const IDLE: Duration = Duration::from_secs(180);

/// O estado da tela vive aqui, dentro do comando: nada global para vazar.
struct Session {
    id: String,
    roster: Vec<AuditEntry>,
    query: String,
    selection: Vec<String>,
    /// Quem mudou nesta sessao, pelo nome em minusculas: (nome, cargo novo).
    changed: BTreeMap<String, (String, String)>,
    notice: Option<String>,
}

/// Busca jogadores e define o cargo de até 10 de uma vez
#[poise::command(slash_command, rename = "audit-setranks")]
pub async fn audit_setranks(ctx: Context<'_>) -> Result<(), Error> {
    ctx.defer_ephemeral().await?;

    if let Some(denied) = staff::check(ctx).await {
        return reply(ctx, denied, true).await;
    }

    let mut roster = match ctx.data().audit.read_audit().await {
        Ok(audit) => audit.entries,
        Err(cause) => return reply(ctx, store_error(cause), true).await,
    };
    if roster.is_empty() {
        let embed = serenity::CreateEmbed::new()
            .title("Auditoria vazia")
            .description("Nenhum jogador registrado ainda. Use `/audit-add` primeiro.")
            .colour(serenity::Colour::ORANGE);
        return reply(ctx, embed, true).await;
    }
    roster.sort_by_cached_key(|e| key(&e.username));

    let poise::Context::Application(app) = ctx else {
        return Ok(());
    };
    let sctx = ctx.serenity_context();

    let mut session = Session {
        id: ctx.id().to_string(),
        roster,
        query: String::new(),
        selection: Vec::new(),
        changed: BTreeMap::new(),
        notice: None,
    };

    let message = app.interaction.edit_response(sctx, session.view()).await?;

    // Um coletor so para a sessao inteira, criado antes do primeiro clique:
    // recriar a cada volta deixava uma janela em que um clique rapido se
    // perdia e o Discord mostrava "interacao falhou".
    let id = session.id.clone();
    let mut presses = pin!(
        serenity::ComponentInteractionCollector::new(sctx)
            .message_id(message.id)
            .author_id(ctx.author().id)
            .filter(move |p| p.data.custom_id.ends_with(&id))
            .stream()
    );

    loop {
        let Ok(Some(press)) = tokio::time::timeout(IDLE, presses.next()).await else {
            let end = finish(
                "Sessão expirada",
                &session.changed,
                serenity::Colour::ORANGE,
            );
            app.interaction.edit_response(sctx, end).await?;
            return Ok(());
        };

        match press.data.custom_id.split(':').next().unwrap_or_default() {
            "done" => {
                acknowledge(sctx, &press).await?;
                let end = finish(
                    "Cargos atualizados",
                    &session.changed,
                    serenity::Colour::DARK_GREEN,
                );
                app.interaction.edit_response(sctx, end).await?;
                return Ok(());
            }
            "clear" => {
                session.selection.clear();
                session.notice = None;
                acknowledge(sctx, &press).await?;
            }
            "pick" => {
                if let serenity::ComponentInteractionDataKind::StringSelect { values } =
                    &press.data.kind
                {
                    session.pick(values);
                }
                acknowledge(sctx, &press).await?;
            }
            "search" => {
                let input = serenity::CreateInputText::new(
                    serenity::InputTextStyle::Short,
                    "Parte do nome; vazio lista o efetivo inteiro",
                    "",
                )
                .placeholder("Ex.: oda")
                .max_length(32)
                .required(false);
                let input = if session.query.is_empty() {
                    input
                } else {
                    input.value(&session.query)
                };

                session.notice = match ask(sctx, &press, "Buscar jogador", input).await? {
                    Some(typed) => {
                        session.query = typed.trim().to_owned();
                        None
                    }
                    None => Some("⚠️ A busca expirou.".to_owned()),
                };
            }
            "apply" => {
                if session.selection.is_empty() {
                    acknowledge(sctx, &press).await?;
                    continue;
                }

                let prefill = build_rank_block(
                    session
                        .selection
                        .iter()
                        .map(|name| (name.as_str(), session.rank_of(name))),
                );
                let input = serenity::CreateInputText::new(
                    serenity::InputTextStyle::Paragraph,
                    "Uma linha por jogador: nome = cargo",
                    "",
                )
                .placeholder("RafaOdebrecht = Serjeant Major")
                .max_length(4000)
                .required(false)
                .value(prefill);

                match ask(sctx, &press, "Definir cargos", input).await? {
                    Some(block) => session.apply(ctx, &block).await,
                    None => {
                        session.notice =
                            Some("⚠️ O modal expirou; nenhum cargo foi alterado.".to_owned());
                    }
                }
            }
            _ => continue,
        }

        app.interaction.edit_response(sctx, session.view()).await?;
    }
}

impl Session {
    fn rank_of(&self, name: &str) -> &str {
        self.roster
            .iter()
            .find(|e| same_name(&e.username, name))
            .map_or("", |e| e.rank.as_str())
    }

    /// Soma a cesta em vez de trocar: da para juntar gente de buscas diferentes.
    fn pick(&mut self, values: &[String]) {
        for name in values {
            if self.selection.len() >= MAX_PER_BATCH {
                break;
            }
            if !self.selection.iter().any(|s| same_name(s, name)) {
                self.selection.push(name.clone());
            }
        }

        self.notice = (self.selection.len() >= MAX_PER_BATCH).then(|| {
            format!("ℹ️ Cesta cheia ({MAX_PER_BATCH}). Defina os cargos ou limpe a seleção.")
        });
    }

    async fn apply(&mut self, ctx: Context<'_>, block: &str) {
        let parsed = parse_ranks(block);
        let known = index(&self.roster);

        // Nome fora da auditoria e reportado, nao engolido: um erro de
        // digitacao viraria "0 cargos atualizados" sem dizer por que.
        let mut updates = Vec::new();
        let mut unknown = Vec::new();
        for line in &parsed.entries {
            match known.get(&key(&line.username)) {
                Some(&i) => updates.push((self.roster[i].username.clone(), line.rank.clone())),
                None => unknown.push(line.username.clone()),
            }
        }

        let mut applied = 0;
        if !updates.is_empty() {
            let saved = ctx
                .data()
                .audit
                .update(|audit, _| {
                    let positions = index(&audit.entries);
                    updates
                        .iter()
                        .filter_map(|(name, rank)| {
                            let &i = positions.get(&key(name))?;
                            audit.entries[i].rank = rank.clone();
                            Some((audit.entries[i].username.clone(), rank.clone()))
                        })
                        .collect::<Vec<_>>()
                })
                .await;

            let saved = match saved {
                Ok(saved) => saved,
                Err(cause) => {
                    self.notice =
                        Some(format!("⚠️ Não consegui gravar: {}", truncate(&cause, 300)));
                    return;
                }
            };

            for (name, rank) in &saved {
                if let Some(entry) = self
                    .roster
                    .iter_mut()
                    .find(|e| same_name(&e.username, name))
                {
                    entry.rank = rank.clone();
                }
                self.changed.insert(key(name), (name.clone(), rank.clone()));
            }

            let details = saved
                .iter()
                .map(|(name, rank)| format!("{name} → {}", or_no_rank(rank, "sem cargo")))
                .collect::<Vec<_>>()
                .join(", ");
            ctx.data()
                .audit
                .record(ctx.author(), Action::SetRanks, &details)
                .await;

            // A cesta so esvazia quando algo foi gravado: um bloco todo errado
            // fica ali para corrigir.
            applied = saved.len();
            self.selection.clear();
        }

        self.notice = Some(notice(applied, &unknown, &parsed));
    }

    fn view(&self) -> serenity::EditInteractionResponse {
        // Quem ja esta na cesta sai das opcoes: so ocuparia uma das 25 vagas.
        let matches: Vec<&AuditEntry> = search(
            &self.roster,
            &self.query,
            SELECT_SIZE + self.selection.len(),
        )
        .into_iter()
        .filter(|e| !self.selection.iter().any(|s| same_name(s, &e.username)))
        .take(SELECT_SIZE)
        .collect();
        let full = self.selection.len() >= MAX_PER_BATCH;
        let free = MAX_PER_BATCH - self.selection.len();

        let mut description = Vec::new();
        if let Some(notice) = &self.notice {
            description.push(format!("{notice}\n"));
        }
        description.push(if self.query.is_empty() {
            "🔍 Sem filtro — mostrando o começo do efetivo em ordem alfabética.".to_owned()
        } else {
            format!(
                "🔍 Busca: **{}** — {} resultado(s) na lista.",
                truncate(&self.query, 60),
                matches.len()
            )
        });

        if self.selection.is_empty() {
            description.push(format!(
                "\nCesta vazia. Selecione até {MAX_PER_BATCH} jogadores; buscar de novo **não** perde a seleção."
            ));
        } else {
            let basket = self
                .selection
                .iter()
                .map(|name| {
                    format!(
                        "{:<22} {}",
                        truncate(name, 22),
                        or_no_rank(self.rank_of(name), "— sem cargo")
                    )
                })
                .collect::<Vec<_>>()
                .join("\n");
            description.push(format!(
                "\n**Na cesta ({}/{MAX_PER_BATCH}):**\n```\n{basket}\n```",
                self.selection.len()
            ));
        }

        let embed = serenity::CreateEmbed::new()
            .title("Definir cargos")
            .description(description.join("\n"))
            .colour(if full {
                serenity::Colour::ORANGE
            } else {
                serenity::Colour::BLURPLE
            })
            .footer(serenity::CreateEmbedFooter::new(format!(
                "{} jogadores na auditoria — {} alterado(s) nesta sessão",
                self.roster.len(),
                self.changed.len()
            )));

        let mut rows = Vec::new();

        // Select vazio e recusado pelo Discord: sem resultado ou com a cesta
        // cheia, ele simplesmente nao vai.
        if !matches.is_empty() && !full {
            let options = matches
                .iter()
                .map(|e| {
                    let summary = format!(
                        "K{} D{} A{} B{} — {}",
                        e.kills,
                        e.deaths,
                        e.assists,
                        e.battles,
                        or_no_rank(&e.rank, "sem cargo")
                    );
                    serenity::CreateSelectMenuOption::new(truncate(&e.username, 100), &e.username)
                        .description(truncate(&summary, 100))
                })
                .collect();

            rows.push(serenity::CreateActionRow::SelectMenu(
                serenity::CreateSelectMenu::new(
                    format!("pick:{}", self.id),
                    serenity::CreateSelectMenuKind::String { options },
                )
                .placeholder(format!("Selecione jogadores (cabem mais {free})"))
                .min_values(1)
                .max_values(u8::try_from(free.min(matches.len())).unwrap_or(1)),
            ));
        }

        let empty = self.selection.is_empty();
        let button = |action: &str, label: &str, style, disabled| {
            serenity::CreateButton::new(format!("{action}:{}", self.id))
                .label(label)
                .style(style)
                .disabled(disabled)
        };
        rows.push(serenity::CreateActionRow::Buttons(vec![
            button("search", "🔍 Buscar", serenity::ButtonStyle::Primary, false),
            button(
                "apply",
                "Definir cargos",
                serenity::ButtonStyle::Success,
                empty,
            ),
            button(
                "clear",
                "Limpar seleção",
                serenity::ButtonStyle::Secondary,
                empty,
            ),
            button("done", "Concluir", serenity::ButtonStyle::Secondary, false),
        ]));

        serenity::EditInteractionResponse::new()
            .embed(embed)
            .components(rows)
    }
}

async fn acknowledge(
    sctx: &serenity::Context,
    press: &serenity::ComponentInteraction,
) -> Result<(), Error> {
    press
        .create_response(sctx, serenity::CreateInteractionResponse::Acknowledge)
        .await?;
    Ok(())
}

/// Responde ao clique com um modal de campo unico e devolve o texto, ou None
/// se a pessoa nao enviou a tempo.
async fn ask(
    sctx: &serenity::Context,
    press: &serenity::ComponentInteraction,
    title: &str,
    input: serenity::CreateInputText,
) -> Result<Option<String>, Error> {
    let modal = serenity::CreateQuickModal::new(title)
        .timeout(IDLE)
        .field(input);
    let Some(response) = press.quick_modal(sctx, modal).await? else {
        return Ok(None);
    };

    response
        .interaction
        .create_response(sctx, serenity::CreateInteractionResponse::Acknowledge)
        .await?;
    Ok(response.inputs.into_iter().next())
}

fn or_no_rank<'a>(rank: &'a str, fallback: &'a str) -> &'a str {
    if rank.trim().is_empty() {
        fallback
    } else {
        rank
    }
}

fn notice(applied: usize, unknown: &[String], parsed: &RankParse) -> String {
    let mut parts = Vec::new();

    if applied > 0 {
        parts.push(format!("✅ {applied} cargo(s) atualizado(s)."));
    }
    if !unknown.is_empty() {
        parts.push(format!(
            "⚠️ Fora da auditoria: {}",
            truncate(&unknown.join(", "), 200)
        ));
    }
    for failure in parsed.failures.iter().take(3) {
        parts.push(format!(
            "⚠️ Linha {} (`{}`): {}",
            failure.line,
            truncate(&failure.raw, 40),
            failure.reason
        ));
    }
    if !parsed.duplicates.is_empty() {
        let mut repeated: Vec<&str> = Vec::new();
        for name in &parsed.duplicates {
            if !repeated.contains(&name.as_str()) {
                repeated.push(name);
            }
        }
        parts.push(format!(
            "ℹ️ Repetidos no bloco (valeu a última linha): {}",
            truncate(&repeated.join(", "), 150)
        ));
    }

    if parts.is_empty() {
        "⚠️ Nada foi alterado: o bloco veio vazio.".to_owned()
    } else {
        parts.join("\n")
    }
}

fn finish(
    title: &str,
    changed: &BTreeMap<String, (String, String)>,
    colour: serenity::Colour,
) -> serenity::EditInteractionResponse {
    let summary = if changed.is_empty() {
        "Nenhum cargo foi alterado.".to_owned()
    } else {
        let lines = changed
            .values()
            .take(40)
            .map(|(name, rank)| format!("**{name}** → {}", or_no_rank(rank, "*sem cargo*")))
            .collect::<Vec<_>>()
            .join("\n");
        if changed.len() > 40 {
            format!("{lines}\n… e mais {}", changed.len() - 40)
        } else {
            lines
        }
    };

    let embed = serenity::CreateEmbed::new()
        .title(title)
        .description(summary)
        .colour(colour);
    serenity::EditInteractionResponse::new()
        .embed(embed)
        .components(vec![])
}
