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
    /// IA local (llama.cpp). Sem a secao, o bot nem oferece o "-ia on".
    pub llm: Option<LlmConfig>,
}

/// IA local. Ligada so por operador ("-ia on"), nunca por padrao.
#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LlmConfig {
    #[serde(default = "llm_base_url")]
    pub base_url: String,
    /// Unit de usuario do systemd que roda o llama-server.
    #[serde(default = "llm_service")]
    pub service: String,
    /// Quem pode usar "-ia on" e "-ia off" (e nao tem intervalo entre perguntas).
    #[serde(default)]
    pub operator_user_ids: Vec<u64>,
    /// Canais onde ela responde. Vazio: qualquer canal dos servidores do bot.
    #[serde(default)]
    pub channel_ids: Vec<u64>,
    #[serde(default = "llm_prompt")]
    pub system_prompt: String,
    /// Intervalo por pessoa entre perguntas.
    #[serde(default = "twenty")]
    pub cooldown_seconds: u64,
    /// Desliga sozinha depois de tanto tempo sem uso. 0: nunca.
    #[serde(default = "thirty")]
    pub idle_off_minutes: u64,
    #[serde(default = "four_hundred")]
    pub max_tokens: u32,
    /// Mensagens anteriores do canal que vao junto como contexto.
    #[serde(default = "eight")]
    pub history: usize,
}

fn llm_base_url() -> String {
    "http://127.0.0.1:9931".to_owned()
}

fn llm_service() -> String {
    "ccore-llm.service".to_owned()
}

fn llm_prompt() -> String {
    "Você é o ccore, um cara descontraído num servidor de Discord. Fala português do Brasil, \
     curto e natural, como gente conversando. Lê o tom de quem falou e responde no mesmo clima: \
     zoeira com zoeira, pergunta séria com resposta séria, gente chateada com calma. Não se \
     apresenta e não puxa assunto de regimento, exército ou Roblox por conta própria; só fala \
     disso se perguntarem. Se perguntarem: deployment é a convocação pra uma partida no Roblox, \
     com o código do jogo; auditoria é o registro de kills, deaths e assists por batalha; as \
     patentes seguem a escada britânica (Recruit, Private, Corporal, Serjeant, Ensign, \
     Lieutenant, Captain...). Você não tem acesso a planilha, auditoria nem dados de jogador: \
     não inventa números e não promete buscar nada; se não souber, diz que não sabe. Se te \
     xingarem ou provocarem, devolve na mesma moeda com uma alfinetada leve e bem-humorada: \
     pode ser um pouco ácido, mas nunca pesado, sem palavrão pesado, ofensa à família, \
     preconceito ou ataque pessoal sério. Nunca menciona @everyone, @here nem cargos."
        .to_owned()
}

fn twenty() -> u64 {
    20
}

fn thirty() -> u64 {
    30
}

fn four_hundred() -> u32 {
    400
}

fn eight() -> usize {
    8
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
    /// URL /exec do app da web do Apps Script (sheets/ccore.gs). Sem ela, o
    /// /audit-push nao mexe na planilha.
    pub sheet_webhook_url: Option<String>,
    /// O mesmo valor de CCORE_SECRET nas propriedades do script.
    pub sheet_webhook_secret: Option<String>,
    /// ID da planilha em que o push pode gravar. O script recusa se estiver
    /// implantado em outra.
    pub sheet_spreadsheet_id: Option<String>,
    #[serde(default)]
    pub csv_columns: CsvColumns,
    #[serde(default = "one")]
    pub csv_header_rows: usize,
    /// De quantos em quantos minutos o bot copia a planilha para o audit.json.
    /// 0 desliga.
    #[serde(default = "five")]
    pub sheet_sync_minutes: u64,
}

fn one() -> usize {
    1
}

fn five() -> u64 {
    5
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
