use std::collections::HashSet;

use chrono::Utc;

use tokio::sync::Mutex;

use super::{
    github::{GitHub, PushError, resolve_token},
    model::{AuditFile, MergeReport, PendingBatch, key, merge_pending},
    store::{AuditStore, to_json},
};
use crate::{Data, config::Config};

/// O que a publicacao usa. O /audit-push tira do Data; o dashboard, das copias
/// que ele guarda.
pub struct Deps<'a> {
    pub audit: &'a AuditStore,
    pub config: &'a Config,
    pub http: &'a reqwest::Client,
    pub github_token: &'a Mutex<Option<String>>,
}

impl<'a> From<&'a Data> for Deps<'a> {
    fn from(data: &'a Data) -> Self {
        Self {
            audit: &data.audit,
            config: &data.config,
            http: &data.http,
            github_token: &data.github_token,
        }
    }
}

pub struct Outcome {
    pub dry_run: bool,
    pub has_pending: bool,
    pub nothing_to_do: bool,
    pub report: MergeReport,
    pub total_entries: usize,
    pub pending_batches: usize,
    pub audit_sha: Option<String>,
    pub audit_commit_url: Option<String>,
    pub archive_commit_url: Option<String>,
    pub archive_path: Option<String>,
}

impl Outcome {
    /// Frase para o historico da auditoria.
    pub fn log_details(&self) -> String {
        let sha = self
            .audit_sha
            .as_deref()
            .map_or("-", |sha| &sha[..sha.len().min(7)]);

        if self.has_pending {
            format!(
                "Consolidou {} lote(s) (+{} batalha(s), {} jogador(es)) e publicou no GitHub — commit `{sha}`.",
                self.report.batches_applied, self.report.battles_added, self.report.players_updated
            )
        } else {
            format!(
                "Publicou o arquivo local no GitHub sem lotes pendentes ({} jogador(es)) — commit `{sha}`.",
                self.total_entries
            )
        }
    }
}

pub struct Failure {
    pub step: &'static str,
    pub error: PushError,
}

fn at<E: Into<PushError>>(step: &'static str) -> impl FnOnce(E) -> Failure {
    move |error| Failure {
        step,
        error: error.into(),
    }
}

fn non_blank(value: Option<&String>) -> Option<&str> {
    value.map(|v| v.trim()).filter(|v| !v.is_empty())
}

/// Consolida os lotes pendentes e publica no GitHub. A ORDEM dos passos e o
/// que garante que uma falha no meio deixa um estado do qual o proximo push se
/// recupera sozinho, sem contar batalha em dobro.
pub async fn publish(data: Deps<'_>, dry_run: bool) -> Result<Outcome, Failure> {
    let result = run(&data, dry_run).await;

    // 401 quase sempre e token vencido: esquecer faz a proxima tentativa
    // reler a credencial em vez de repetir a falha.
    if let Err(Failure {
        error: PushError::Unauthorized,
        ..
    }) = &result
    {
        *data.github_token.lock().await = None;
    }

    result
}

