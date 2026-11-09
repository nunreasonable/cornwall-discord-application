use std::collections::{HashMap, HashSet};

use chrono::{DateTime, Datelike, Utc};
use poise::serenity_prelude as serenity;
use serde::{Deserialize, Serialize};

const MAX_TRACKED_BATCH_IDS: usize = 500;

/// Estatisticas acumuladas de um jogador. Os nomes dos campos sao os do
/// audit.json que o bot em C# ja gravou e publicou.
#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
#[serde(default)]
pub struct AuditEntry {
    pub username: String,
    pub kills: i64,
    pub deaths: i64,
    pub assists: i64,
    pub battles: i64,
    pub rank: String,
}

impl AuditEntry {
    pub fn kd(&self) -> f64 {
        if self.deaths == 0 {
            self.kills as f64
        } else {
            self.kills as f64 / self.deaths as f64
        }
    }

    /// So para exibir: derivado de kills/deaths, nunca vai para o arquivo.
    pub fn kd_ratio(&self) -> String {
        if self.deaths == 0 {
            self.kills.to_string()
        } else {
            format!("{:.2}", self.kd())
        }
    }
}

/// O arquivo consolidado, fonte da verdade da auditoria.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct AuditFile {
    pub version: i64,
    pub last_updated_utc: DateTime<Utc>,
    /// Lotes ja incorporados: um /audit-push repetido nao conta em dobro.
    pub applied_batch_ids: Vec<String>,
    pub entries: Vec<AuditEntry>,
}

impl Default for AuditFile {
    fn default() -> Self {
        Self {
            version: 1,
            last_updated_utc: DateTime::default(),
            applied_batch_ids: Vec::new(),
            entries: Vec::new(),
        }
    }
}

impl AuditFile {
    /// Carimbo para embed. Arquivo que nunca foi gravado tem data zero.
    pub fn timestamp(&self) -> serenity::Timestamp {
        if self.last_updated_utc.year() < 2000 {
            return serenity::Timestamp::now();
        }
        serenity::Timestamp::from_unix_timestamp(self.last_updated_utc.timestamp())
            .unwrap_or_else(|_| serenity::Timestamp::now())
    }
}

/// Um /audit-add. A regra e "+1 batalha por jogador por lote", entao os lotes
/// ficam separados ate a consolidacao.
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct PendingBatch {
    pub batch_id: String,
    pub created_utc: DateTime<Utc>,
    pub submitted_by_user_id: u64,
    pub submitted_by_username: String,
    pub entries: Vec<AuditEntry>,
    pub raw_lines: Vec<String>,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(default)]
pub struct PendingFile {
    pub batches: Vec<PendingBatch>,
}

/// Nome sem diferenca de caixa, para indice e comparacao.
pub fn key(name: &str) -> String {
    name.to_lowercase()
}

pub fn same_name(a: &str, b: &str) -> bool {
    key(a) == key(b)
}

/// Posicao de cada jogador, sem diferenca de caixa. Nome repetido nao quebra:
/// o arquivo e editavel a mao, e o ultimo vence.
pub fn index(entries: &[AuditEntry]) -> HashMap<String, usize> {
    entries
        .iter()
        .enumerate()
        .filter(|(_, e)| !e.username.trim().is_empty())
        .map(|(i, e)| (key(&e.username), i))
        .collect()
}

/// Filtra o efetivo pelo texto digitado; quem comeca com ele vem primeiro.
/// Texto vazio devolve o efetivo em ordem alfabetica.
pub fn search<'a>(entries: &'a [AuditEntry], typed: &str, take: usize) -> Vec<&'a AuditEntry> {
    let query = key(typed.trim());
    let mut found: Vec<&AuditEntry> = entries
        .iter()
        .filter(|e| !e.username.trim().is_empty() && key(&e.username).contains(&query))
        .collect();

    found.sort_by_cached_key(|e| {
        let name = key(&e.username);
        (!name.starts_with(&query), name)
    });
    found.truncate(take);
    found
}

#[derive(Debug, Default)]
pub struct MergeReport {
    pub batches_applied: usize,
    pub batches_skipped: usize,
    pub players_updated: usize,
    pub battles_added: usize,
    pub new_players: Vec<String>,
}

