use std::{
    os::unix::fs::PermissionsExt,
    path::{Path, PathBuf},
};

use chrono::{DateTime, Utc};
use poise::serenity_prelude as serenity;
use serde::{Deserialize, Serialize, de::DeserializeOwned};
use tokio::{io::AsyncWriteExt, sync::Mutex};
use tracing::warn;

use super::model::{AuditFile, PendingFile};
use crate::embeds::truncate;

const AUDIT: &str = "audit.json";
const PENDING: &str = "audit_pending.json";
const LOG: &str = "audit_log.json";
const MAX_LOG_ENTRIES: usize = 2000;

/// Acoes do historico. O codigo gravado e o mesmo do bot em C#.
#[derive(Clone, Copy, Debug, PartialEq, Eq, poise::ChoiceParameter)]
pub enum Action {
    #[name = "Registro de batalha (/audit-add)"]
    Add,
    #[name = "Edição de jogador (/audit-edit)"]
    Edit,
    #[name = "Remoção de jogador (/audit-edit)"]
    Remove,
    #[name = "Definição de cargos (/audit-setranks)"]
    SetRanks,
    #[name = "Importação da planilha (/audit-import)"]
    Import,
    #[name = "Publicação no GitHub (/audit-push)"]
    Push,
}

impl Action {
    pub fn code(self) -> &'static str {
        match self {
            Self::Add => "ADD",
            Self::Edit => "EDIT",
            Self::Remove => "REMOVE",
            Self::SetRanks => "SETRANKS",
            Self::Import => "IMPORT",
            Self::Push => "PUSH",
        }
    }

    pub fn label(code: &str) -> String {
        let label = match code.to_uppercase().as_str() {
            "ADD" => "batalha registrada",
            "EDIT" => "jogador editado",
            "REMOVE" => "jogador removido",
            "SETRANKS" => "cargos definidos",
            "IMPORT" => "planilha importada",
            "PUSH" => "publicado no GitHub",
            _ => return code.to_lowercase(),
        };
        label.to_owned()
    }
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct LogEntry {
    pub timestamp_utc: DateTime<Utc>,
    pub user_id: u64,
    pub username: String,
    pub action: String,
    pub details: String,
}

#[derive(Default, Serialize, Deserialize)]
#[serde(default)]
struct LogFile {
    entries: Vec<LogEntry>,
}

/// Os arquivos da auditoria em data/. Tudo passa pelo mesmo lock, entao um
/// /audit-add e um /audit-push ao mesmo tempo nao se atropelam.
pub struct AuditStore {
    dir: PathBuf,
    lock: Mutex<()>,
}

impl AuditStore {
    pub fn new(dir: impl Into<PathBuf>) -> Self {
        Self {
            dir: dir.into(),
            lock: Mutex::default(),
        }
    }

    pub async fn read_audit(&self) -> Result<AuditFile, String> {
        let _guard = self.lock.lock().await;
        read_json(&self.dir.join(AUDIT)).await
    }

    pub async fn read_both(&self) -> Result<(AuditFile, PendingFile), String> {
        let _guard = self.lock.lock().await;
        Ok((
            read_json(&self.dir.join(AUDIT)).await?,
            read_json(&self.dir.join(PENDING)).await?,
        ))
    }

    /// A unica forma de escrever: le os dois, aplica a mudanca e grava so o
    /// que mudou. Sem isso, uma edicao cancelada mexia no lastUpdatedUtc e o
    /// /audit-push achava que havia novidade.
    pub async fn update<T>(
        &self,
        mutate: impl FnOnce(&mut AuditFile, &mut PendingFile) -> T,
    ) -> Result<T, String> {
        let _guard = self.lock.lock().await;
        let mut audit: AuditFile = read_json(&self.dir.join(AUDIT)).await?;
        let mut pending: PendingFile = read_json(&self.dir.join(PENDING)).await?;
        let audit_before = to_json(&audit);
        let pending_before = to_json(&pending);

        let result = mutate(&mut audit, &mut pending);

        if to_json(&audit) != audit_before {
            audit.last_updated_utc = Utc::now();
            write_json(&self.dir, AUDIT, &audit).await?;
        }
        if to_json(&pending) != pending_before {
            write_json(&self.dir, PENDING, &pending).await?;
        }
        Ok(result)
    }

