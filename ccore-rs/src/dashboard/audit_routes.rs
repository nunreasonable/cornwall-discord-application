//! Auditoria de batalhas e promocoes pelo painel. Usa o mesmo AuditStore e o
//! mesmo publish dos comandos /audit-*, entao as garantias valem igual.

use serde_json::{Value, json};
use tracing::warn;

use super::{
    Dashboard,
    request::{Fail, Reply, Req, err, ok, utf16_len},
};
use crate::audit::{
    ladder,
    model::{AuditEntry, same_name},
    publish::{self, Deps},
    store::Action,
};

fn query_int(raw: Option<&str>, fallback: usize, min: usize, max: usize) -> usize {
    raw.and_then(|v| v.trim().parse::<i64>().ok())
        .map_or(fallback, |v| v.clamp(min as i64, max as i64) as usize)
}

/// Efetivo consolidado mais o que ainda esta na fila.
pub async fn roster(d: &Dashboard, req: &mut Req) -> Reply {
    if d.require(&req.headers, 1).await.is_none() {
        return d.auth_failure(req, "Permissão insuficiente.");
    }

    let (audit, pending) = d.bot.audit.read_both().await?;
    let mut entries: Vec<&AuditEntry> = audit.entries.iter().collect();
    entries.sort_by_cached_key(|e| (std::cmp::Reverse(e.battles), e.username.to_lowercase()));

    ok(
        200,
        json!({
            "lastUpdatedUtc": audit.last_updated_utc,
            "totals": {
                "players": audit.entries.len(),
                "battles": audit.entries.iter().map(|e| e.battles).sum::<i64>(),
                "kills": audit.entries.iter().map(|e| e.kills).sum::<i64>(),
            },
            "entries": entries.iter().map(|e| json!({
                "username": e.username,
                "kills": e.kills,
                "deaths": e.deaths,
                "assists": e.assists,
                "battles": e.battles,
                "rank": e.rank,
                "kd": e.kd_ratio(),
            })).collect::<Vec<Value>>(),
            "pending": pending.batches.iter().map(|b| json!({
                "batchId": b.batch_id,
                "createdUtc": b.created_utc,
                "submittedByUsername": b.submitted_by_username,
                "players": b.entries.len(),
            })).collect::<Vec<Value>>(),
        }),
    )
}

/// O mesmo historico do /audit-logs.
pub async fn history(d: &Dashboard, req: &mut Req) -> Reply {
    if d.require(&req.headers, 1).await.is_none() {
        return d.auth_failure(req, "Permissão insuficiente.");
    }

    let take = query_int(req.query("take"), 100, 1, 500);
    let mut log = d.bot.audit.read_log().await?;
    log.sort_by_key(|e| std::cmp::Reverse(e.timestamp_utc));
    log.truncate(take);

    ok(
        200,
        json!({
            "logs": log.iter().map(|e| json!({
                "timestampUtc": e.timestamp_utc,
                "userId": e.user_id.to_string(),
                "username": e.username,
                "action": e.action,
                "details": e.details,
            })).collect::<Vec<Value>>(),
        }),
    )
}

