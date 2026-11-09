use std::{error::Error, fs, path::Path};

use json_comments::StripComments;
use serde::Deserialize;

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
