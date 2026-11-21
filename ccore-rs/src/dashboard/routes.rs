//! Login, moderacao, status, logs, deployment, DM em massa e alistamento.

use std::{collections::HashMap, fmt::Display};

use chrono::Utc;
use poise::serenity_prelude as serenity;
use serde_json::{Value, json};
use tracing::warn;

use super::{
    Dashboard, PERMISSION_REFRESH,
    auth::{CoreConfig, permission_name},
    gate::{PendingCode, Session, retry_secs},
    request::{Reply, Req, err, error, ok, utf16_len, with_retry_after},
};
use crate::{
    commands::massdm::deployment_dm,
    config::Config,
    deployment, dm,
    enlistment::{self, LogDetails},
    log_buffer::Severity,
};

/// Bem abaixo dos 64 KB do corpo: sem isto um request virava 32 mensagens.
const MAX_SEND_LENGTH: usize = 4000;
/// Teto do Discord para o valor de um field de embed.
const MAX_DM_FIELD: usize = 1024;
const DISCORD_MESSAGE: usize = 2000;
const LOG_CAPACITY: i64 = 1000;

fn guild_of(core: &CoreConfig) -> Option<serenity::GuildId> {
    (core.guild_id != 0).then(|| serenity::GuildId::new(core.guild_id))
}

/// Falha do Discord numa rota: o detalhe vai para o log, a frase para o painel.
fn failed(route: &str, cause: impl Display, message: &str) -> Reply {
    warn!("dashboard: {route}: {cause}");
    err(400, message)
}

fn user_json(session: &Session, with_expiry: bool) -> Value {
    let level = session.level();
    let mut user = json!({
        "id": session.user_id,
        "username": session.username,
        "avatarUrl": session.avatar_url,
        "permissionLevel": level,
        "permissionName": permission_name(level),
        "rank": session.rank(),
    });
    if with_expiry {
        user["expiresAt"] = json!(session.expires_at);
    }
    user
}

/// Os cargos do servidor, do cache quando da.
async fn guild_roles(
    ctx: &serenity::Context,
    guild: serenity::GuildId,
) -> Result<HashMap<serenity::RoleId, serenity::Role>, serenity::Error> {
    let cached = ctx.cache.guild(guild).map(|g| g.roles.clone());
    match cached {
        Some(roles) => Ok(roles),
        None => guild.roles(&ctx.http).await,
    }
}

pub async fn login(d: &Dashboard, req: &mut Req) -> Reply {
    // A trava vem antes de qualquer trabalho: e ela que impede varrer codigos.
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
    let Some(code) = body.string("linkCode").filter(|c| !c.trim().is_empty()) else {
        return err(400, "linkCode é obrigatório.");
    };
    let Some(pending) = d.gate.take_code(&code) else {
        return err(
            401,
            "Código inválido ou expirado. Gere um novo com /dashboardlink.",
        );
    };

    // Codigo valido: a origem deixa de estar sob suspeita.
    d.gate.login_succeeded(&req.headers, req.peer);

    finish_login(d, pending, "Login efetuado via link do bot.").await
}

/// O que vem depois de saber quem e a pessoa, igual para o codigo do
/// /dashboardlink e para o login com o Discord.
pub(super) async fn finish_login(d: &Dashboard, pending: PendingCode, how: &str) -> Reply {
    let resolution = d.auth.resolve_level(&d.bot.ctx, pending.user_id).await;
    if resolution.level <= 0 {
        if resolution.lookup_failed {
            return err(
                503,
                "Nao foi possivel validar permissoes no Discord agora. Tente novamente em alguns segundos.",
            );
        }
        return err(403, "Usuário sem permissão para o dashboard.");
    }

    // A whitelist e so registro: a autorizacao vem do Discord a cada vez.
    if let Err(cause) = d
        .auth
        .upsert_whitelist(pending.user_id, &pending.username, resolution.level)
        .await
    {
        warn!("dashboard: falha ao gravar a whitelist: {cause}");
    }

    let minutes = d
        .auth
        .read()
        .await
        .core
        .session_lifetime_minutes
        .clamp(5, 240);
    let session = d.gate.open_session(
        pending,
        resolution.level,
        resolution.rank,
        chrono::Duration::minutes(minutes),
        PERMISSION_REFRESH,
    );
    d.record(&session, "LOGIN", how).await;

    ok(
        200,
        json!({ "token": session.token, "user": user_json(&session, false) }),
    )
}

pub fn logout(d: &Dashboard, req: &mut Req) -> Reply {
    if let Some(session) = d.gate.session(&req.headers) {
        d.gate.close_session(&session.token);
    }
    ok(200, json!({ "ok": true }))
}

pub fn me(d: &Dashboard, req: &mut Req) -> Reply {
    let Some(session) = d.gate.session(&req.headers) else {
        return err(401, "Sessão inválida.");
    };
    ok(200, json!({ "user": user_json(&session, true) }))
}

/// O log do painel. Com reconferencia de nivel: quem perdeu o cargo nao
/// segue lendo ids e acoes ate a sessao vencer.
pub async fn panel_audit(d: &Dashboard, req: &mut Req) -> Reply {
    if d.require(&req.headers, 1).await.is_none() {
        return d.auth_failure(req, "Permissão insuficiente.");
    }
    ok(200, json!({ "logs": d.auth.audit(200).await }))
}

