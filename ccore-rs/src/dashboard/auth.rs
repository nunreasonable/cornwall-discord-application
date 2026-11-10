//! O config/dashboard_auth.json e os dois arquivos que moram ao lado dele, no
//! mesmo formato que o bot em C# lia e gravava.

use std::{
    os::unix::fs::PermissionsExt,
    path::{Path, PathBuf},
    time::{Duration, SystemTime},
};

use chrono::{DateTime, Utc};
use json_comments::StripComments;
use poise::serenity_prelude as serenity;
use serde::{Deserialize, Deserializer, Serialize, de::DeserializeOwned};
use tokio::{io::AsyncWriteExt, sync::Mutex};
use tracing::warn;

pub const AUTH_PATH: &str = "config/dashboard_auth.json";
const WHITELIST: &str = "dashboard_whitelisted_users.json";
const AUDIT_LOG: &str = "dashboard_audit_log.json";
const KEEP_AUDIT: usize = 500;
pub const DEFAULT_LISTEN: &str = "http://127.0.0.1:5056/";

/// O nucleo do dashboard_auth.json. A ordem dos campos e a do C#, para o
/// arquivo regravado continuar igual ao que ja existe.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct CoreConfig {
    #[serde(deserialize_with = "or_default")]
    pub listen_url: String,
    /// Vazio de proposito: o curinga e recusado, e vazio vira as duas origens reais.
    #[serde(deserialize_with = "or_default")]
    pub allowed_origins: Vec<String>,
    /// Segredo que o Worker da Cloudflare manda no X-Ccore-Tunnel.
    #[serde(deserialize_with = "or_default")]
    pub tunnel_secret: String,
    #[serde(deserialize_with = "id")]
    pub guild_id: u64,
    #[serde(deserialize_with = "ids")]
    pub level4_user_ids: Vec<u64>,
    /// Piso de nivel 2 por ID; cargo ainda pode subir, nunca descer.
    #[serde(deserialize_with = "ids")]
    pub level2_user_ids: Vec<u64>,
    #[serde(deserialize_with = "ids")]
    pub level3_role_ids: Vec<u64>,
    #[serde(deserialize_with = "ids")]
    pub level2_role_ids: Vec<u64>,
    #[serde(deserialize_with = "ids")]
    pub level1_role_ids: Vec<u64>,
    #[serde(deserialize_with = "ids")]
    pub regiment_role_ids: Vec<u64>,
    /// Unicos cargos que /api/roles/* pode tocar. Vazio recusa tudo.
    #[serde(deserialize_with = "ids")]
    pub grantable_role_ids: Vec<u64>,
    /// Canais liberados para o nivel 1 em /api/messages/send. Vazio nao restringe.
    #[serde(deserialize_with = "ids")]
    pub allowed_send_channel_ids: Vec<u64>,
    #[serde(deserialize_with = "or_default")]
    pub link_code_lifetime_minutes: i64,
    #[serde(deserialize_with = "or_default")]
    pub session_lifetime_minutes: i64,
}

impl Default for CoreConfig {
    fn default() -> Self {
        Self {
            listen_url: DEFAULT_LISTEN.to_owned(),
            allowed_origins: Vec::new(),
            tunnel_secret: String::new(),
            guild_id: 0,
            level4_user_ids: Vec::new(),
            level2_user_ids: Vec::new(),
            level3_role_ids: Vec::new(),
            level2_role_ids: Vec::new(),
            level1_role_ids: Vec::new(),
            regiment_role_ids: Vec::new(),
            grantable_role_ids: Vec::new(),
            allowed_send_channel_ids: Vec::new(),
            link_code_lifetime_minutes: 10,
            session_lifetime_minutes: 60,
        }
    }
}

#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct WhitelistedUser {
    pub user_id: u64,
    pub username: String,
    pub permission_level: i32,
    pub first_whitelisted_at: DateTime<Utc>,
    pub last_validated_at: DateTime<Utc>,
}

#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct AuditEntry {
    pub timestamp_utc: DateTime<Utc>,
    pub actor_user_id: u64,
    pub actor_username: String,
    pub actor_avatar_url: String,
    pub action: String,
    pub details: String,
}

/// O bot em C# guardava whitelist e log dentro do arquivo principal.
#[derive(Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct Legacy {
    whitelisted_users: Option<Vec<WhitelistedUser>>,
    audit_log: Option<Vec<AuditEntry>>,
}