/// Edita, renomeia ou remove um jogador, como o /audit-edit. Corrigir numero e
/// nivel 1; APAGAR o registro de alguem e nivel 2.
pub async fn entry(d: &Dashboard, req: &mut Req) -> Reply {
    // O piso vem antes do corpo: sem sessao nao se chega nem ao parser.
    let Some(session) = d.require(&req.headers, 1).await else {
        return d.auth_failure(req, "Permissão insuficiente para editar a auditoria.");
    };

    let body = match req.json().await {
        Err(Fail::BadJson) => return err(400, "Corpo da requisição não é JSON válido."),
        other => other?,
    };

    let remove = body.boolean("remove");
    if remove && d.require(&req.headers, 2).await.is_none() {
        return d.auth_failure(req, "Remover registros da auditoria exige permissão 2+.");
    }

    let Some(username) = body.trimmed("username").filter(|u| !u.is_empty()) else {
        return err(400, "username é obrigatório.");
    };
    let pending_scope = body
        .string("scope")
        .is_some_and(|s| s.eq_ignore_ascii_case("pendente"));
    let scope = if pending_scope {
        "pendente"
    } else {
        "auditoria"
    };

    if remove {
        let name = username.clone();
        let removed = d
            .bot
            .audit
            .update(move |audit, pending| {
                let before = audit.entries.len()
                    + pending
                        .batches
                        .iter()
                        .map(|b| b.entries.len())
                        .sum::<usize>();
                if pending_scope {
                    for batch in &mut pending.batches {
                        batch.entries.retain(|e| !same_name(&e.username, &name));
                    }
                } else {
                    audit.entries.retain(|e| !same_name(&e.username, &name));
                }
                before
                    - audit.entries.len()
                    - pending
                        .batches
                        .iter()
                        .map(|b| b.entries.len())
                        .sum::<usize>()
            })
            .await?;

        if removed == 0 {
            return err(
                404,
                format!("'{username}' não foi encontrado em `{scope}`."),
            );
        }

        let details = format!("Removeu **{username}** de `{scope}` ({removed} registro(s)).");
        d.bot
            .audit
            .record_as(session.user_id, &session.username, Action::Remove, &details)
            .await;
        d.record(&session, "AUDIT_REMOVE", &details).await;
        return ok(200, json!({ "ok": true, "removed": removed }));
    }

    let new_name = body
        .trimmed("newUsername")
        .filter(|n| !n.is_empty())
        .unwrap_or_else(|| username.clone());
    if utf16_len(&new_name) > 32 || new_name.contains(',') {
        return err(
            400,
            "O nome precisa ter até 32 caracteres e não pode conter vírgula.",
        );
    }

    let (Ok(kills), Ok(deaths), Ok(assists), Ok(battles)) = (
        body.count("kills"),
        body.count("deaths"),
        body.count("assists"),
        body.count("battles"),
    ) else {
        return err(
            400,
            "kills, deaths, assists e battles precisam ser inteiros não negativos.",
        );
    };
    let (kills, deaths, assists, battles) = (
        kills.map(i64::from),
        deaths.map(i64::from),
        assists.map(i64::from),
        battles.map(i64::from),
    );

    let (old, new) = (username.clone(), new_name.clone());
    let (failure, before) = d
        .bot
        .audit
        .update(move |audit, pending| {
            let renamed = !same_name(&old, &new);
            // So grava o que veio: campo ausente mantem o valor atual.
            let apply = |e: &mut AuditEntry, with_battles: bool| {
                e.username = new.clone();
                if let Some(v) = kills {
                    e.kills = v;
                }
                if let Some(v) = deaths {
                    e.deaths = v;
                }
                if let Some(v) = assists {
                    e.assists = v;
                }
                if let (true, Some(v)) = (with_battles, battles) {
                    e.battles = v;
                }
            };

            if pending_scope {
                let all = || pending.batches.iter().flat_map(|b| &b.entries);
                if renamed && all().any(|e| same_name(&e.username, &new)) {
                    return (Some(format!("Já existe '{new}' nos lotes pendentes.")), None);
                }
                let mut before = None;
                // Batalhas nao se editam no pendente: sao contadas por lote no push.
                for entry in pending
                    .batches
                    .iter_mut()
                    .flat_map(|b| &mut b.entries)
                    .filter(|e| same_name(&e.username, &old))
                {
                    before.get_or_insert_with(|| entry.clone());
                    apply(entry, false);
                }
                return match before {
                    Some(before) => (None, Some(before)),
                    None => (
                        Some("O jogador não está mais nos lotes pendentes.".to_owned()),
                        None,
                    ),
                };
            }

            if renamed && audit.entries.iter().any(|e| same_name(&e.username, &new)) {
                return (
                    Some(format!(
                        "Já existe '{new}' na auditoria. Renomear aqui juntaria dois jogadores sem aviso, então a operação foi cancelada."
                    )),
                    None,
                );
            }
            let Some(target) = audit
                .entries
                .iter_mut()
                .find(|e| same_name(&e.username, &old))
            else {
                return (Some("O jogador não está mais na auditoria.".to_owned()), None);
            };
            let before = target.clone();
            apply(target, true);
            (None, Some(before))
        })
        .await?;

    if let Some(failure) = failure {
        return err(400, failure);
    }
    let Some(before) = before else {
        return Err(Fail::Internal("edicao sem registro anterior".to_owned()));
    };

    let after_battles = if pending_scope {
        before.battles
    } else {
        battles.unwrap_or(before.battles)
    };
    let details = format!(
        "Editou **{username}** em `{scope}`: {}/{}/{} ({} bat.) → {new_name} {}/{}/{} ({after_battles} bat.)",
        before.kills,
        before.deaths,
        before.assists,
        before.battles,
        kills.unwrap_or(before.kills),
        deaths.unwrap_or(before.deaths),
        assists.unwrap_or(before.assists),
    );
    d.bot
        .audit
        .record_as(session.user_id, &session.username, Action::Edit, &details)
        .await;
    d.record(&session, "AUDIT_EDIT", &details).await;
    ok(200, json!({ "ok": true, "username": new_name }))
}