/// Partes de ate 2000 unidades UTF-16 sem partir um emoji ao meio: a metade
/// solta fazia o Discord recusar a segunda parte.
pub fn split_for_discord(message: &str) -> Vec<String> {
    let mut parts = Vec::new();
    let mut current = String::new();
    let mut units = 0;
    for c in message.chars() {
        if units + c.len_utf16() > DISCORD_MESSAGE {
            parts.push(std::mem::take(&mut current));
            units = 0;
        }
        current.push(c);
        units += c.len_utf16();
    }
    if !current.is_empty() {
        parts.push(current);
    }
    parts
}

pub async fn send_message(d: &Dashboard, req: &mut Req) -> Reply {
    let Some(session) = d.require(&req.headers, 1).await else {
        return d.auth_failure(req, "Permissão insuficiente.");
    };

    let body = req.json().await?;
    let channel_id = body.snowflake("channelId");
    let message = body.string("message").unwrap_or_default();
    let Some(channel_id) = channel_id.filter(|_| !message.trim().is_empty()) else {
        return err(400, "channelId e message são obrigatórios.");
    };

    let length = utf16_len(&message);
    if length > MAX_SEND_LENGTH {
        return err(
            400,
            format!("A mensagem tem {length} caracteres; o limite é {MAX_SEND_LENGTH}."),
        );
    }

    let ctx = &d.bot.ctx;
    let route = "/api/messages/send";
    let fail = "Falha ao enviar mensagem.";
    let channel = match serenity::ChannelId::new(channel_id).to_channel(ctx).await {
        Ok(channel) => channel,
        Err(cause) => return failed(route, cause, fail),
    };

    // So canal do servidor configurado: senao um nivel 1 fazia o bot escrever
    // em qualquer servidor onde ele esteja.
    let core = d.auth.read().await.core;
    let Some(channel) = channel
        .guild()
        .filter(|c| c.guild_id.get() == core.guild_id)
    else {
        return err(400, "Canal não pertence ao servidor do regimento.");
    };

    // Com a lista preenchida, nivel 1 fica restrito a ela; vazia nao restringe.
    let allowed = &core.allowed_send_channel_ids;
    if !allowed.is_empty() && !allowed.contains(&channel_id) && session.level() < 2 {
        return err(
            403,
            "Este canal não está na lista liberada para o seu nível.",
        );
    }

    let parts = split_for_discord(&message);
    for part in &parts {
        // Cargo e usuario continuam pingaveis; @everyone/@here nao.
        let message = serenity::CreateMessage::new()
            .content(part)
            .allowed_mentions(
                serenity::CreateAllowedMentions::new()
                    .all_roles(true)
                    .all_users(true),
            );
        if let Err(cause) = channel.id.send_message(ctx, message).await {
            return failed(route, cause, fail);
        }
    }

    d.record(
        &session,
        "SEND_MESSAGE",
        &format!(
            "Enviou mensagem para o canal {channel_id} em {} parte(s).",
            parts.len()
        ),
    )
    .await;
    ok(200, json!({ "ok": true }))
}

