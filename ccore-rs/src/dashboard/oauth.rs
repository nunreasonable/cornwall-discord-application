//! Login com o Discord (OAuth2, escopo identify). O painel manda a pessoa ao
//! discord.com, recebe o `code` de volta e o entrega aqui; o client secret
//! nunca sai do bot. Depois de saber quem e, o caminho e o mesmo do codigo do
//! /dashboardlink, que continua valendo.

use std::time::Duration;

use chrono::Utc;
use serde::Deserialize;
use serde_json::json;
use tracing::warn;

use super::{
    Dashboard,
    gate::{PendingCode, retry_secs},
    request::{Reply, Req, err, error, ok, with_retry_after},
    routes::finish_login,
};

const TOKEN_URL: &str = "https://discord.com/api/v10/oauth2/token";
const ME_URL: &str = "https://discord.com/api/v10/users/@me";
const TIMEOUT: Duration = Duration::from_secs(15);

#[derive(Deserialize)]
struct Token {
    access_token: String,
}

#[derive(Deserialize)]
struct DiscordUser {
    id: String,
    username: String,
    avatar: Option<String>,
}

fn avatar_url(user_id: u64, avatar: Option<&str>) -> String {
    match avatar {
        Some(hash) => format!("https://cdn.discordapp.com/avatars/{user_id}/{hash}.png"),
        None => format!(
            "https://cdn.discordapp.com/embed/avatars/{}.png",
            (user_id >> 22) % 6
        ),
    }
}

fn client_id(d: &Dashboard) -> u64 {
    d.bot
        .ctx
        .http
        .application_id()
        .map(|id| id.get())
        .unwrap_or_else(|| d.bot.ctx.cache.current_user().id.get())
}

/// O painel pergunta se o botao aparece e para onde mandar a pessoa.
pub async fn config(d: &Dashboard, _req: &mut Req) -> Reply {
    let core = d.auth.read().await.core;
    let enabled = !core.discord_client_secret.trim().is_empty()
        && !core.discord_redirect_uri.trim().is_empty();
    if !enabled {
        return ok(200, json!({ "enabled": false }));
    }
    ok(
        200,
        json!({
            "enabled": true,
            "clientId": client_id(d).to_string(),
            "redirectUri": core.discord_redirect_uri.trim(),
        }),
    )
}

pub async fn login(d: &Dashboard, req: &mut Req) -> Reply {
    // A mesma trava do login por codigo.
    if let Err(wait) = d.gate.login_attempt(&req.headers, req.peer) {
        return Ok(with_retry_after(
            error(
                429,
                "Tentativas demais. Espere alguns minutos e tente de novo.",
            ),
            retry_secs(wait),
        ));
    }

    let body = req.json().await?;
    let Some(code) = body
        .trimmed("code")
        .filter(|c| !c.is_empty() && c.len() <= 200)
    else {
        return err(400, "code é obrigatório.");
    };

    let core = d.auth.read().await.core;
    let secret = core.discord_client_secret.trim();
    let redirect = core.discord_redirect_uri.trim();
    if secret.is_empty() || redirect.is_empty() {
        return err(404, "Login com o Discord não está configurado.");
    }

    let user = match exchange(d, &code, secret, redirect).await {
        Ok(user) => user,
        Err(cause) => {
            warn!("dashboard: login com o Discord falhou: {cause}");
            return err(
                401,
                "O Discord não confirmou o login. Tente de novo ou use o /dashboardlink.",
            );
        }
    };
    let Ok(user_id) = user.id.parse::<u64>() else {
        return err(401, "O Discord devolveu um usuário inválido.");
    };

    d.gate.login_succeeded(&req.headers, req.peer);

    let pending = PendingCode {
        user_id,
        username: user.username,
        avatar_url: avatar_url(user_id, user.avatar.as_deref()),
        expires_at: Utc::now(),
    };
    finish_login(d, pending, "Login efetuado com o Discord.").await
}

async fn exchange(
    d: &Dashboard,
    code: &str,
    secret: &str,
    redirect: &str,
) -> Result<DiscordUser, String> {
    let client_id = client_id(d).to_string();
    let form = [
        ("client_id", client_id.as_str()),
        ("client_secret", secret),
        ("grant_type", "authorization_code"),
        ("code", code),
        ("redirect_uri", redirect),
    ];
    let token: Token = d
        .bot
        .http
        .post(TOKEN_URL)
        .timeout(TIMEOUT)
        .form(&form)
        .send()
        .await
        .map_err(|e| e.without_url().to_string())?
        .error_for_status()
        .map_err(|e| e.without_url().to_string())?
        .json()
        .await
        .map_err(|e| e.without_url().to_string())?;

    d.bot
        .http
        .get(ME_URL)
        .timeout(TIMEOUT)
        .bearer_auth(&token.access_token)
        .send()
        .await
        .map_err(|e| e.without_url().to_string())?
        .error_for_status()
        .map_err(|e| e.without_url().to_string())?
        .json()
        .await
        .map_err(|e| e.without_url().to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn avatar_com_e_sem_hash() {
        assert_eq!(
            avatar_url(1, Some("abc")),
            "https://cdn.discordapp.com/avatars/1/abc.png"
        );
        assert!(avatar_url(1 << 22, None).ends_with("/embed/avatars/1.png"));
    }
}