/// Define a patente de um ou mais jogadores; campo vazio remove. Nivel 1:
/// e digitacao de dado, nao da cargo no Discord.
pub async fn ranks(d: &Dashboard, req: &mut Req) -> Reply {
    let Some(session) = d.require(&req.headers, 1).await else {
        return d.auth_failure(req, "Permissão insuficiente para definir patentes.");
    };

    let body = req.json().await?;
    let Some(raw) = body
        .get("ranks")
        .and_then(Value::as_array)
        .filter(|r| !r.is_empty())
    else {
        return err(400, "ranks é obrigatório: uma lista de { username, rank }.");
    };

    let text = |item: &Value, field: &str| match item.get(field) {
        Some(Value::String(s)) => Some(s.trim().to_owned()),
        Some(Value::Number(n)) => Some(n.to_string()),
        _ => None,
    };
    let requested: Vec<(String, String)> = raw
        .iter()
        .filter(|item| item.is_object())
        .filter_map(|item| {
            let name = text(item, "username").filter(|n| !n.is_empty())?;
            Some((name, text(item, "rank").unwrap_or_default()))
        })
        .collect();
    if requested.is_empty() {
        return err(400, "Nenhum jogador válido na lista.");
    }

    let (changes, missing) = d
        .bot
        .audit
        .update(move |audit, _| {
            let (mut changes, mut missing) = (Vec::new(), Vec::new());
            let shown = |r: &str| {
                if r.trim().is_empty() {
                    "-".to_owned()
                } else {
                    r.to_owned()
                }
            };
            for (name, rank) in requested {
                let Some(target) = audit
                    .entries
                    .iter_mut()
                    .find(|e| same_name(&e.username, &name))
                else {
                    missing.push(name);
                    continue;
                };
                if target.rank == rank {
                    continue;
                }
                changes.push(format!(
                    "{}: '{}' → '{}'",
                    target.username,
                    shown(&target.rank),
                    shown(&rank)
                ));
                target.rank = rank;
            }
            (changes, missing)
        })
        .await?;

    if !changes.is_empty() {
        let details = format!(
            "Definiu cargo(s) de {} jogador(es): {}{}",
            changes.len(),
            changes
                .iter()
                .take(10)
                .cloned()
                .collect::<Vec<_>>()
                .join("; "),
            if changes.len() > 10 { "…" } else { "" }
        );
        d.bot
            .audit
            .record_as(
                session.user_id,
                &session.username,
                Action::SetRanks,
                &details,
            )
            .await;
        d.record(&session, "AUDIT_SETRANKS", &details).await;
    }

    ok(
        200,
        json!({ "ok": true, "changed": changes.len(), "changes": changes, "missing": missing }),
    )
}