/// Canais e cargos do servidor para os menus do painel. IDs vao como texto:
/// um snowflake passa do maior inteiro exato do JavaScript.
pub async fn guild_options(d: &Dashboard, req: &mut Req) -> Reply {
    let Some(session) = d.require(&req.headers, 1).await else {
        return d.auth_failure(req, "Permissão insuficiente.");
    };

    let ctx = &d.bot.ctx;
    let route = "/api/guild/options";
    let fail = "Falha ao listar canais e cargos.";
    let core = d.auth.read().await.core;
    let Some(guild) = guild_of(&core) else {
        return failed(route, "guildId nao configurado", fail);
    };

    let cached = ctx.cache.guild(guild).map(|g| g.channels.clone());
    let channels = match cached {
        Some(channels) => channels,
        None => match guild.channels(&ctx.http).await {
            Ok(channels) => channels,
            Err(cause) => return failed(route, cause, fail),
        },
    };

    // O mesmo filtro do /api/messages/send: nivel 1 so ve o que pode usar.
    let allowed = &core.allowed_send_channel_ids;
    let restricted = !allowed.is_empty() && session.level() < 2;
    let category = |id: Option<serenity::ChannelId>| {
        id.and_then(|id| channels.get(&id))
            .map(|c| (c.position, c.name.clone()))
    };
    let mut text: Vec<_> = channels
        .values()
        .filter(|c| {
            matches!(
                c.kind,
                serenity::ChannelType::Text | serenity::ChannelType::News
            )
        })
        .filter(|c| !restricted || allowed.contains(&c.id.get()))
        .map(|c| (category(c.parent_id), c))
        .collect();
    text.sort_by_key(|(parent, c)| (parent.as_ref().map(|p| p.0), c.position, c.id));
    let channels: Vec<Value> = text
        .iter()
        .map(|(parent, c)| {
            json!({
                "id": c.id.to_string(),
                "name": c.name,
                "category": parent.as_ref().map(|p| p.1.clone()),
            })
        })
        .collect();

    // Cargos servem a DM em massa (nivel 2) e a /api/roles/* (nivel 1, ate a
    // propria patente).
    let mut roles: Vec<Value> = Vec::new();
    {
        let all = match guild_roles(ctx, guild).await {
            Ok(all) => all,
            Err(cause) => return failed(route, cause, fail),
        };
        let limit = if session.level() >= 4 {
            None
        } else {
            match guild
                .member(ctx, serenity::UserId::new(session.user_id))
                .await
            {
                Ok(me) => Some(rank_position(&core, &all, &me.roles).unwrap_or(0)),
                Err(cause) => return failed(route, cause, fail),
            }
        };
        let bot_top = bot_top_position(ctx, guild, &all).await;
        let mut sorted: Vec<_> = all
            .values()
            .filter(|r| r.id.get() != guild.get() && !r.managed)
            .collect();
        sorted.sort_by(|a, b| b.position.cmp(&a.position).then(a.id.cmp(&b.id)));
        // O servidor separa os cargos com cargos-titulo ("Permissions",
        // "Honours"...). Tudo acima do primeiro separador sao as patentes.
        let mut category = "Patentes".to_owned();
        for r in sorted {
            if let Some(title) = separator_title(&r.name) {
                category = title;
                continue;
            }
            // Honrarias e medalhas nao se dao pelo painel.
            if is_honours(&category) {
                continue;
            }
            let refusal = role_refusal(
                r.id.get(),
                r.permissions,
                &d.grantable,
                &core,
                &d.bot.config,
            )
            .or_else(|| {
                bot_top
                    .filter(|&top| r.position >= top)
                    .map(|_| "Acima do cargo do bot no servidor.")
            })
            .or_else(|| {
                limit
                    .filter(|&limit| r.position >= limit)
                    .map(|_| "Acima da sua patente.")
            });
            roles.push(json!({
                "id": r.id.to_string(),
                "name": r.name,
                "category": category,
                "color": (r.colour.0 != 0).then(|| r.colour.hex()),
                "grantable": refusal.is_none(),
                "refusal": refusal,
            }));
        }
    }

    ok(200, json!({ "channels": channels, "roles": roles }))
}

/// Cargo-titulo: o nome real vem cercado de espacos e caracteres invisiveis
/// (U+2063), so para alinhar no Discord. Devolve o titulo limpo.
fn separator_title(name: &str) -> Option<String> {
    let padding = |c: char| c.is_whitespace() || matches!(c, '\u{2063}' | '\u{200b}' | '\u{2800}');
    let leading = name.chars().take_while(|&c| padding(c)).count();
    let title = name.trim_matches(padding);
    (leading >= 3 && !title.is_empty()).then(|| title.to_owned())
}

fn is_honours(category: &str) -> bool {
    let lower = category.to_lowercase();
    lower.contains("honour") || lower.contains("award")
}

/// Posicao do cargo mais alto do bot: o Discord so deixa ele dar ou tirar
/// cargos abaixo disso.
async fn bot_top_position(
    ctx: &serenity::Context,
    guild: serenity::GuildId,
    roles: &HashMap<serenity::RoleId, serenity::Role>,
) -> Option<u16> {
    let me = ctx.cache.current_user().id;
    let member = guild.member(ctx, me).await.ok()?;
    member
        .roles
        .iter()
        .filter_map(|id| roles.get(id))
        .map(|role| role.position)
        .max()
}

/// Posicao da patente mais alta da pessoa (os cargos dos niveis 1 a 3). None
/// e quem nao tem patente nenhuma.
fn rank_position(
    core: &CoreConfig,
    roles: &HashMap<serenity::RoleId, serenity::Role>,
    member_roles: &[serenity::RoleId],
) -> Option<u16> {
    member_roles
        .iter()
        .filter(|id| {
            let id = id.get();
            core.level1_role_ids.contains(&id)
                || core.level2_role_ids.contains(&id)
                || core.level3_role_ids.contains(&id)
        })
        .filter_map(|id| roles.get(id))
        .map(|role| role.position)
        .max()
}

/// Por que um cargo NAO pode passar por /api/roles/*. A allowlist sozinha
/// bastaria, mas ela e digitada a mao, e o erro provavel e listar justo o
/// cargo de staff: aqui ele e portao do painel, do /enlistuser e do deployment
/// ao mesmo tempo. Por isso a recusa e por papel, nao so por lista.
pub fn role_refusal(
    role_id: u64,
    permissions: serenity::Permissions,
    grantable: &std::collections::HashSet<u64>,
    core: &CoreConfig,
    bot: &Config,
) -> Option<&'static str> {
    if !grantable.contains(&role_id) {
        return Some("Este cargo não está em grantableRoleIds.");
    }
    // As patentes passam: quem pede so mexe abaixo da propria (ver
    // rank_position). O que abre o painel de verdade e o 12 Staff.
    if core.staff_role_ids.contains(&role_id) {
        return Some("Este cargo dá acesso ao próprio painel.");
    }
    if bot.enlist_permission_role_id == Some(role_id) {
        return Some("Este cargo é o portão dos comandos de staff.");
    }
    if bot.deployment_allowed_role_ids.contains(&role_id) {
        return Some("Este cargo autoriza deployment e DM em massa.");
    }
    // Mexer nestes muda quem pode se realistar: decisao de comando.
    if bot.roblox_enlist_blocked_role_ids.contains(&role_id) {
        return Some("Este cargo controla quem pode se alistar.");
    }
    let dangerous = serenity::Permissions::ADMINISTRATOR
        | serenity::Permissions::MANAGE_ROLES
        | serenity::Permissions::MANAGE_GUILD;
    if permissions.intersects(dangerous) {
        return Some("Este cargo carrega Administrator, Manage Roles ou Manage Server.");
    }
    None
}