/// Null no JSON vale o padrao, como no Newtonsoft.
fn or_default<'de, D: Deserializer<'de>, T: Deserialize<'de> + Default>(
    d: D,
) -> Result<T, D::Error> {
    Ok(Option::<T>::deserialize(d)?.unwrap_or_default())
}

fn to_id(value: &serde_json::Value) -> Option<u64> {
    match value {
        serde_json::Value::Number(n) => n.as_u64(),
        serde_json::Value::String(s) => s.trim().parse().ok(),
        _ => None,
    }
}

/// Snowflake como numero ou texto: config editado a mao aparece dos dois jeitos.
fn id<'de, D: Deserializer<'de>>(d: D) -> Result<u64, D::Error> {
    Ok(Option::<serde_json::Value>::deserialize(d)?
        .as_ref()
        .and_then(to_id)
        .unwrap_or(0))
}

fn ids<'de, D: Deserializer<'de>>(d: D) -> Result<Vec<u64>, D::Error> {
    Ok(match Option::<serde_json::Value>::deserialize(d)? {
        Some(serde_json::Value::Array(items)) => items.iter().filter_map(to_id).collect(),
        _ => Vec::new(),
    })
}

#[derive(Clone, Debug, Default)]
pub struct AuthConfig {
    pub core: CoreConfig,
    pub whitelisted_users: Vec<WhitelistedUser>,
    pub audit_log: Vec<AuditEntry>,
}

type Stamps = [Option<SystemTime>; 3];

#[derive(Default)]
struct State {
    cache: Option<(Stamps, AuthConfig)>,
    /// A ultima leitura do arquivo principal falhou: em memoria ele esta vazio,
    /// mas o disco ainda tem a config real. Enquanto isso o nucleo NAO e
    /// regravado, senao uma virgula sobrando apagaria guildId e tunnelSecret.
    degraded: bool,
}

pub struct Resolution {
    pub level: i32,
    pub lookup_failed: bool,
}

pub struct AuthStore {
    path: PathBuf,
    whitelist: PathBuf,
    audit: PathBuf,
    state: Mutex<State>,
    /// Serializa ler-mudar-gravar da whitelist e do log.
    edit: Mutex<()>,
}

impl AuthStore {
    pub fn new(path: impl Into<PathBuf>) -> Self {
        let path = path.into();
        let dir = path
            .parent()
            .filter(|d| !d.as_os_str().is_empty())
            .unwrap_or(Path::new("."))
            .to_path_buf();
        Self {
            whitelist: dir.join(WHITELIST),
            audit: dir.join(AUDIT_LOG),
            path,
            state: Mutex::default(),
            edit: Mutex::default(),
        }
    }

    fn stamps(&self) -> Stamps {
        let at = |p: &Path| std::fs::metadata(p).and_then(|m| m.modified()).ok();
        [at(&self.path), at(&self.whitelist), at(&self.audit)]
    }

    /// Cache pelo carimbo dos tres arquivos: quase toda rota le a config, e
    /// qualquer gravacao (nossa ou a mao) invalida sozinha.
    pub async fn read(&self) -> AuthConfig {
        let mut state = self.state.lock().await;
        let stamps = self.stamps();
        if let Some((cached_at, config)) = &state.cache
            && *cached_at == stamps
        {
            return config.clone();
        }

        let mut legacy = Legacy::default();
        let mut rewrite_core = false;
        let core = match tokio::fs::read_to_string(&self.path).await {
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => {
                // Primeira execucao: nao ha config real para preservar.
                state.degraded = false;
                let core = CoreConfig::default();
                if let Err(cause) = write_json(&self.path, &core).await {
                    warn!("dashboard: {cause}");
                }
                core
            }
            Err(e) => {
                warn!(
                    "dashboard: {} ilegivel ({e}); seguindo com o padrao, sem regravar o arquivo",
                    self.path.display()
                );
                state.degraded = true;
                CoreConfig::default()
            }
            Ok(text) => match parse_core(&text) {
                Ok((core, old)) => {
                    rewrite_core =
                        text.contains("\"whitelistedUsers\"") || text.contains("\"auditLog\"");
                    legacy = old;
                    state.degraded = false;
                    core
                }
                Err(e) => {
                    // O padrao tem allowedOrigins e grantableRoleIds vazios: erra
                    // fechando, nunca com permissao ampla.
                    warn!(
                        "dashboard: {} ilegivel ({e}); seguindo com o padrao. O arquivo NAO foi sobrescrito e a gravacao do nucleo fica suspensa ate ele ser corrigido",
                        self.path.display()
                    );
                    state.degraded = true;
                    CoreConfig::default()
                }
            },
        };

        let whitelisted_users = read_aux(
            &self.whitelist,
            legacy.whitelisted_users.unwrap_or_default(),
        )
        .await;
        let audit_log = read_aux(&self.audit, legacy.audit_log.unwrap_or_default()).await;

        if rewrite_core && let Err(cause) = write_json(&self.path, &core).await {
            warn!("dashboard: {cause}");
        }

        let config = AuthConfig {
            core,
            whitelisted_users,
            audit_log,
        };
        // Carimbo de DEPOIS: a migracao acima pode ter regravado o principal.
        state.cache = Some((self.stamps(), config.clone()));
        config
    }