/// Incorpora os lotes no consolidado: +1 batalha para cada jogador de cada lote.
pub fn merge_pending(audit: &mut AuditFile, batches: &[PendingBatch]) -> MergeReport {
    let mut report = MergeReport::default();
    let mut positions = index(&audit.entries);
    let mut applied: HashSet<String> = audit.applied_batch_ids.iter().map(|id| key(id)).collect();
    let mut touched = HashSet::new();

    let mut ordered: Vec<&PendingBatch> = batches.iter().collect();
    ordered.sort_by_key(|b| b.created_utc);

    for batch in ordered {
        // Se o processo caiu entre o push e a limpeza do pendente, o lote ja
        // pode estar contado.
        if !batch.batch_id.is_empty() && applied.contains(&key(&batch.batch_id)) {
            report.batches_skipped += 1;
            continue;
        }

        // Um lote e uma batalha: nome repetido no lote soma K/D/A, mas a
        // batalha continua sendo uma so.
        let mut counted = HashSet::new();

        for entry in &batch.entries {
            if entry.username.trim().is_empty() {
                continue;
            }

            let name = key(&entry.username);
            let first = counted.insert(name.clone());

            match positions.get(&name) {
                Some(&i) => {
                    let target = &mut audit.entries[i];
                    target.kills += entry.kills;
                    target.deaths += entry.deaths;
                    target.assists += entry.assists;
                    if first {
                        target.battles += 1;
                    }
                }
                None => {
                    audit.entries.push(AuditEntry {
                        username: entry.username.clone(),
                        kills: entry.kills,
                        deaths: entry.deaths,
                        assists: entry.assists,
                        battles: 1,
                        rank: String::new(),
                    });
                    positions.insert(name.clone(), audit.entries.len() - 1);
                    report.new_players.push(entry.username.clone());
                }
            }

            touched.insert(name);
            if first {
                report.battles_added += 1;
            }
        }

        report.batches_applied += 1;
        if !batch.batch_id.is_empty() {
            audit.applied_batch_ids.push(batch.batch_id.clone());
            applied.insert(key(&batch.batch_id));
        }
    }

    let excess = audit
        .applied_batch_ids
        .len()
        .saturating_sub(MAX_TRACKED_BATCH_IDS);
    audit.applied_batch_ids.drain(..excess);

    report.players_updated = touched.len();
    report
}

#[derive(Debug, Default)]
pub struct ImportReport {
    pub added: Vec<String>,
    pub conflicts: Vec<String>,
    pub skipped_rows: usize,
    pub overwritten: usize,
}

/// Semeia o arquivo a partir da planilha. Sem `overwrite`, quem ja existe nao
/// muda: o audit.json ja soma a planilha MAIS tudo que veio depois, e
/// sobrescrever faria os totais andarem para tras. Sem `include_kda`, a
/// planilha nao tem kills/deaths/assists e eles ficam intocados.
pub fn merge_import(
    audit: &mut AuditFile,
    imported: &[AuditEntry],
    overwrite: bool,
    include_kda: bool,
) -> ImportReport {
    let mut report = ImportReport::default();
    let mut positions = index(&audit.entries);

    for row in imported {
        if row.username.trim().is_empty() {
            report.skipped_rows += 1;
            continue;
        }

        if let Some(&i) = positions.get(&key(&row.username)) {
            let existing = &mut audit.entries[i];
            let kda_differs = existing.kills != row.kills
                || existing.deaths != row.deaths
                || existing.assists != row.assists;

            if overwrite {
                if include_kda {
                    existing.kills = row.kills;
                    existing.deaths = row.deaths;
                    existing.assists = row.assists;
                }
                existing.battles = row.battles;
                if !row.rank.trim().is_empty() {
                    existing.rank = row.rank.clone();
                }
                report.overwritten += 1;
            } else if existing.battles != row.battles || (include_kda && kda_differs) {
                report.conflicts.push(if include_kda {
                    format!(
                        "{}: planilha {}/{}/{} ({} bat.) vs armazenado {}/{}/{} ({} bat.)",
                        existing.username,
                        row.kills,
                        row.deaths,
                        row.assists,
                        row.battles,
                        existing.kills,
                        existing.deaths,
                        existing.assists,
                        existing.battles
                    )
                } else {
                    format!(
                        "{}: planilha {} bat. vs armazenado {} bat.",
                        existing.username, row.battles, existing.battles
                    )
                });
            }
            continue;
        }

        audit.entries.push(row.clone());
        positions.insert(key(&row.username), audit.entries.len() - 1);
        report.added.push(row.username.clone());
    }

    report
}