/// Publica no GitHub pelo mesmo publish do /audit-push. Repositorio publico:
/// nivel 2.
pub async fn push(d: &Dashboard, req: &mut Req) -> Reply {
    let Some(session) = d.require(&req.headers, 2).await else {
        return d.auth_failure(req, "Permissão insuficiente para publicar a auditoria.");
    };

    let body = req.json().await?;
    let dry_run = body.boolean("dryRun");
    let deps = Deps {
        audit: &d.bot.audit,
        config: &d.bot.config,
        http: &d.bot.http,
        github_token: &d.bot.github_token,
    };

    let outcome = match publish::publish(deps, dry_run).await {
        Ok(outcome) => outcome,
        Err(failure) => {
            warn!(
                "dashboard: publicacao da auditoria falhou em {}: {}",
                failure.step, failure.error
            );
            return ok(
                502,
                json!({
                    "error": format!("Falha ao publicar durante: {}.", failure.step),
                    "details": failure.error.to_string(),
                    "hint": "O arquivo pendente NÃO foi limpo; republicar não conta em dobro.",
                }),
            );
        }
    };

    if !outcome.dry_run && !outcome.nothing_to_do {
        let details = outcome.log_details();
        d.bot
            .audit
            .record_as(session.user_id, &session.username, Action::Push, &details)
            .await;
        d.record(&session, "AUDIT_PUSH", &details).await;
    }

    ok(
        200,
        json!({
            "ok": true,
            "dryRun": outcome.dry_run,
            "nothingToDo": outcome.nothing_to_do,
            "hasPending": outcome.has_pending,
            "pendingBatches": outcome.pending_batches,
            "totalEntries": outcome.total_entries,
            "batchesApplied": outcome.report.batches_applied,
            "batchesSkipped": outcome.report.batches_skipped,
            "playersUpdated": outcome.report.players_updated,
            "battlesAdded": outcome.report.battles_added,
            "newPlayers": outcome.report.new_players,
            "auditCommitUrl": outcome.audit_commit_url,
            "archiveCommitUrl": outcome.archive_commit_url,
            "archivePath": outcome.archive_path,
        }),
    )
}

/// Quem esta elegivel, pela mesma escada do /promocoes.
pub async fn promotions(d: &Dashboard, req: &mut Req) -> Reply {
    if d.require(&req.headers, 1).await.is_none() {
        return d.auth_failure(req, "Permissão insuficiente.");
    }

    let audit = d.bot.audit.read_audit().await?;
    let mut results: Vec<ladder::Evaluation> = audit.entries.iter().map(ladder::evaluate).collect();
    results.sort_by_cached_key(|r| {
        (
            !r.is_promotable(),
            std::cmp::Reverse(r.battles),
            r.username.to_lowercase(),
        )
    });

    let players: Vec<Value> = results
        .iter()
        .map(|r| {
            json!({
                "username": r.username,
                "promotable": r.is_promotable(),
                "status": format!("{:?}", r.status),
                // A escada nao decide sozinha: separa "bateu o numero" de
                // "o comando precisa avaliar".
                "needsApproval": r.status == ladder::Status::EligibleNeedsApproval,
                "currentRank": (!r.current_name.trim().is_empty()).then_some(r.current_name),
                "targetRank": r.target.map(|t| t.name),
                "nextRank": r.next.map(|n| n.name),
                "stepsSkipped": r.steps,
                "battles": r.battles,
                "battlesToNext": r.to_next,
            })
        })
        .collect();

    ok(
        200,
        json!({
            "promotable": results.iter().filter(|r| r.is_promotable()).count(),
            "total": results.len(),
            "players": players,
        }),
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn take_do_historico_tem_limites() {
        assert_eq!(query_int(None, 100, 1, 500), 100);
        assert_eq!(query_int(Some("0"), 100, 1, 500), 1);
        assert_eq!(query_int(Some("9000"), 100, 1, 500), 500);
    }
}
