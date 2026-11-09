use std::time::Duration;

use chrono::{DateTime, Utc};
use poise::serenity_prelude as serenity;
use reqwest::Client;
use serde::{Deserialize, de::DeserializeOwned, de::IgnoredAny};
use serde_json::json;
use serenity::Mentionable;
use tracing::{info, warn};

use crate::{
    config::Config,
    embeds::{self, truncate},
};

const MIN_ACCOUNT_AGE_DAYS: i64 = 90;
const MIN_FRIENDS: u32 = 1;
const NICK_PREFIX: &str = "[12°]";
const MAX_NICK_CHARS: usize = 32;

pub struct RobloxCheck {
    pub user_id: u64,
    pub account_age_days: i64,
    pub friends: u32,
    /// None quando a API de badges falhou. Badge e bonus, nunca bloqueia.
    pub badges: Option<usize>,
}

impl RobloxCheck {
    /// So idade e amigos decidem se e alt.
    pub fn is_likely_main(&self) -> bool {
        self.account_age_days >= MIN_ACCOUNT_AGE_DAYS && self.friends >= MIN_FRIENDS
    }

    pub fn badges_display(&self) -> String {
        self.badges
            .map_or_else(|| "Indisponível".to_owned(), |count| count.to_string())
    }

    pub fn alt_embed(&self) -> serenity::CreateEmbed {
        embeds::error(
            "Alistamento negado - Conta provavelmente ALT",
            "A conta ROBLOX fornecida não atende aos critérios mínimos de confiabilidade.",
        )
        .field(
            "Idade da conta",
            format!("{} dias", self.account_age_days),
            true,
        )
        .field("Amigos", self.friends.to_string(), true)
        .field("Badges (bônus)", self.badges_display(), true)
    }
}

#[derive(Deserialize)]
struct Lookup {
    data: Vec<LookupUser>,
}

#[derive(Deserialize)]
struct LookupUser {
    id: u64,
}

#[derive(Deserialize)]
struct UserInfo {
    created: String,
}

#[derive(Deserialize)]
struct FriendCount {
    count: u32,
}

#[derive(Deserialize)]
struct Badges {
    data: Vec<IgnoredAny>,
}

/// O Err e a frase pronta para o usuario. Conta reprovada como alt NAO e Err:
/// a consulta funcionou, quem decide e `is_likely_main`.
pub async fn verify_roblox(http: &Client, username: &str) -> Result<RobloxCheck, String> {
    let username = username.trim();
    if username.is_empty() {
        return Err("O username fornecido está vazio.".to_owned());
    }

    let lookup: Lookup = http
        .post("https://users.roblox.com/v1/usernames/users")
        .json(&json!({ "usernames": [username], "excludeBannedUsers": true }))
        .send()
        .await
        .map_err(describe)?
        .error_for_status()
        .map_err(|_| "Não foi possível encontrar uma conta ROBLOX com esse nome. Verifique se o nome foi digitado corretamente.".to_owned())?
        .json()
        .await
        .map_err(describe)?;

    let user_id = lookup
        .data
        .first()
        .ok_or("Nenhuma conta ROBLOX foi encontrada com o nome informado.")?
        .id;

    let info: UserInfo = get_json(
        http,
        &format!("https://users.roblox.com/v1/users/{user_id}"),
        "Não foi possível obter as informações da conta ROBLOX.",
    )
    .await?;

    let created = DateTime::parse_from_rfc3339(info.created.trim())
        .map_err(|_| "Não foi possível determinar a data de criação da conta ROBLOX.")?;

    let friends: FriendCount = get_json(
        http,
        &format!("https://friends.roblox.com/v1/users/{user_id}/friends/count"),
        "Não foi possível obter a quantidade de amigos da conta ROBLOX.",
    )
    .await?;

    let badges = fetch_badges(http, user_id).await;

    Ok(RobloxCheck {
        user_id,
        account_age_days: (Utc::now() - created.to_utc()).num_days(),
        friends: friends.count,
        badges,
    })
}