    async fn write(&self, config: &AuthConfig) -> Result<(), String> {
        let mut state = self.state.lock().await;
        // Invalida antes de tudo: uma gravacao que falha no meio nao pode
        // deixar no cache algo que nunca chegou ao disco.
        state.cache = None;
        if state.degraded {
            warn!(
                "dashboard: nucleo degradado; {} preservado, so whitelist e auditoria gravados",
                self.path.display()
            );
        } else {
            write_json(&self.path, &config.core).await?;
        }
        write_json(&self.whitelist, &config.whitelisted_users).await?;
        write_json(&self.audit, &config.audit_log).await
    }

    /// Quem ja entrou no painel. So registro: a autorizacao vem do Discord.
    pub async fn upsert_whitelist(
        &self,
        user_id: u64,
        username: &str,
        level: i32,
    ) -> Result<(), String> {
        let _edit = self.edit.lock().await;
        let mut config = self.read().await;
        let now = Utc::now();
        match config
            .whitelisted_users
            .iter_mut()
            .find(|u| u.user_id == user_id)
        {
            Some(user) => {
                user.username = username.to_owned();
                user.permission_level = level;
                user.last_validated_at = now;
            }
            None => config.whitelisted_users.push(WhitelistedUser {
                user_id,
                username: username.to_owned(),
                permission_level: level,
                first_whitelisted_at: now,
                last_validated_at: now,
            }),
        }
        self.write(&config).await
    }

    pub async fn append_audit(&self, entry: AuditEntry) -> Result<(), String> {
        let _edit = self.edit.lock().await;
        let mut config = self.read().await;
        config.audit_log.push(entry);
        keep_newest(&mut config.audit_log, KEEP_AUDIT);
        self.write(&config).await
    }

    /// Do mais recente para o mais antigo.
    pub async fn audit(&self, take: usize) -> Vec<AuditEntry> {
        let mut log = self.read().await.audit_log;
        log.sort_by_key(|e| std::cmp::Reverse(e.timestamp_utc));
        log.truncate(take);
        log
    }

    /// Nivel do usuario no painel: 4 por ID, piso 2 por ID, o resto pelos
    /// cargos no servidor. O Discord falhando tres vezes vira `lookup_failed`.
    pub async fn resolve_level(&self, ctx: &serenity::Context, user_id: u64) -> Resolution {
        let core = self.read().await.core;
        if core.level4_user_ids.contains(&user_id) || core.guild_id == 0 || user_id == 0 {
            return Resolution {
                level: level_for(&core, user_id, None),
                lookup_failed: false,
            };
        }

        let guild = serenity::GuildId::new(core.guild_id);
        let user = serenity::UserId::new(user_id);
        let mut last = None;
        for attempt in 1..=3u64 {
            match guild.member(ctx, user).await {
                Ok(member) => {
                    let roles: Vec<u64> = member.roles.iter().map(|r| r.get()).collect();
                    return Resolution {
                        level: level_for(&core, user_id, Some(&roles)),
                        lookup_failed: false,
                    };
                }
                Err(cause) => last = Some(cause),
            }
            if attempt < 3 {
                tokio::time::sleep(Duration::from_millis(200 * attempt)).await;
            }
        }

        if let Some(cause) = last {
            warn!("dashboard: falha ao validar a permissao de {user_id}: {cause}");
        }
        // O piso por ID nao depende do Discord.
        Resolution {
            level: level_for(&core, user_id, None),
            lookup_failed: true,
        }
    }
}