/// Conceder cargo pode escalar privilegio: nivel 2 e, mesmo assim, so os
/// cargos que passam em `role_refusal`.
pub async fn change_role(d: &Dashboard, req: &mut Req, add: bool) -> Reply {
    let Some(session) = d.require(&req.headers, 1).await else {
        return d.auth_failure(req, "Permissão insuficiente para gerenciar cargos.");
    };

    let body = req.json().await?;
    let reason = body.trimmed("reason").filter(|r| !r.is_empty());
    let (Some(user_id), Some(role_id)) = (body.snowflake("userId"), body.snowflake("roleId"))
    else {
        return err(400, "userId e roleId são obrigatórios.");
    };

    let ctx = &d.bot.ctx;
    let route = if add {
        "/api/roles/add"
    } else {
        "/api/roles/remove"
    };
    let fail = "Falha ao alterar cargo.";
    let core = d.auth.read().await.core;
    let Some(guild) = guild_of(&core) else {
        return failed(route, "guildId nao configurado", fail);
    };
    let user = serenity::UserId::new(user_id);
    let target = match guild.member(ctx, user).await {
        Ok(member) => member,
        Err(cause) => return failed(route, cause, fail),
    };
    let roles = match guild_roles(ctx, guild).await {
        Ok(roles) => roles,
        Err(cause) => return failed(route, cause, fail),
    };
    let role = roles.get(&serenity::RoleId::new(role_id)).cloned();
    let Some(role) = role else {
        return err(400, format!("Cargo {role_id} não existe neste servidor."));
    };

    // A hierarquia da patente: ninguem mexe em cargo da propria patente para
    // cima, nem em quem tem patente igual ou maior. O Developer nao tem patente
    // no painel e fica so com as outras travas.
    let above_bot = bot_top_position(ctx, guild, &roles)
        .await
        .filter(|&top| role.position >= top)
        .map(|_| "Este cargo está acima do cargo do bot no servidor.");
    let hierarchy = if above_bot.is_some() {
        above_bot
    } else if session.level() >= 4 {
        None
    } else {
        let limit = match guild
            .member(ctx, serenity::UserId::new(session.user_id))
            .await
        {
            Ok(me) => rank_position(&core, &roles, &me.roles),
            Err(cause) => return failed(route, cause, fail),
        };
        match limit {
            None => Some("Você não tem patente para mexer em cargos."),
            Some(limit) if role.position >= limit => {
                Some("Você só pode mexer em cargos abaixo da sua patente.")
            }
            Some(limit)
                if rank_position(&core, &roles, &target.roles).is_some_and(|p| p >= limit) =>
            {
                Some("Não dá para mexer em quem tem patente igual ou maior que a sua.")
            }
            Some(_) => None,
        }
    };

    if let Some(refusal) = role_refusal(
        role_id,
        role.permissions,
        &d.grantable,
        &core,
        &d.bot.config,
    )
    .or(hierarchy)
    {
        // 403: o pedido esta certo, falta autorizacao para ESTE cargo.
        warn!(
            "dashboard: {} tentou {} o cargo {role_id} ({}): recusado - {refusal}",
            session.username,
            if add { "conceder" } else { "remover" },
            role.name
        );
        d.record(
            &session,
            if add {
                "ADD_ROLE_DENIED"
            } else {
                "REMOVE_ROLE_DENIED"
            },
            &format!(
                "Recusado: cargo {role_id} ({}) para o usuário {user_id}. {refusal}",
                role.name
            ),
        )
        .await;
        return err(403, format!("Cargo não permitido pelo painel. {refusal}"));
    }

    let result = if add {
        ctx.http
            .add_member_role(guild, user, role.id, reason.as_deref())
            .await
    } else {
        ctx.http
            .remove_member_role(guild, user, role.id, reason.as_deref())
            .await
    };
    if let Err(cause) = result {
        return failed(route, cause, fail);
    }

    d.record(
        &session,
        if add { "ADD_ROLE" } else { "REMOVE_ROLE" },
        &format!(
            "{} cargo {role_id} do usuário {user_id}.",
            if add { "Adicionou" } else { "Removeu" }
        ),
    )
    .await;
    ok(200, json!({ "ok": true }))
}

/// Aviso por DM. Falha e silenciosa: DM fechada nao desfaz a punicao.
async fn punishment_dm(
    ctx: &serenity::Context,
    user: &serenity::User,
    kind: &str,
    reason: Option<&str>,
) {
    let mut text = format!("Você recebeu a punição: {kind}.");
    if let Some(reason) = reason.filter(|r| !r.trim().is_empty()) {
        text.push_str(&format!(" Motivo: {reason}"));
    }
    let _ = user
        .direct_message(ctx, serenity::CreateMessage::new().content(text))
        .await;
}