async fn get_json<T: DeserializeOwned>(
    http: &Client,
    url: &str,
    status_error: &str,
) -> Result<T, String> {
    http.get(url)
        .send()
        .await
        .map_err(describe)?
        .error_for_status()
        .map_err(|_| status_error.to_owned())?
        .json()
        .await
        .map_err(describe)
}

async fn fetch_badges(http: &Client, user_id: u64) -> Option<usize> {
    let url =
        format!("https://badges.roblox.com/v1/users/{user_id}/badges?limit=100&sortOrder=Asc");
    match get_json::<Badges>(http, &url, "status de erro").await {
        Ok(badges) => Some(badges.data.len()),
        Err(cause) => {
            warn!("falha ao consultar badges ROBLOX: {cause}");
            None
        }
    }
}

fn describe(error: reqwest::Error) -> String {
    if error.is_timeout() {
        return "A consulta à API do ROBLOX demorou demais. Tente novamente em instantes."
            .to_owned();
    }

    let text = truncate(&error.without_url().to_string(), 150);
    format!("Ocorreu um erro inesperado ao consultar a conta ROBLOX: {text}")
}

pub struct Applied {
    pub added_roles: Vec<serenity::RoleId>,
    pub warnings: Vec<String>,
}

/// Da os cargos e poe o prefixo no apelido. O que nao der vira aviso e o resto
/// continua: um cargo com problema nao pode cancelar o alistamento inteiro.
pub async fn apply(
    ctx: &serenity::Context,
    guild_id: serenity::GuildId,
    target: &serenity::Member,
    config: &Config,
    social_role: bool,
    channel_id: serenity::ChannelId,
) -> Applied {
    let mut applied = Applied {
        added_roles: Vec::new(),
        warnings: Vec::new(),
    };

    // Copia e solta o cache antes do primeiro await: a referencia do cache
    // segura um lock e nao pode atravessar um await.
    let Some(guild) = guild_id.to_guild_cached(&ctx.cache).map(|g| g.clone()) else {
        applied
            .warnings
            .push("O servidor não está no cache do bot.".to_owned());
        return applied;
    };

    let bot_id = ctx.cache.current_user().id;
    let bot = match guild_id.member(ctx, bot_id).await {
        Ok(bot) => bot,
        Err(cause) => {
            warn!("nao foi possivel obter o membro do bot: {cause}");
            applied
                .warnings
                .push("Não consegui consultar as permissões do bot.".to_owned());
            return applied;
        }
    };

    let permissions = guild.channels.get(&channel_id).map_or_else(
        || guild.member_permissions(&bot),
        |c| guild.user_permissions_in(c, &bot),
    );
    let can_manage_roles = permissions.manage_roles();
    let bot_top_position = bot
        .roles
        .iter()
        .filter_map(|id| guild.roles.get(id))
        .map(|role| role.position)
        .max();

    if !config.enlist_target_role_ids.is_empty() {
        if can_manage_roles {
            for &id in &config.enlist_target_role_ids {
                grant(
                    ctx,
                    &guild,
                    target,
                    id,
                    bot_top_position,
                    "Alistamento via comando",
                    &mut applied,
                )
                .await;
            }
        } else {
            applied.warnings.push(
                "O bot não tem permissão para gerenciar cargos. Verifique as permissões do bot."
                    .to_owned(),
            );
        }
    }

    if social_role {
        match config.enlist_social_role_id.filter(|&id| id != 0) {
            None => applied.warnings.push(
                "O cargo social não está configurado. Verifique o `enlistSocialRoleId` no config.jsonc."
                    .to_owned(),
            ),
            Some(_) if !can_manage_roles => applied
                .warnings
                .push("O bot não tem permissão para gerenciar cargos (cargo social).".to_owned()),
            Some(id) => {
                grant(ctx, &guild, target, id, bot_top_position, "Cargo social via comando", &mut applied)
                    .await;
            }
        }
    }

    let current = target.nick.as_deref().unwrap_or(&target.user.name);
    if !has_prefix(current) {
        if permissions.manage_nicknames() {
            let edit = serenity::EditMember::new().nickname(with_prefix(current));
            if let Err(cause) = guild_id.edit_member(ctx, target.user.id, edit).await {
                applied
                    .warnings
                    .push(format!("Não foi possível alterar o apelido. Erro: {cause}"));
            }
        } else {
            applied.warnings.push(
                "O bot não tem permissão para gerenciar apelidos. Verifique as permissões do bot."
                    .to_owned(),
            );
        }
    }

    applied
}