#[cfg(test)]
mod tests {
    use super::*;

    fn entry(name: &str, kills: i64, battles: i64) -> AuditEntry {
        AuditEntry {
            username: name.to_owned(),
            kills,
            battles,
            ..AuditEntry::default()
        }
    }

    fn batch(id: &str, entries: Vec<AuditEntry>) -> PendingBatch {
        PendingBatch {
            batch_id: id.to_owned(),
            entries,
            ..PendingBatch::default()
        }
    }

    #[test]
    fn le_o_json_gravado_pelo_bot_em_csharp() {
        let audit: AuditFile = serde_json::from_str(
            r#"{"version":1,"lastUpdatedUtc":"2026-10-01T12:00:00.1234567+00:00",
                "appliedBatchIds":["abc"],
                "entries":[{"username":"Sr.Miojao","kills":10,"deaths":4,"assists":3,"battles":4,"rank":"Private"}]}"#,
        )
        .unwrap();
        assert_eq!(audit.entries[0].kd_ratio(), "2.50");
        assert_eq!(audit.last_updated_utc.year(), 2026);

        let pending: PendingFile = serde_json::from_str(
            r#"{"batches":[{"batchId":"x","createdUtc":"0001-01-01T00:00:00+00:00",
                "submittedByUserId":1403164163869507725,"submittedByUsername":"a","entries":[],"rawLines":[]}]}"#,
        )
        .unwrap();
        assert_eq!(pending.batches[0].submitted_by_user_id, 1403164163869507725);
    }

    #[test]
    fn grava_com_os_nomes_de_campo_do_csharp() {
        let json = serde_json::to_string(&AuditFile::default()).unwrap();
        assert!(json.contains("\"lastUpdatedUtc\"") && json.contains("\"appliedBatchIds\""));
    }

    #[test]
    fn lote_conta_uma_batalha_por_jogador_mesmo_repetido() {
        let mut audit = AuditFile {
            entries: vec![entry("Ana", 5, 2)],
            ..AuditFile::default()
        };
        let batches = vec![
            batch(
                "1",
                vec![entry("ana", 1, 0), entry("ANA", 2, 0), entry("Bia", 3, 0)],
            ),
            batch("2", vec![entry("Bia", 1, 0), entry("bia", 1, 0)]),
        ];

        let report = merge_pending(&mut audit, &batches);

        assert_eq!(audit.entries[0].kills, 8);
        assert_eq!(audit.entries[0].battles, 3);
        assert_eq!(audit.entries[1].battles, 2);
        assert_eq!(audit.entries[1].kills, 5);
        assert_eq!(report.battles_added, 3);
        assert_eq!(report.new_players, vec!["Bia"]);
    }

    #[test]
    fn lote_ja_aplicado_nao_conta_de_novo() {
        let mut audit = AuditFile::default();
        let batches = vec![batch("x", vec![entry("Ana", 1, 0)])];
        merge_pending(&mut audit, &batches);
        let report = merge_pending(&mut audit, &batches);

        assert_eq!(report.batches_skipped, 1);
        assert_eq!(audit.entries[0].battles, 1);
    }

    #[test]
    fn importacao_nao_mexe_em_quem_existe_sem_sobrescrever() {
        let mut audit = AuditFile {
            entries: vec![entry("Ana", 50, 20)],
            ..AuditFile::default()
        };
        let report = merge_import(
            &mut audit,
            &[entry("ana", 0, 18), entry("Bia", 0, 3)],
            false,
            false,
        );

        assert_eq!(audit.entries[0].kills, 50);
        assert_eq!(audit.entries[0].battles, 20);
        assert_eq!(report.added, vec!["Bia"]);
        assert_eq!(report.conflicts.len(), 1);
    }

    #[test]
    fn busca_poe_quem_comeca_com_o_texto_primeiro() {
        let entries = vec![
            entry("Rodaz", 0, 0),
            entry("Odair", 0, 0),
            entry("Zé", 0, 0),
        ];
        let names: Vec<&str> = search(&entries, "OD", 5)
            .iter()
            .map(|e| e.username.as_str())
            .collect();
        assert_eq!(names, vec!["Odair", "Rodaz"]);
    }
}