pub async fn timeout(d: &Dashboard, req: &mut Req) -> Reply {
    let Some(session) = d.require(&req.headers, 2).await else {
        return d.auth_failure(req, "Permissão insuficiente para timeout.");
    };

    let body = req.json().await?;
    let reason = body.trimmed("reason");
    let (Some(user_id), Ok(Some(minutes))) =
        (body.snowflake("userId"), body.count("durationMinutes"))
    else {
        return err(400, "userId e durationMinutes válidos são obrigatórios.");
    };
    if minutes <= 0 {
        return err(400, "userId e durationMinutes válidos são obrigatórios.");
    }

    let ctx = &d.bot.ctx;
    let route = "/api/punishments/timeout";
    let fail = "Falha ao aplicar timeout.";
    let core = d.auth.read().await.core;
    let Some(guild) = guild_of(&core) else {
        return failed(route, "guildId nao configurado", fail);
    };
    let member = match guild.member(ctx, serenity::UserId::new(user_id)).await {
        Ok(member) => member,
        Err(cause) => return failed(route, cause, fail),
    };

    // 40320 minutos sao os 28 dias que o Discord aceita.
    let until = Utc::now() + chrono::Duration::minutes(i64::from(minutes.clamp(1, 40320)));
    let Ok(until) = serenity::Timestamp::from_unix_timestamp(until.timestamp()) else {
        return failed(route, "data fora do intervalo", fail);
    };
    let mut edit = serenity::EditMember::new().disable_communication_until_datetime(until);
    if let Some(reason) = reason.as_deref().filter(|r| !r.is_empty()) {
        edit = edit.audit_log_reason(reason);
    }
    if let Err(cause) = guild.edit_member(ctx, member.user.id, edit).await {
        return failed(route, cause, fail);
    }

    punishment_dm(ctx, &member.user, "Timeout", reason.as_deref()).await;
    d.record(
        &session,
        "TIMEOUT",
        &format!("Aplicou timeout em {user_id} por {minutes} minuto(s)."),
    )
    .await;
    ok(200, json!({ "ok": true }))
}

pub async fn remove_from_regiment(d: &Dashboard, req: &mut Req) -> Reply {
    let Some(session) = d.require(&req.headers, 3).await else {
        return d.auth_failure(
            req,
            "Remover do regimento exige acesso total (Captain ou acima).",
        );
    };

    let body = req.json().await?;
    let reason = body.trimmed("reason");
    let Some(user_id) = body.snowflake("userId") else {
        return err(400, "userId é obrigatório.");
    };

    let ctx = &d.bot.ctx;
    let route = "/api/punishments/remove-from-regiment";
    let fail = "Falha ao remover usuário do regimento.";
    let core = d.auth.read().await.core;
    let Some(guild) = guild_of(&core) else {
        return failed(route, "guildId nao configurado", fail);
    };
    let member = match guild.member(ctx, serenity::UserId::new(user_id)).await {
        Ok(member) => member,
        Err(cause) => return failed(route, cause, fail),
    };
    let roles = match guild_roles(ctx, guild).await {
        Ok(roles) => roles,
        Err(cause) => return failed(route, cause, fail),
    };

    let audit_reason = reason.as_deref().filter(|r| !r.is_empty());
    for &id in core.regiment_role_ids.iter().filter(|&&id| id != 0) {
        let role = serenity::RoleId::new(id);
        if roles.contains_key(&role)
            && member.roles.contains(&role)
            && let Err(cause) = ctx
                .http
                .remove_member_role(guild, member.user.id, role, audit_reason)
                .await
        {
            return failed(route, cause, fail);
        }
    }

    punishment_dm(ctx, &member.user, "Remoção do regimento", reason.as_deref()).await;
    d.record(
        &session,
        "REMOVE_FROM_REGIMENT",
        &format!("Removeu do regimento o usuário {user_id}."),
    )
    .await;
    ok(200, json!({ "ok": true }))
}

fn log_counts(d: &Dashboard) -> Value {
    let counts = d.bot.logs.counts();
    json!({
        "info": counts.info,
        "aviso": counts.aviso,
        "erro": counts.erro,
        "totalSeen": d.bot.logs.total_seen(),
    })
}

/// A unica rota sem login alem do health: alimenta ccore.daeese.me/status. O
/// corpo padrao so tem o que qualquer membro ja ve; dados da maquina pedem
/// `?detail=host` e nivel 2, o mesmo criterio do -osinfo.
pub async fn status(d: &Dashboard, req: &mut Req) -> Reply {
    let wants_host = req
        .query("detail")
        .is_some_and(|v| v.eq_ignore_ascii_case("host"));
    if !wants_host {
        return ok(
            200,
            json!({ "bot": d.public_status().await, "roster": roster_totals(d).await }),
        );
    }

    if d.require(&req.headers, 2).await.is_none() {
        return d.auth_failure(req, "Permissão insuficiente para ver dados da máquina.");
    }
    ok(
        200,
        json!({
            "bot": d.fresh_status().await,
            "host": crate::status::host().await,
            "logs": log_counts(d),
        }),
    )
}