async fn run(data: &Deps<'_>, dry_run: bool) -> Result<Outcome, Failure> {
    let (audit, pending) = data.audit.read_both().await.map_err(at("leitura local"))?;

    // Sem lote ainda pode haver trabalho: import, edit e setranks mexem direto
    // no consolidado, e so chegam ao GitHub por aqui.
    let has_pending = !pending.batches.is_empty();

    // Consolida numa copia: nada e gravado enquanto o GitHub nao aceitar.
    let mut merged = audit.clone();
    let report = if has_pending {
        merge_pending(&mut merged, &pending.batches)
    } else {
        MergeReport::default()
    };

    let mut outcome = Outcome {
        dry_run,
        has_pending,
        nothing_to_do: false,
        report,
        total_entries: merged.entries.len(),
        pending_batches: pending.batches.len(),
        audit_sha: None,
        audit_commit_url: None,
        archive_commit_url: None,
        archive_path: None,
    };
    if dry_run {
        return Ok(outcome);
    }

    let step = "resolução das credenciais";
    let config = data.config.audit.as_ref().ok_or_else(|| {
        at(step)(PushError::Config(
            "A seção `audit` não existe no config.jsonc.".to_owned(),
        ))
    })?;
    let (Some(owner), Some(repo)) = (
        non_blank(config.github_owner.as_ref()),
        non_blank(config.github_repo.as_ref()),
    ) else {
        return Err(at(step)(PushError::Config(
            "`audit.githubOwner` e `audit.githubRepo` precisam estar configurados.".to_owned(),
        )));
    };
    let token = resolve_token(data.github_token, config)
        .await
        .map_err(at(step))?;

    let branch = non_blank(config.github_branch.as_ref()).unwrap_or("audit-data");
    let audit_path = non_blank(config.audit_file_path.as_ref()).unwrap_or("audit.json");
    let archive_dir = non_blank(config.archive_directory.as_ref()).unwrap_or("audits");
    let github = GitHub::new(data.http.clone(), token, owner, repo, branch);

    github
        .ensure_branch()
        .await
        .map_err(at("verificação da branch"))?;

    let today = Utc::now().format("%Y-%m-%d").to_string();

    if has_pending {
        // O lote bruto vai PRIMEIRO: se o passo seguinte falhar, ele ja esta
        // salvo na branch e pode ser reprocessado.
        let path = github
            .archive_path(archive_dir, &today)
            .await
            .map_err(at("resolução do arquivo de arquivamento"))?;
        let message = format!(
            "audit: arquivo de lote {today} ({} lote(s), {} jogador(es))",
            pending.batches.len(),
            outcome.report.players_updated
        );
        let sha = github
            .put_file(&path, &to_json(&pending), &message)
            .await
            .map_err(at("publicação do arquivamento"))?;
        outcome.archive_commit_url = Some(github.commit_url(&sha));
        outcome.archive_path = Some(path);
    } else {
        // Sem lote, so publica se o GitHub estiver atrasado: senao seria um
        // commit vazio a cada execucao.
        let published = github
            .file_content(audit_path)
            .await
            .map_err(at("comparação com o GitHub"))?
            .and_then(|text| serde_json::from_str::<AuditFile>(&text).ok());

        if published.is_some_and(|p| same_data(&p, &merged)) {
            outcome.nothing_to_do = true;
            return Ok(outcome);
        }
    }

    let message = if has_pending {
        format!(
            "audit: consolidação {today} (+{} batalha(s))",
            outcome.report.battles_added
        )
    } else {
        format!(
            "audit: sincronização do arquivo local {today} ({} jogador(es))",
            merged.entries.len()
        )
    };
    let sha = github
        .put_file(audit_path, &to_json(&merged), &message)
        .await
        .map_err(at("publicação da auditoria consolidada"))?;
    outcome.audit_commit_url = Some(github.commit_url(&sha));
    outcome.audit_sha = Some(sha);

    if has_pending {
        // Reaplica sobre o que esta gravado AGORA, em vez de gravar a copia:
        // um /audit-edit feito durante a ida ao GitHub seria perdido. O
        // appliedBatchIds impede de contar em dobro.
        let consumed: HashSet<String> = pending.batches.iter().map(|b| key(&b.batch_id)).collect();
        let version = merged.version;

        data.audit
            .update(move |stored_audit, stored_pending| {
                let to_apply: Vec<PendingBatch> = stored_pending
                    .batches
                    .iter()
                    .filter(|b| consumed.contains(&key(&b.batch_id)))
                    .cloned()
                    .collect();
                merge_pending(stored_audit, &to_apply);
                stored_audit.version = version;
                stored_pending
                    .batches
                    .retain(|b| !consumed.contains(&key(&b.batch_id)));
            })
            .await
            .map_err(at("gravação local"))?;
    }

    Ok(outcome)
}

/// Compara ignorando lastUpdatedUtc: o carimbo muda a cada gravacao local e
/// sozinho nao justifica um commit.
fn same_data(a: &AuditFile, b: &AuditFile) -> bool {
    let mut a = a.clone();
    a.last_updated_utc = b.last_updated_utc;
    a == *b
}