async fn grant(
    ctx: &serenity::Context,
    guild: &serenity::Guild,
    target: &serenity::Member,
    role_id: u64,
    bot_top_position: Option<u16>,
    reason: &str,
    applied: &mut Applied,
) {
    let id = serenity::RoleId::new(role_id);
    let Some(role) = guild.roles.get(&id) else {
        applied
            .warnings
            .push(format!("Cargo ID {role_id} não encontrado no servidor."));
        return;
    };

    if target.roles.contains(&id) {
        return;
    }

    if bot_top_position.is_some_and(|top| role.position >= top) {
        applied.warnings.push(format!(
            "Não foi possível atribuir o cargo {}: posição igual ou superior ao cargo mais alto do bot.",
            role.name
        ));
        return;
    }

    match ctx
        .http
        .add_member_role(guild.id, target.user.id, id, Some(reason))
        .await
    {
        Ok(()) => applied.added_roles.push(id),
        Err(cause) => applied
            .warnings
            .push(format!("Falha ao adicionar o cargo {}: {cause}", role.name)),
    }
}

fn has_prefix(nick: &str) -> bool {
    nick.to_lowercase().starts_with(&NICK_PREFIX.to_lowercase())
}

/// O Discord recusa apelido com mais de 32 caracteres.
fn with_prefix(current: &str) -> String {
    let room = MAX_NICK_CHARS - NICK_PREFIX.chars().count() - 1;
    let name: String = current.chars().take(room).collect();
    format!("{NICK_PREFIX} {name}")
}

/// Os campos variaveis do log. O /alistar-se troca titulo e descricao e soma as
/// respostas do formulario.
pub struct LogDetails<'a> {
    pub executor: String,
    pub roblox_name: &'a str,
    pub social_role: bool,
    pub title: &'a str,
    pub description: &'a str,
    pub extra_fields: Vec<(&'a str, String)>,
}

pub fn log_embed(
    ctx: &serenity::Context,
    target: &serenity::Member,
    check: &RobloxCheck,
    applied: &Applied,
    details: LogDetails<'_>,
) -> serenity::CreateEmbed {
    let bot_avatar = ctx.cache.current_user().face();
    let roles = if applied.added_roles.is_empty() {
        "Nenhum".to_owned()
    } else {
        applied
            .added_roles
            .iter()
            .map(|id| id.mention().to_string())
            .collect::<Vec<_>>()
            .join(", ")
    };

    let mut embed = serenity::CreateEmbed::new()
        .title(details.title)
        .description(details.description)
        .colour(serenity::Colour::BLURPLE)
        .thumbnail(target.face())
        .footer(
            serenity::CreateEmbedFooter::new("Recruit log gerado por CornwallBot")
                .icon_url(bot_avatar),
        )
        .timestamp(serenity::Timestamp::now())
        .field("Executor", details.executor, true)
        .field("Alistado", target.mention().to_string(), true)
        .field("Nome no ROBLOX", details.roblox_name, true)
        .field("ROBLOX ID", check.user_id.to_string(), true);

    for (name, value) in details.extra_fields {
        let value = if value.trim().is_empty() {
            "N/A".to_owned()
        } else {
            truncate(&value, 1024)
        };
        embed = embed.field(name, value, true);
    }

    embed
        .field(
            "Idade da conta (dias)",
            check.account_age_days.to_string(),
            true,
        )
        .field("Amigos", check.friends.to_string(), true)
        .field("Badges", check.badges_display(), true)
        .field("Cargos adicionados", roles, false)
        .field(
            "Cargo social?",
            if details.social_role { "Sim" } else { "Não" },
            true,
        )
        .field("Verificação de alt", "Automática (aprovado)", true)
}

