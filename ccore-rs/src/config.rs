use std::{error::Error, fs, path::Path};

use json_comments::StripComments;
use serde::{Deserialize, Deserializer};

const CONFIG_PATH: &str = "config/config.jsonc";

/// So os campos que a parte ja portada usa. O serde ignora as outras chaves,
/// entao o mesmo config.jsonc do bot em C# continua valido aqui.
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Config {
    pub token: String,
    #[serde(default = "default_prefix")]
    pub prefix: String,
    #[serde(default)]
    pub guild_ids: Vec<u64>,
    pub enlist_permission_role_id: Option<u64>,
    /// Link que vai no texto da DM do /dmdeployment, para o Discord mostrar o preview.
    pub default_game_link: Option<String>,
    pub enlist_log_channel_id: Option<u64>,
    pub enlist_welcome_channel_id: Option<u64>,
    #[serde(default)]
    pub enlist_target_role_ids: Vec<u64>,
    pub enlist_social_role_id: Option<u64>,
    pub roblox_enlist_channel_id: Option<u64>,
    #[serde(default)]
    pub roblox_enlist_blocked_role_ids: Vec<u64>,
    #[serde(default)]
    pub spreadsheet_info_tabs: Vec<SpreadsheetTab>,
    pub audit: Option<AuditConfig>,
    pub deployment_channel_id: Option<u64>,
    #[serde(default)]
    pub deployment_allowed_role_ids: Vec<u64>,
    #[serde(default)]
    pub deployment_default_roles: Vec<u64>,
    pub deployment_game_link: Option<String>,
    pub deployment_voice_chat_link: Option<String>,
    pub deployment_title: Option<String>,
    pub deployment_voice_channel: Option<String>,
    pub deployment_image_url: Option<String>,
    #[serde(default, deserialize_with = "string_or_number")]
    pub deployment_place_id: Option<String>,
    pub deployment_quick_launch_link: Option<String>,
    pub message_reposting: Option<RepostConfig>,
    pub message_blacklist: Option<BlacklistConfig>,
}

/// O placeId aparece como texto ou como numero nos configs que existem.
fn string_or_number<'de, D: Deserializer<'de>>(d: D) -> Result<Option<String>, D::Error> {
    Ok(match Option::<serde_json::Value>::deserialize(d)? {
        Some(serde_json::Value::String(text)) => Some(text),
        Some(serde_json::Value::Number(number)) => Some(number.to_string()),
        _ => None,
    })
}

#[derive(Debug, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct RepostConfig {
    pub enabled: bool,
    pub target_channel_id: u64,
    pub repost_interval_minutes: u64,
    pub message_retention_hours: u64,
    pub minimum_messages_for_repost: usize,
}

impl Default for RepostConfig {
    fn default() -> Self {
        Self {
            enabled: false,
            target_channel_id: 0,
            repost_interval_minutes: 60,
            message_retention_hours: 24,
            minimum_messages_for_repost: 50,
        }
    }
}

#[derive(Debug, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct BlacklistConfig {
    pub enabled: bool,
    pub blacklisted_terms: Vec<String>,
    pub response_message: String,
    pub response_message2_terms: Vec<String>,
    pub response_message2: Option<String>,
    pub notify_user_ids: Vec<u64>,
    pub dm_alert_cooldown_minutes: Option<u64>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AuditConfig {
    /// So um override: o normal e o token vir do `gh auth token` da maquina.
    pub github_token: Option<String>,
    pub github_owner: Option<String>,
    pub github_repo: Option<String>,
    pub github_branch: Option<String>,
    pub audit_file_path: Option<String>,
    pub archive_directory: Option<String>,
    pub audit_csv_url: Option<String>,
    #[serde(default)]
    pub csv_columns: CsvColumns,
    #[serde(default = "one")]
    pub csv_header_rows: usize,
}

fn one() -> usize {
    1
}

/// Indices (base 0) das colunas da planilha do /audit-import. -1 e uma coluna
/// que a planilha nao tem: a planilha atual nao traz kills/deaths/assists.
#[derive(Debug, Deserialize)]
#[serde(default)]
pub struct CsvColumns {
    pub username: i32,
    pub rank: i32,
    pub battles: i32,
    pub kills: i32,
    pub deaths: i32,
    pub assists: i32,
}

impl Default for CsvColumns {
    fn default() -> Self {
        Self {
            username: 0,
            rank: 1,
            battles: 2,
            kills: -1,
            deaths: -1,
            assists: -1,
        }
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SpreadsheetTab {
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub csv_url: String,
    /// Aba de historico: vira uma secao "antiga -> nova" no embed.
    #[serde(default)]
    pub promotions: bool,
}

fn default_prefix() -> String {
    "-".to_owned()
}

impl Config {
    pub fn load() -> Result<Self, Box<dyn Error + Send + Sync>> {
        Self::from_path(Path::new(CONFIG_PATH))
    }

    fn from_path(path: &Path) -> Result<Self, Box<dyn Error + Send + Sync>> {
        let text = fs::read_to_string(path)
            .map_err(|e| format!("nao consegui ler {}: {e}", path.display()))?;
        Self::parse(&text)
    }

    fn parse(text: &str) -> Result<Self, Box<dyn Error + Send + Sync>> {
        let config: Self = serde_json::from_reader(StripComments::new(text.as_bytes()))?;
        if config.token.trim().is_empty() {
            return Err("token vazio no config".into());
        }
        Ok(config)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn le_jsonc_e_ignora_chaves_desconhecidas() {
        let config = Config::parse(
            r#"{
                // comentario
                "token": "abc",
                "guildIds": [1487938282200236224],
                "enlistLogChannelId": 1
            }"#,
        )
        .unwrap();

        assert_eq!(config.token, "abc");
        assert_eq!(config.prefix, "-");
        assert_eq!(config.guild_ids, [1487938282200236224]);
    }

    #[test]
    fn recusa_token_vazio() {
        assert!(Config::parse(r#"{ "token": "  " }"#).is_err());
    }
}
