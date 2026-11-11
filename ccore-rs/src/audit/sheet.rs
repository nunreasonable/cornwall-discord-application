//! Manda os lotes do /audit-push para a planilha do regimento, pelo app da web
//! do Apps Script (sheets/ccore.gs). La eles entram na EventLogs do mesmo jeito
//! que o EVENT FORM grava, e as abas calculadas se atualizam sozinhas.

use std::time::Duration;

use chrono::FixedOffset;
use serde::Deserialize;
use serde_json::json;

use super::model::PendingBatch;
use crate::embeds::truncate;

/// O Apps Script pode demorar: ele le a EventLogs inteira para o eventID.
const TIMEOUT: Duration = Duration::from_secs(60);

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Reply {
    ok: bool,
    #[serde(default)]
    appended: usize,
    #[serde(default)]
    added_to_roster: Vec<String>,
    error: Option<String>,
    spreadsheet_name: Option<String>,
}

pub struct Sent {
    pub rows: usize,
    pub added_to_roster: Vec<String>,
}

/// Data do lote no fuso do regimento (Brasilia, -03:00, sem horario de verao
/// desde 2019): um lote colado as 22h nao pode cair no dia seguinte.
fn local_date(batch: &PendingBatch) -> String {
    let brasilia = FixedOffset::west_opt(3 * 3600).expect("offset valido");
    batch
        .created_utc
        .with_timezone(&brasilia)
        .format("%Y-%m-%d")
        .to_string()
}

fn payload(secret: &str, spreadsheet: Option<&str>, batches: &[PendingBatch]) -> serde_json::Value {
    let batches: Vec<_> = batches
        .iter()
        .map(|b| {
            let rows: Vec<_> = b
                .entries
                .iter()
                .filter(|e| !e.username.trim().is_empty())
                .map(|e| json!([e.username, e.kills, e.deaths, e.assists]))
                .collect();
            json!({ "batchId": b.batch_id, "date": local_date(b), "rows": rows })
        })
        .collect();
    json!({ "secret": secret, "spreadsheetId": spreadsheet, "batches": batches })
}

/// O Err e a frase pronta para o staff.
pub async fn send(
    http: &reqwest::Client,
    url: &str,
    secret: &str,
    spreadsheet: Option<&str>,
    batches: &[PendingBatch],
) -> Result<Sent, String> {
    // O app da web responde com um redirect para o googleusercontent; o
    // cliente segue sozinho.
    let response = http
        .post(url)
        .timeout(TIMEOUT)
        .json(&payload(secret, spreadsheet, batches))
        .send()
        .await
        .map_err(|e| format!("Não consegui falar com a planilha: {}", e.without_url()))?;

    let status = response.status();
    let text = response
        .text()
        .await
        .map_err(|e| format!("A planilha não respondeu direito: {}", e.without_url()))?;

    // Erro do Apps Script volta como pagina HTML, e nao como JSON.
    let reply: Reply = serde_json::from_str(&text).map_err(|_| {
        format!(
            "Resposta inesperada da planilha (HTTP {status}). Confira a URL /exec e se a implantação está ativa: {}",
            truncate(text.trim(), 200)
        )
    })?;

    if !reply.ok {
        let place = reply
            .spreadsheet_name
            .map(|name| format!(" (script em \"{name}\")"))
            .unwrap_or_default();
        return Err(format!(
            "A planilha recusou o envio: {}{place}",
            reply.error.as_deref().unwrap_or("sem motivo")
        ));
    }

    Ok(Sent {
        rows: reply.appended,
        added_to_roster: reply.added_to_roster,
    })
}

#[cfg(test)]
mod tests {
    use chrono::{TimeZone, Utc};

    use super::*;
    use crate::audit::model::AuditEntry;

    #[test]
    fn data_do_lote_segue_brasilia() {
        let batch = PendingBatch {
            batch_id: "x".to_owned(),
            created_utc: Utc.with_ymd_and_hms(2026, 11, 12, 1, 30, 0).unwrap(),
            entries: vec![
                AuditEntry {
                    username: "Ana".to_owned(),
                    kills: 3,
                    deaths: 1,
                    assists: 2,
                    ..AuditEntry::default()
                },
                AuditEntry::default(),
            ],
            ..PendingBatch::default()
        };

        let body = payload("s", Some("abc"), &[batch]);
        assert_eq!(body["batches"][0]["date"], "2026-11-11");
        assert_eq!(body["batches"][0]["rows"], json!([["Ana", 3, 1, 2]]));
        assert_eq!(body["secret"], "s");
        assert_eq!(body["spreadsheetId"], "abc");
    }
}