/// `roles` None e "nao deu para consultar": so os IDs fixos contam.
pub fn level_for(core: &CoreConfig, user_id: u64, roles: Option<&[u64]>) -> i32 {
    if core.level4_user_ids.contains(&user_id) {
        return 4;
    }
    let floor = if core.level2_user_ids.contains(&user_id) {
        2
    } else {
        0
    };
    let Some(roles) = roles else {
        return floor;
    };
    let has = |ids: &[u64]| ids.iter().any(|id| roles.contains(id));
    let by_role = if has(&core.level3_role_ids) {
        3
    } else if has(&core.level2_role_ids) {
        2
    } else if has(&core.level1_role_ids) {
        1
    } else {
        0
    };
    by_role.max(floor)
}

pub fn permission_name(level: i32) -> &'static str {
    match level {
        4 => "Developer",
        3 => "General Staff",
        2 => "Regimental Command",
        1 => "NCO/Officer",
        _ => "Unknown",
    }
}

fn keep_newest(log: &mut Vec<AuditEntry>, keep: usize) {
    if log.len() > keep {
        log.sort_by_key(|e| e.timestamp_utc);
        let excess = log.len() - keep;
        log.drain(..excess);
    }
}

fn parse_core(text: &str) -> Result<(CoreConfig, Legacy), serde_json::Error> {
    let text = text.trim_start_matches('\u{feff}');
    let value: serde_json::Value = serde_json::from_reader(StripComments::new(text.as_bytes()))?;
    Ok((
        serde_json::from_value(value.clone())?,
        serde_json::from_value(value)?,
    ))
}

/// Arquivo auxiliar ilegivel cai no padrao SEM ser sobrescrito: o que sobrou
/// dele ainda pode ser recuperado a mao.
async fn read_aux<T: DeserializeOwned + Serialize>(path: &Path, fallback: T) -> T {
    let text = match tokio::fs::read_to_string(path).await {
        Ok(text) => text,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => {
            if let Err(cause) = write_json(path, &fallback).await {
                warn!("dashboard: {cause}");
            }
            return fallback;
        }
        Err(e) => {
            warn!(
                "dashboard: {} ilegivel ({e}); seguindo com o padrao",
                path.display()
            );
            return fallback;
        }
    };

    let text = text.trim_start_matches('\u{feff}');
    if text.trim().is_empty() {
        return fallback;
    }
    serde_json::from_str(text).unwrap_or_else(|e| {
        warn!(
            "dashboard: {} ilegivel ({e}); seguindo com o padrao, sem regravar o arquivo",
            path.display()
        );
        fallback
    })
}