/// Publica o log e as boas-vindas. Falha vira aviso: o alistamento ja aconteceu.
pub async fn announce(
    ctx: &serenity::Context,
    guild_id: serenity::GuildId,
    config: &Config,
    log: serenity::CreateEmbed,
    target: &serenity::Member,
) -> Vec<String> {
    let mut warnings = Vec::new();

    if let Some(id) = config.enlist_log_channel_id.filter(|&id| id != 0) {
        let message = serenity::CreateMessage::new().embed(log);
        send(ctx, guild_id, id, "canal de logs", message, &mut warnings).await;
    }

    if let Some(id) = config.enlist_welcome_channel_id.filter(|&id| id != 0) {
        let message = serenity::CreateMessage::new()
            .content(format!("Bem-vindo ao 12°, {}!", target.mention()));
        send(
            ctx,
            guild_id,
            id,
            "canal de boas-vindas",
            message,
            &mut warnings,
        )
        .await;
    }

    warnings
}

async fn send(
    ctx: &serenity::Context,
    guild_id: serenity::GuildId,
    channel_id: u64,
    label: &str,
    message: serenity::CreateMessage,
    warnings: &mut Vec<String>,
) {
    let result = async {
        let channel = serenity::ChannelId::new(channel_id).to_channel(ctx).await?;
        match channel.guild() {
            Some(channel) if channel.guild_id == guild_id => {
                channel.send_message(ctx, message).await?;
                Ok(true)
            }
            _ => Ok::<_, serenity::Error>(false),
        }
    }
    .await;

    match result {
        Ok(true) => info!("alistamento publicado no {label}"),
        Ok(false) => warnings.push(format!("O {label} configurado pertence a outro servidor.")),
        Err(cause) => warnings.push(format!(
            "Erro ao escrever no {label}: {}. Confira o ID e se o bot tem **Ver canal** e **Enviar mensagens** nele.",
            truncate(&cause.to_string(), 150)
        )),
    }
}

/// Quanto falta para a pessoa poder tentar de novo, se falta.
pub fn cooldown_left(elapsed: Duration, limit: Duration) -> Option<Duration> {
    (elapsed < limit).then(|| limit - elapsed)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn check(days: i64, friends: u32) -> RobloxCheck {
        RobloxCheck {
            user_id: 1,
            account_age_days: days,
            friends,
            badges: None,
        }
    }

    #[test]
    fn alt_e_decidido_por_idade_e_amigos() {
        assert!(check(90, 1).is_likely_main());
        assert!(!check(89, 50).is_likely_main());
        assert!(!check(400, 0).is_likely_main());
    }

    #[test]
    fn apelido_cabe_em_32_caracteres() {
        let nick = with_prefix(&"ã".repeat(40));
        assert_eq!(nick.chars().count(), 32);
        assert!(nick.starts_with("[12°] ã"));
    }

    #[test]
    fn prefixo_ignora_caixa() {
        assert!(has_prefix("[12°] Fulano"));
        assert!(!has_prefix("Fulano"));
    }

    #[test]
    fn cooldown_so_resta_antes_do_limite() {
        let limit = Duration::from_secs(30);
        assert_eq!(
            cooldown_left(Duration::from_secs(10), limit),
            Some(Duration::from_secs(20))
        );
        assert_eq!(cooldown_left(limit, limit), None);
    }
}
