use std::{collections::HashSet, time::Duration};

use super::model::{AuditEntry, key};
use crate::{config::AuditConfig, http, sheet::parse_csv};

const DEADLINE: Duration = Duration::from_secs(60);

pub struct CsvImport {
    pub rows: Vec<AuditEntry>,
    pub skipped_rows: usize,
    /// Se a planilha traz kills/deaths/assists. Sem eles, a importacao nao
    /// pode compara-los nem sobrescreve-los.
    pub has_kda: bool,
}

/// Baixa a aba da auditoria. Tenta tres vezes: o export do Google as vezes
/// engasga, e uma falha isolada nao deveria derrubar a importacao.
pub async fn fetch(client: &reqwest::Client, config: &AuditConfig) -> Result<CsvImport, String> {
    let url = config
        .audit_csv_url
        .as_deref()
        .map(str::trim)
        .filter(|url| !url.is_empty() && !url.contains("GID_AQUI"))
        .ok_or("`audit.auditCsvUrl` não está configurado (o gid da aba ainda é um placeholder).")?;

    let download = async {
        let mut last = String::new();
        for attempt in 1..=3 {
            match http::fetch_text(client, url).await {
                Ok(text) => return Ok(text),
                Err(cause) => last = cause,
            }
            if attempt < 3 {
                tokio::time::sleep(Duration::from_millis(500 * attempt)).await;
            }
        }
        Err(last)
    };

    let text = tokio::time::timeout(DEADLINE, download)
        .await
        .map_err(|_| {
            "A planilha demorou demais para responder. Tente de novo em alguns minutos; \
             se continuar, verifique a conexão da máquina do bot."
                .to_owned()
        })?
        .map_err(|cause| format!("Não consegui baixar a planilha: {cause}"))?;

    parse(&text, config)
}

/// Separado do download: e a parte que depende do formato da planilha.
pub fn parse(text: &str, config: &AuditConfig) -> Result<CsvImport, String> {
    let columns = &config.csv_columns;
    let mut result = CsvImport {
        rows: Vec::new(),
        skipped_rows: 0,
        // E, e nao OU: com so uma das tres mapeada, sobrescrever zerava as outras.
        has_kda: columns.kills >= 0 && columns.deaths >= 0 && columns.assists >= 0,
    };
    let mut seen = HashSet::new();

    for cells in parse_csv(text)?.iter().skip(config.csv_header_rows) {
        let username = read_text(cells, columns.username);

        // A planilha pode repetir o jogador; a primeira linha vence.
        if username.is_empty() || !seen.insert(key(&username)) {
            result.skipped_rows += 1;
            continue;
        }

        result.rows.push(AuditEntry {
            username,
            kills: read_count(cells, columns.kills),
            deaths: read_count(cells, columns.deaths),
            assists: read_count(cells, columns.assists),
            battles: read_count(cells, columns.battles),
            rank: read_text(cells, columns.rank),
        });
    }

    Ok(result)
}

/// Indice negativo e coluna que a planilha nao tem.
fn read_text(cells: &[String], column: i32) -> String {
    usize::try_from(column)
        .ok()
        .and_then(|i| cells.get(i))
        .map_or_else(String::new, |cell| cell.trim().to_owned())
}

/// Tolera separador de milhar e espaco; o que nao for numero vira 0.
fn read_count(cells: &[String], column: i32) -> i64 {
    let raw: String = read_text(cells, column)
        .chars()
        .filter(|c| !matches!(c, '.' | ',' | ' '))
        .collect();
    raw.parse().ok().filter(|v: &i64| *v >= 0).unwrap_or(0)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn config(json: &str) -> AuditConfig {
        serde_json::from_str(json).unwrap()
    }

    #[test]
    fn le_a_planilha_atual_sem_kda() {
        let csv = "NOME DE USUARIO,PATENTE,BATALHAS,ULTIMO DIA\n\
                   \"sr.miojao\n\",Private,\"1.204\",x\n\
                   Ana,,abc\n\
                   ana,Regular,9\n\
                   ,Private,3\n";
        let import = parse(csv, &config("{}")).unwrap();

        assert!(!import.has_kda);
        assert_eq!(import.rows.len(), 2);
        assert_eq!(import.rows[0].username, "sr.miojao");
        assert_eq!(import.rows[0].battles, 1204);
        assert_eq!(import.rows[1].battles, 0);
        assert_eq!(import.skipped_rows, 2);
    }

    #[test]
    fn kda_so_conta_com_as_tres_colunas() {
        let parcial = config(r#"{"csvColumns":{"kills":3}}"#);
        assert!(!parse("a,b,c,d\n", &parcial).unwrap().has_kda);
        let completa = config(r#"{"csvColumns":{"kills":3,"deaths":4,"assists":5}}"#);
        assert!(parse("a,b,c,d\n", &completa).unwrap().has_kda);
    }
}