/// So os totais do efetivo, sem nomes: a pagina de status e publica.
async fn roster_totals(d: &Dashboard) -> Option<Value> {
    let audit = d.bot.audit.read_audit().await.ok()?;
    let promotable = audit
        .entries
        .iter()
        .map(crate::audit::ladder::evaluate)
        .filter(|r| r.is_promotable())
        .count();
    Some(json!({ "total": audit.entries.len(), "promotable": promotable }))
}

/// Inteiro da query string, com padrao e limites.
fn query_int(raw: Option<&str>, fallback: i64, min: i64, max: i64) -> i64 {
    raw.and_then(|v| v.trim().parse::<i64>().ok())
        .map_or(fallback, |v| v.clamp(min, max))
}

fn parse_severity(raw: Option<&str>) -> Option<Severity> {
    match raw?.trim().to_lowercase().as_str() {
        "erro" | "error" => Some(Severity::Erro),
        "aviso" | "warn" | "warning" => Some(Severity::Aviso),
        "info" => Some(Severity::Info),
        _ => None,
    }
}

/// "[gw] texto" tem a tag "gw", como no C#.
fn tag(text: &str) -> &str {
    if text.len() < 3 || !text.starts_with('[') {
        return "";
    }
    match text.find(']') {
        Some(end) if end > 1 && end <= 24 => &text[1..end],
        _ => "",
    }
}

/// Logs carregam ids, mensagens de erro e caminhos da maquina: nivel 2.
pub async fn logs(d: &Dashboard, req: &mut Req) -> Reply {
    if d.require(&req.headers, 2).await.is_none() {
        return d.auth_failure(req, "Permissão insuficiente para ver os logs do bot.");
    }

    let take = query_int(req.query("take"), 100, 1, LOG_CAPACITY) as usize;
    let severity = parse_severity(req.query("level"));
    let lines: Vec<Value> = d
        .bot
        .logs
        .snapshot(take, severity, req.query("q"))
        .into_iter()
        .map(|line| {
            json!({
                "timestampUtc": line.at.with_timezone(&Utc),
                "level": line.severity.name(),
                "tag": tag(&line.text),
                "text": line.text,
            })
        })
        .collect();
    ok(200, json!({ "logs": lines, "counts": log_counts(d) }))
}

/// A mesma mensagem do /deployment. Notifica, mas nao remove ninguem.
pub async fn deployment(d: &Dashboard, req: &mut Req) -> Reply {
    let Some(session) = d.require(&req.headers, 1).await else {
        return d.auth_failure(req, "Permissão insuficiente para enviar deployment.");
    };

    let body = req.json().await?;
    let Some(codigo) = body.trimmed("codigo").filter(|c| !c.is_empty()) else {
        return err(400, "codigo é obrigatório.");
    };

    let ctx = &d.bot.ctx;
    let config = &d.bot.config;
    let route = "/api/deployment";
    let fail = "Falha ao enviar o deployment.";
    let core = d.auth.read().await.core;
    let Some(guild) = guild_of(&core) else {
        return failed(route, "guildId nao configurado", fail);
    };

    let channel = match deployment::channel(ctx, guild, config).await {
        Ok(channel) => channel,
        Err(cause) => return err(400, cause),
    };
    let Some((roles, resenha)) = deployment::guild_assets(&ctx.cache, guild) else {
        return failed(route, "servidor fora do cache", fail);
    };

    let built = deployment::build(config, &codigo, &roles, resenha.as_deref());
    if let Err(cause) = channel.id.send_message(ctx, built.message).await {
        return failed(route, cause, fail);
    }

    d.record(
        &session,
        "DEPLOYMENT",
        &format!(
            "Enviou deployment com código '{codigo}' para o canal {} ({} cargo(s) pingado(s)).",
            channel.id, built.roles_pinged
        ),
    )
    .await;
    ok(
        200,
        json!({
            "ok": true,
            "channelId": channel.id.to_string(),
            "rolesPinged": built.roles_pinged,
            "warning": built.image_warning,
        }),
    )
}