    /// Registra quem fez o que. Nunca derruba o comando: falha vira log.
    pub async fn record(&self, user: &serenity::User, action: Action, details: &str) {
        let entry = LogEntry {
            timestamp_utc: Utc::now(),
            user_id: user.id.get(),
            username: user.name.clone(),
            action: action.code().to_owned(),
            // Vai dentro de um embed: uma entrada gigante estragaria a pagina.
            details: truncate(details, 300),
        };

        let _guard = self.lock.lock().await;
        let result = async {
            let mut log: LogFile = read_json(&self.dir.join(LOG)).await?;
            log.entries.push(entry);
            let excess = log.entries.len().saturating_sub(MAX_LOG_ENTRIES);
            log.entries.drain(..excess);
            write_json(&self.dir, LOG, &log).await
        }
        .await;

        if let Err(cause) = result {
            warn!("falha ao registrar no historico da auditoria: {cause}");
        }
    }

    /// O historico inteiro, do mais antigo para o mais recente.
    pub async fn read_log(&self) -> Result<Vec<LogEntry>, String> {
        let _guard = self.lock.lock().await;
        let log: LogFile = read_json(&self.dir.join(LOG)).await?;
        Ok(log.entries)
    }
}

/// Como vai para o disco e para o GitHub: indentado, igual ao bot em C#.
pub fn to_json<T: Serialize>(value: &T) -> String {
    serde_json::to_string_pretty(value).unwrap_or_default()
}

/// Arquivo que nao existe e vazio. Arquivo ilegivel e ERRO: devolver vazio
/// faria a proxima gravacao apagar o historico inteiro.
async fn read_json<T: DeserializeOwned + Default>(path: &Path) -> Result<T, String> {
    let text = match tokio::fs::read_to_string(path).await {
        Ok(text) => text,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(T::default()),
        Err(e) => return Err(format!("Não consegui ler `{}`: {e}", path.display())),
    };

    let text = text.trim_start_matches('\u{feff}');
    if text.trim().is_empty() {
        return Ok(T::default());
    }

    serde_json::from_str(text).map_err(|e| {
        format!(
            "`{}` está ilegível ({e}). Nada foi gravado: corrija ou restaure o arquivo.",
            path.display()
        )
    })
}

/// Grava num .tmp e renomeia: uma queda no meio nao trunca o arquivo. 0600
/// porque ha IDs e nomes de usuario dentro.
async fn write_json<T: Serialize>(dir: &Path, name: &str, value: &T) -> Result<(), String> {
    let path = dir.join(name);
    let tmp = dir.join(format!("{name}.tmp"));
    let fail = |e: std::io::Error| format!("Não consegui gravar `{}`: {e}", path.display());

    tokio::fs::create_dir_all(dir).await.map_err(fail)?;
    let mut file = tokio::fs::File::create(&tmp).await.map_err(fail)?;
    file.write_all(to_json(value).as_bytes())
        .await
        .map_err(fail)?;
    file.sync_all().await.map_err(fail)?;
    tokio::fs::set_permissions(&tmp, std::fs::Permissions::from_mode(0o600))
        .await
        .map_err(fail)?;
    tokio::fs::rename(&tmp, &path).await.map_err(fail)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp_store(name: &str) -> (AuditStore, PathBuf) {
        let dir = std::env::temp_dir().join(format!("ccore-audit-{name}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        (AuditStore::new(&dir), dir)
    }

    #[tokio::test]
    async fn arquivo_ilegivel_nao_e_sobrescrito() {
        let (store, dir) = temp_store("ilegivel");
        std::fs::write(dir.join(AUDIT), "{ isto nao e json").unwrap();

        let result = store.update(|audit, _| audit.version = 2).await;

        assert!(result.is_err());
        assert_eq!(
            std::fs::read_to_string(dir.join(AUDIT)).unwrap(),
            "{ isto nao e json"
        );
        let _ = std::fs::remove_dir_all(dir);
    }

    #[tokio::test]
    async fn so_grava_o_que_mudou() {
        let (store, dir) = temp_store("mudou");

        store.update(|_, _| ()).await.unwrap();
        assert!(!dir.join(AUDIT).exists());

        store
            .update(|_, pending| pending.batches.push(Default::default()))
            .await
            .unwrap();
        assert!(dir.join(PENDING).exists() && !dir.join(AUDIT).exists());

        let mode = std::fs::metadata(dir.join(PENDING))
            .unwrap()
            .permissions()
            .mode();
        assert_eq!(mode & 0o777, 0o600);
        let _ = std::fs::remove_dir_all(dir);
    }
}