/// .tmp com fsync, 0600 e rename: aqui mora o tunnelSecret, e uma queda no
/// meio nao pode truncar a whitelist nem o log.
async fn write_json<T: Serialize>(path: &Path, value: &T) -> Result<(), String> {
    let fail = |e: std::io::Error| format!("nao consegui gravar {}: {e}", path.display());
    if let Some(dir) = path.parent().filter(|d| !d.as_os_str().is_empty()) {
        tokio::fs::create_dir_all(dir).await.map_err(fail)?;
    }
    let mut tmp = path.as_os_str().to_owned();
    tmp.push(".tmp");
    let tmp = PathBuf::from(tmp);

    let text = serde_json::to_string_pretty(value).map_err(|e| e.to_string())?;
    let mut file = tokio::fs::File::create(&tmp).await.map_err(fail)?;
    file.write_all(text.as_bytes()).await.map_err(fail)?;
    file.sync_all().await.map_err(fail)?;
    tokio::fs::set_permissions(&tmp, std::fs::Permissions::from_mode(0o600))
        .await
        .map_err(fail)?;
    tokio::fs::rename(&tmp, path).await.map_err(fail)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp_dir(name: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("ccore-dash-{name}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }

    fn core(json: &str) -> CoreConfig {
        parse_core(json).unwrap().0
    }

    #[test]
    fn le_o_exemplo_do_csharp_e_grava_os_mesmos_nomes() {
        let c = core(
            r#"{"listenUrl":"http://127.0.0.1:5056/","allowedOrigins":["https://x"],
                "guildId":"1487938282200236224","level4UserIds":[1],"level2UserIds":null,
                "level2RoleIds":[0,0],"grantableRoleIds":[5],"allowedSendChannelIds":[],
                "linkCodeLifetimeMinutes":10,"sessionLifetimeMinutes":60,"tunnelSecret":""}"#,
        );
        assert_eq!(c.guild_id, 1487938282200236224);
        assert!(c.level2_user_ids.is_empty());
        assert_eq!(c.grantable_role_ids, [5]);

        let json = serde_json::to_string(&CoreConfig::default()).unwrap();
        for key in [
            "\"listenUrl\"",
            "\"allowedOrigins\"",
            "\"tunnelSecret\"",
            "\"guildId\"",
            "\"level4UserIds\"",
            "\"level2UserIds\"",
            "\"level3RoleIds\"",
            "\"level2RoleIds\"",
            "\"level1RoleIds\"",
            "\"regimentRoleIds\"",
            "\"grantableRoleIds\"",
            "\"allowedSendChannelIds\"",
            "\"linkCodeLifetimeMinutes\"",
            "\"sessionLifetimeMinutes\"",
        ] {
            assert!(json.contains(key), "{key}");
        }
    }

    #[test]
    fn nivel_por_id_por_cargo_e_piso() {
        let c = core(
            r#"{"level4UserIds":[9],"level2UserIds":[7],"level3RoleIds":[30],
                "level2RoleIds":[20],"level1RoleIds":[10]}"#,
        );
        assert_eq!(level_for(&c, 9, None), 4);
        assert_eq!(level_for(&c, 7, None), 2);
        assert_eq!(level_for(&c, 7, Some(&[10])), 2);
        assert_eq!(level_for(&c, 7, Some(&[30])), 3);
        assert_eq!(level_for(&c, 1, Some(&[10, 20])), 2);
        assert_eq!(level_for(&c, 1, Some(&[99])), 0);
        assert_eq!(level_for(&c, 1, None), 0);
        assert_eq!(permission_name(2), "Regimental Command");
        assert_eq!(permission_name(0), "Unknown");
    }

    #[test]
    fn log_guarda_so_os_mais_recentes() {
        let mut log: Vec<AuditEntry> = (0..5)
            .map(|i| AuditEntry {
                timestamp_utc: DateTime::from_timestamp(100 - i, 0).unwrap(),
                details: i.to_string(),
                ..AuditEntry::default()
            })
            .collect();
        keep_newest(&mut log, 3);
        let kept: Vec<&str> = log.iter().map(|e| e.details.as_str()).collect();
        assert_eq!(kept, ["2", "1", "0"]);
    }

    #[tokio::test]
    async fn migra_whitelist_antiga_e_separa_os_arquivos() {
        let dir = temp_dir("migra");
        let path = dir.join("dashboard_auth.json");
        std::fs::write(
            &path,
            r#"{"guildId":5,"whitelistedUsers":[{"userId":1,"username":"a","permissionLevel":2,
                "firstWhitelistedAt":"2026-01-01T00:00:00+00:00","lastValidatedAt":"2026-01-01T00:00:00+00:00"}],
                "auditLog":[]}"#,
        )
        .unwrap();

        let store = AuthStore::new(&path);
        let config = store.read().await;
        assert_eq!(config.core.guild_id, 5);
        assert_eq!(config.whitelisted_users[0].username, "a");
        assert!(
            !std::fs::read_to_string(&path)
                .unwrap()
                .contains("whitelistedUsers")
        );
        assert!(dir.join(WHITELIST).exists());

        store.upsert_whitelist(1, "b", 3).await.unwrap();
        store
            .append_audit(AuditEntry {
                action: "LOGIN".to_owned(),
                ..AuditEntry::default()
            })
            .await
            .unwrap();
        let again = store.read().await;
        assert_eq!(again.whitelisted_users[0].username, "b");
        assert_eq!(store.audit(10).await[0].action, "LOGIN");
        let mode = std::fs::metadata(&path).unwrap().permissions().mode();
        assert_eq!(mode & 0o777, 0o600);
        let _ = std::fs::remove_dir_all(dir);
    }

    #[tokio::test]
    async fn nucleo_ilegivel_nao_e_sobrescrito() {
        let dir = temp_dir("ilegivel");
        let path = dir.join("dashboard_auth.json");
        std::fs::write(&path, "{ \"guildId\": 5,, }").unwrap();

        let store = AuthStore::new(&path);
        assert_eq!(store.read().await.core.guild_id, 0);
        store.upsert_whitelist(1, "a", 1).await.unwrap();
        assert_eq!(
            std::fs::read_to_string(&path).unwrap(),
            "{ \"guildId\": 5,, }"
        );
        let _ = std::fs::remove_dir_all(dir);
    }
}