/// Responde com o jobId em vez de esperar: 500 destinatarios levam ~35 min.
pub async fn dm_start(d: &Dashboard, req: &mut Req) -> Reply {
    let Some(session) = d.require(&req.headers, 2).await else {
        return d.auth_failure(req, "Permissão insuficiente para enviar DM em massa.");
    };

    let body = req.json().await?;
    let role_id = body.snowflake("roleId");
    let user_id = body.snowflake("userId");
    let message = body.trimmed("message").unwrap_or_default();
    let code = body.trimmed("code").unwrap_or_default();
    if role_id.is_none() && user_id.is_none() {
        return err(400, "Informe roleId ou userId.");
    }

    // Os dois viram field de embed, e o Discord recusa field acima de 1024:
    // sem isto, as 500 DMs falhavam uma a uma.
    let length = utf16_len(&message);
    if length > MAX_DM_FIELD {
        return err(
            400,
            format!("A mensagem tem {length} caracteres; o limite por DM é {MAX_DM_FIELD}."),
        );
    }
    let length = utf16_len(&code);
    if length > MAX_DM_FIELD {
        return err(
            400,
            format!("O código tem {length} caracteres; o limite por DM é {MAX_DM_FIELD}."),
        );
    }

    let ctx = &d.bot.ctx;
    let route = "/api/dm";
    let fail = "Falha ao iniciar o envio.";
    let core = d.auth.read().await.core;
    let Some(guild) = guild_of(&core) else {
        return failed(route, "guildId nao configurado", fail);
    };

    let role = match role_id {
        None => None,
        Some(id) => match guild_roles(ctx, guild).await {
            Ok(roles) => match roles.get(&serenity::RoleId::new(id)) {
                Some(role) => Some(role.clone()),
                None => return err(400, format!("Cargo {id} não existe neste servidor.")),
            },
            Err(cause) => return failed(route, cause, fail),
        },
    };
    let user = match user_id {
        None => None,
        Some(id) => match serenity::UserId::new(id).to_user(ctx).await {
            Ok(user) => Some(user),
            Err(cause) => return failed(route, cause, fail),
        },
    };

    // Resolve AQUI: alvo invalido falha no request, nao dentro do job.
    let recipients = match dm::resolve(ctx, guild, role.as_ref(), user.as_ref()).await {
        Ok(recipients) => recipients,
        Err(cause) => return err(400, cause),
    };
    let (count, target) = (recipients.members.len(), recipients.name.clone());

    // Link no content, e nao no embed, para o Discord montar o preview.
    let mut dm_message =
        serenity::CreateMessage::new().embed(deployment_dm(&target, &code, &message));
    if let Some(link) = d
        .bot
        .config
        .default_game_link
        .as_deref()
        .filter(|l| !l.trim().is_empty())
    {
        dm_message = dm_message.content(link);
    }

    let job = match d.jobs.start(
        ctx.clone(),
        d.bot.dm_limiter.clone(),
        recipients,
        dm_message,
    ) {
        Ok(job) => job,
        Err(cause) => return err(429, cause),
    };

    d.record(
        &session,
        "MASS_DM",
        &format!(
            "Iniciou envio de DM para {count} destinatário(s) ({target}) — job {}.",
            job.job_id
        ),
    )
    .await;
    ok(202, json!({ "ok": true, "job": job }))
}

pub async fn dm_status(d: &Dashboard, req: &mut Req) -> Reply {
    if d.require(&req.headers, 2).await.is_none() {
        return d.auth_failure(req, "Permissão insuficiente.");
    }

    let Some(id) = req.query("jobId").filter(|id| !id.trim().is_empty()) else {
        return ok(200, json!({ "jobs": d.jobs.recent() }));
    };
    match d.jobs.get(id) {
        Some(job) => ok(200, json!({ "job": job })),
        None => err(404, "Job não encontrado ou já expirado."),
    }
}

pub async fn dm_cancel(d: &Dashboard, req: &mut Req) -> Reply {
    let Some(session) = d.require(&req.headers, 2).await else {
        return d.auth_failure(req, "Permissão insuficiente.");
    };

    let body = req.json().await?;
    let Some(id) = body.trimmed("jobId").filter(|id| !id.is_empty()) else {
        return err(400, "Informe jobId.");
    };
    if !d.jobs.cancel(&id) {
        return err(404, "Job não encontrado ou já finalizado.");
    }

    d.record(
        &session,
        "MASS_DM_CANCEL",
        &format!("Solicitou cancelamento do envio de DM — job {id}."),
    )
    .await;
    ok(200, json!({ "ok": true, "job": d.jobs.get(&id) }))
}

/// A mesma verificacao ROBLOX do /enlistuser. Consulta API externa, da cargo
/// e muda apelido: nivel 2.
pub async fn enlist(d: &Dashboard, req: &mut Req) -> Reply {
    let Some(session) = d.require(&req.headers, 2).await else {
        return d.auth_failure(req, "Permissão insuficiente para alistar.");
    };

    let body = req.json().await?;
    let user_id = body.snowflake("userId");
    let roblox_name = body.trimmed("robloxUsername").unwrap_or_default();
    let social_role = body.boolean("socialRole");
    let Some(user_id) = user_id.filter(|_| !roblox_name.is_empty()) else {
        return err(400, "userId e robloxUsername são obrigatórios.");
    };

    let ctx = &d.bot.ctx;
    let config = &d.bot.config;
    let core = d.auth.read().await.core;
    let member = match guild_of(&core) {
        Some(guild) => guild
            .member(ctx, serenity::UserId::new(user_id))
            .await
            .ok()
            .map(|m| (guild, m)),
        None => None,
    };
    let Some((guild, member)) = member else {
        return err(400, "O usuário informado não é membro do servidor.");
    };

    let check = match enlistment::verify_roblox(&d.bot.http, &roblox_name).await {
        Ok(check) => check,
        Err(cause) => return err(400, cause),
    };

    // Reprovado nao e erro do chamador: a consulta funcionou e a resposta e
    // "negado". 200 para o painel mostrar os numeros que motivaram.
    if !check.is_likely_main() {
        return ok(
            200,
            json!({
                "ok": false,
                "approved": false,
                "reason": "Conta ROBLOX não atende aos critérios mínimos de confiabilidade.",
                "accountAgeDays": check.account_age_days,
                "friends": check.friends,
                "badges": check.badges_display(),
            }),
        );
    }

    let applied = enlistment::apply(ctx, guild, &member, config, social_role, None).await;
    let log = enlistment::log_embed(
        ctx,
        &member,
        &check,
        &applied,
        LogDetails {
            executor: format!("<@{}> (dashboard)", session.user_id),
            roblox_name: &roblox_name,
            social_role,
            title: "12° Regiment - Recruit Log",
            description: "Registro de alistamento realizado com sucesso.",
            extra_fields: Vec::new(),
        },
    );
    let announce_warnings = enlistment::announce(ctx, guild, config, log, &member).await;

    let role_names: Vec<String> = {
        let roles = ctx.cache.guild(guild).map(|g| g.roles.clone());
        applied
            .added_roles
            .iter()
            .map(|id| {
                roles
                    .as_ref()
                    .and_then(|r| r.get(id))
                    .map_or_else(|| id.to_string(), |r| r.name.clone())
            })
            .collect()
    };

    d.record(
        &session,
        "ENLIST",
        &format!(
            "Alistou {user_id} como '{roblox_name}' ({} cargo(s)).",
            applied.added_roles.len()
        ),
    )
    .await;

    let warnings: Vec<&String> = applied.warnings.iter().chain(&announce_warnings).collect();
    ok(
        200,
        json!({
            "ok": true,
            "approved": true,
            "accountAgeDays": check.account_age_days,
            "friends": check.friends,
            "badges": check.badges_display(),
            "rolesAdded": role_names,
            "nicknameChanged": applied.nickname_changed,
            "warnings": warnings,
        }),
    )
}

