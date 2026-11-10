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
    /// O aviso de imagem invalida, que o dashboard devolve a parte.
    pub image_warning: Option<String>,
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
    let mut image_warning = None;
    match non_blank(config.deployment_image_url.as_ref()).map(Url::parse) {
        Some(Ok(image)) => embed = embed.image(image),
        Some(Err(_)) => {
            let warning = "⚠️ URL da imagem inválida. A imagem não será exibida.".to_owned();
            lines.push(String::new());
            lines.push(warning.clone());
            image_warning = Some(warning);
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
        image_warning,
    }
}

/// Os cargos que existem no servidor e o emoji :Resenha: dele, do cache.
pub fn guild_assets(
    cache: &serenity::Cache,
    guild_id: serenity::GuildId,
) -> Option<(HashSet<u64>, Option<String>)> {
    let guild = cache.guild(guild_id)?;
    let roles = guild.roles.keys().map(|id| id.get()).collect();
    let resenha = guild
        .emojis
        .values()
        .find(|e| e.name.eq_ignore_ascii_case("Resenha"))
        .map(ToString::to_string);
    Some((roles, resenha))
}

/// Confere o canal e as permissoes do bot antes de tentar: troca um erro
/// generico da API por qual permissao falta. O Err e a frase para o usuario.
pub async fn channel(
    ctx: &serenity::Context,
    guild_id: serenity::GuildId,
    config: &Config,
) -> Result<serenity::GuildChannel, String> {
    let Some(id) = config.deployment_channel_id.filter(|&id| id != 0) else {
        return Err(
            "O ID do canal para envio de deployments não está configurado (deploymentChannelId)."
                .to_owned(),
        );
    };

    let not_found = || {
        format!("O canal configurado para deployments não foi encontrado neste servidor. ID: {id}")
    };
    let channel = serenity::ChannelId::new(id)
        .to_channel(ctx)
        .await
        .ok()
        .and_then(|c| c.guild())
        .filter(|c| c.guild_id == guild_id)
        .ok_or_else(not_found)?;

    let bot_id = ctx.cache.current_user().id;
    let bot = guild_id.member(ctx, bot_id).await.map_err(|_| {
        "Não foi possível obter as informações do membro do bot no servidor.".to_owned()
    })?;

    // A referencia do cache segura um lock: so depois do ultimo await.
    let permissions = ctx
        .cache
        .guild(guild_id)
        .map(|guild| guild.user_permissions_in(&channel, &bot))
        .ok_or("O servidor não está no cache do bot.")?;

    if !permissions.send_messages() {
        return Err(format!(
            "O bot não tem permissão para enviar mensagens no canal {}.",
            channel.name
        ));
    }
    if !permissions.embed_links() {
        return Err(format!(
            "O bot não tem permissão para enviar embeds no canal {}.",
            channel.name
        ));
    }
    Ok(channel)
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
