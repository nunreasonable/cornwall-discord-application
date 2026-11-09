use std::collections::HashSet;

use poise::serenity_prelude as serenity;
use reqwest::Url;

use crate::config::Config;

const FALLBACK_GAME_LINK: &str = "https://www.roblox.com/games/12068120918/Napoleonic-Wars";
const FALLBACK_TITLE: &str = "<:Resenha1490483982331023572> À BATALHA!!!!!";
const FALLBACK_VOICE: &str = ":beer: Taverna dos Faiões";

pub struct Deployment {
    pub message: serenity::CreateMessage,
    pub title: String,
    pub voice: String,
    pub roles_pinged: usize,
}

fn non_blank(value: Option<&String>) -> Option<&str> {
    value.map(|v| v.trim()).filter(|v| !v.is_empty())
}

/// Tira o que quebraria o markdown do embed: a crase fecha o code span, e
/// colchete/parentese forjariam um segundo link no rotulo, justo na mensagem
/// que o regimento inteiro clica.
fn sanitize(code: &str) -> String {
    code.chars()
        .filter(|c| {
            !matches!(
                c,
                '`' | '[' | ']' | '(' | ')' | '*' | '_' | '~' | '|' | '\\'
            )
        })
        .collect()
}

/// Igual ao Uri.EscapeDataString: so as letras, digitos e -_.~ passam.
fn escape(text: &str) -> String {
    text.bytes()
        .map(|b| {
            if b.is_ascii_alphanumeric() || matches!(b, b'-' | b'_' | b'.' | b'~') {
                (b as char).to_string()
            } else {
                format!("%{b:02X}")
            }
        })
        .collect()
}

fn quick_launch(config: &Config, code: &str, game_link: &str) -> Option<String> {
    let template = non_blank(config.deployment_quick_launch_link.as_ref())
        .map(str::to_owned)
        .or_else(|| {
            non_blank(config.deployment_place_id.as_ref()).map(|place| {
                format!("https://www.roblox.com/games/start?launchData=CODE_HERE&placeId={place}")
            })
        })
        .unwrap_or_else(|| game_link.to_owned());

    let resolved = template
        .replace("CODE_HERE", &escape(code))
        .replace("{CODE}", &escape(code));
    Url::parse(&resolved).ok().map(String::from)
}

/// Monta a mensagem de deployment. `roles` sao os cargos que existem no
/// servidor, e `resenha` o emoji :Resenha: dele, se houver.
pub fn build(
    config: &Config,
    code: &str,
    roles: &HashSet<u64>,
    resenha: Option<&str>,
) -> Deployment {
    let game_link = non_blank(config.deployment_game_link.as_ref())
        .or_else(|| non_blank(config.default_game_link.as_ref()))
        .unwrap_or(FALLBACK_GAME_LINK);
    let title = non_blank(config.deployment_title.as_ref())
        .unwrap_or(FALLBACK_TITLE)
        .replace(":Resenha:", resenha.unwrap_or("⚔️"));
    let voice = non_blank(config.deployment_voice_channel.as_ref()).unwrap_or(FALLBACK_VOICE);

    let mut mentions: Vec<String> = Vec::new();
    for &id in config
        .deployment_default_roles
        .iter()
        .filter(|id| roles.contains(id))
    {
        let mention = format!("<@&{id}>");
        if !mentions.contains(&mention) {
            mentions.push(mention);
        }
    }
    let mentions = mentions.join(" ");

    let voice_display = match non_blank(config.deployment_voice_chat_link.as_ref()).map(Url::parse)
    {
        Some(Ok(link)) => format!("[{voice}]({link})"),
        _ => voice.to_owned(),
    };

    let launch = quick_launch(config, code, game_link);
    let safe = sanitize(code);

    let mut lines = Vec::new();
    if !mentions.is_empty() {
        lines.push(mentions.clone());
        lines.push(String::new());
    }
    lines.push(format!("**Voice Channel:** {voice_display}"));
    lines.push(format!("**Code:** `{safe}`"));
    lines.push(String::new());
    lines.push(match &launch {
        Some(link) => format!("**Quick Launch Link:** [Join & Enter {safe}]({link})"),
        None => format!("**Quick Launch Link:** Join & Enter {safe}"),
    });

    let mut embed = serenity::CreateEmbed::new()
        .title(&title)
        .colour(serenity::Colour::BLURPLE);
    match non_blank(config.deployment_image_url.as_ref()).map(Url::parse) {
        Some(Ok(image)) => embed = embed.image(image),
        Some(Err(_)) => {
            lines.push(String::new());
            lines.push("⚠️ URL da imagem inválida. A imagem não será exibida.".to_owned());
        }
        None => {}
    }
    embed = embed.description(lines.join("\n"));

    let button = serenity::CreateButton::new_link(launch.as_deref().unwrap_or(game_link))
        .label("Quick Launch");

    // As mencoes vao no conteudo: dentro do embed elas aparecem mas nao
    // notificam ninguem.
    let mut message = serenity::CreateMessage::new()
        .embed(embed)
        .components(vec![serenity::CreateActionRow::Buttons(vec![button])]);
    if !mentions.is_empty() {
        message = message.content(&mentions);
    }

    Deployment {
        message,
        title,
        voice: voice.to_owned(),
        roles_pinged: mentions.split_whitespace().count(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn config(json: &str) -> Config {
        let mut value: serde_json::Value = serde_json::from_str(json).unwrap();
        value["token"] = "x".into();
        serde_json::from_value(value).unwrap()
    }

    #[test]
    fn codigo_nao_forja_link_nem_fecha_crase() {
        assert_eq!(sanitize("ab`c](https://x)"), "abchttps://x");
    }

    #[test]
    fn link_rapido_usa_o_place_id_com_codigo_escapado() {
        let c = config(r#"{"deploymentPlaceId": 123}"#);
        assert_eq!(
            quick_launch(&c, "a b&c", FALLBACK_GAME_LINK).unwrap(),
            "https://www.roblox.com/games/start?launchData=a%20b%26c&placeId=123"
        );
    }

    #[test]
    fn modelo_proprio_tem_prioridade_e_link_invalido_some() {
        let c = config(
            r#"{"deploymentQuickLaunchLink": "https://x.test/{CODE}", "deploymentPlaceId": "9"}"#,
        );
        assert_eq!(quick_launch(&c, "AB", "").unwrap(), "https://x.test/AB");
        let bad = config(r#"{"deploymentQuickLaunchLink": "nao e url CODE_HERE"}"#);
        assert!(quick_launch(&bad, "AB", "").is_none());
    }

    #[test]
    fn so_pinga_cargo_que_existe_e_sem_repetir() {
        let c = config(r#"{"deploymentDefaultRoles": [1, 2, 1]}"#);
        let d = build(&c, "X", &HashSet::from([1]), None);
        assert_eq!(d.roles_pinged, 1);
        assert!(d.title.contains("<:Resenha"));
    }
}