#[cfg(test)]
mod tests {

    #[test]
    fn titulo_de_categoria() {
        assert_eq!(
            separator_title(
                "\u{2063}       \u{2063}    Permissions\u{2063}\u{2063}           \u{2063}"
            )
            .as_deref(),
            Some("Permissions")
        );
        assert_eq!(separator_title("Captain"), None);
        assert_eq!(separator_title("   "), None);
        assert!(is_honours("Honours"));
        assert!(!is_honours("Classification"));
    }

    use super::*;
    use std::collections::HashSet;

    fn bot(json: &str) -> Config {
        let mut value: serde_json::Value = serde_json::from_str(json).unwrap();
        value["token"] = "x".into();
        serde_json::from_value(value).unwrap()
    }

    #[test]
    fn divide_em_2000_sem_partir_emoji() {
        assert!(split_for_discord("").is_empty());
        let plain = "a".repeat(4001);
        let parts = split_for_discord(&plain);
        assert_eq!(
            parts.iter().map(|p| p.len()).collect::<Vec<_>>(),
            [2000, 2000, 1]
        );

        let emoji = format!("{}😀b", "a".repeat(1999));
        let parts = split_for_discord(&emoji);
        assert_eq!(parts[0], "a".repeat(1999));
        assert_eq!(parts[1], "😀b");
    }

    #[test]
    fn cargo_so_passa_se_listado_e_sem_papel_de_portao() {
        let grantable: HashSet<u64> = [1, 2, 3, 4, 5, 6].into();
        let core: CoreConfig =
            serde_json::from_str(r#"{"staffRoleIds":[2],"level3RoleIds":[6]}"#).unwrap();
        let bot = bot(
            r#"{"enlistPermissionRoleId":3,"deploymentAllowedRoleIds":[4],
                "robloxEnlistBlockedRoleIds":[5]}"#,
        );
        let none = serenity::Permissions::empty();
        let refuse = |id, perms| role_refusal(id, perms, &grantable, &core, &bot);

        assert_eq!(refuse(1, none), None);
        assert_eq!(
            refuse(9, none),
            Some("Este cargo não está em grantableRoleIds.")
        );
        assert_eq!(
            refuse(2, none),
            Some("Este cargo dá acesso ao próprio painel.")
        );
        // Patente passa; o limite dela e a hierarquia de quem pede.
        assert_eq!(refuse(6, none), None);
        assert_eq!(
            refuse(3, none),
            Some("Este cargo é o portão dos comandos de staff.")
        );
        assert_eq!(
            refuse(4, none),
            Some("Este cargo autoriza deployment e DM em massa.")
        );
        assert_eq!(
            refuse(5, none),
            Some("Este cargo controla quem pode se alistar.")
        );
        assert!(refuse(1, serenity::Permissions::MANAGE_ROLES).is_some());
        assert!(refuse(1, serenity::Permissions::SEND_MESSAGES).is_none());
    }

    #[test]
    fn query_logs_e_tag() {
        assert_eq!(query_int(Some(" 50 "), 100, 1, 1000), 50);
        assert_eq!(query_int(Some("9999"), 100, 1, 1000), 1000);
        assert_eq!(query_int(Some("x"), 100, 1, 1000), 100);
        assert_eq!(parse_severity(Some("WARN")), Some(Severity::Aviso));
        assert_eq!(parse_severity(Some("tudo")), None);
        assert_eq!(tag("[gw] caiu"), "gw");
        assert_eq!(tag("sem tag"), "");
        assert_eq!(tag("[]"), "");
    }
}
