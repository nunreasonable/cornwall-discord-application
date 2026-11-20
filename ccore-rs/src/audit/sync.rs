//! Traz a planilha para o audit.json sozinho, de tempos em tempos. O dashboard
//! e o /audit-check leem o arquivo local, e a planilha muda fora do bot
//! (patentes, correcoes, o EVENT FORM).

use std::{sync::Arc, time::Duration};

use tracing::{info, warn};

use super::{
    import,
    model::sync_with_sheet,
    store::{Action, AuditStore},
};
use crate::config::Config;

/// Espera o bot conectar antes da primeira leitura.
const FIRST_DELAY: Duration = Duration::from_secs(30);

pub fn spawn(http: reqwest::Client, config: Arc<Config>, audit: Arc<AuditStore>) {
    let Some(audit_config) = config.audit.as_ref() else {
        return;
    };
    if audit_config.sheet_sync_minutes == 0 {
        info!("sincronizacao da planilha desligada (sheetSyncMinutes = 0)");
        return;
    }
    let every = Duration::from_secs(audit_config.sheet_sync_minutes * 60);
    info!(
        "sincronizacao da planilha a cada {} min",
        audit_config.sheet_sync_minutes
    );
    tokio::spawn(async move {
        tokio::time::sleep(FIRST_DELAY).await;
        loop {
            if let Some(audit_config) = config.audit.as_ref() {
                sync_once(&http, audit_config, &audit).await;
            }
            tokio::time::sleep(every).await;
        }
    });
}

async fn sync_once(
    http: &reqwest::Client,
    config: &crate::config::AuditConfig,
    audit: &AuditStore,
) {
    let csv = match import::fetch(http, config).await {
        Ok(csv) => csv,
        Err(cause) => {
            warn!("sincronizacao da planilha: {cause}");
            return;
        }
    };
    let local = match audit.read_audit().await {
        Ok(local) => local.entries.len(),
        Err(cause) => {
            warn!("sincronizacao da planilha: {cause}");
            return;
        }
    };
    // Uma aba vazia ou cortada no meio apagaria o efetivo inteiro.
    if csv.rows.is_empty() || csv.rows.len() * 2 < local {
        warn!(
            "sincronizacao da planilha ignorada: ela trouxe {} jogador(es) e o arquivo tem {local}",
            csv.rows.len()
        );
        return;
    }
    let report = match audit
        .update(|stored, _| sync_with_sheet(stored, &csv.rows, csv.has_kda))
        .await
    {
        Ok(report) => report,
        Err(cause) => {
            warn!("sincronizacao da planilha: {cause}");
            return;
        }
    };
    if !report.changed() {
        return;
    }
    let details = format!(
        "Sincronizou com a planilha: {} adicionado(s), {} removido(s), {} atualizado(s).",
        report.added.len(),
        report.removed.len(),
        report.updated
    );
    info!("{details}");
    audit
        .record_as(0, "planilha", Action::Import, &details)
        .await;
}
