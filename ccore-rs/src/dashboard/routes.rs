//! Login, moderacao, status, logs, deployment, DM em massa e alistamento.

use std::{collections::HashMap, fmt::Display};

use chrono::Utc;
use poise::serenity_prelude as serenity;
use serde_json::{Value, json};
use tracing::warn;

use super::{
    Dashboard, PERMISSION_REFRESH,
    auth::{CoreConfig, permission_name},
    gate::{Session, retry_secs},
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
        chrono::Duration::minutes(minutes),
        PERMISSION_REFRESH,
    );
    d.record(&session, "LOGIN", "Login efetuado via link do bot.")
        .await;

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
    if core.level1_role_ids.contains(&role_id)
        || core.level2_role_ids.contains(&role_id)
        || core.level3_role_ids.contains(&role_id)
    {
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
    let Some(session) = d.require(&req.headers, 2).await else {
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
    if let Err(cause) = guild.member(ctx, user).await {
        return failed(route, cause, fail);
    }
    let role = match guild_roles(ctx, guild).await {
        Ok(roles) => roles.get(&serenity::RoleId::new(role_id)).cloned(),
        Err(cause) => return failed(route, cause, fail),
    };
    let Some(role) = role else {
        return err(400, format!("Cargo {role_id} não existe neste servidor."));
    };

    if let Some(refusal) = role_refusal(
        role_id,
        role.permissions,
        &d.grantable,
        &core,
        &d.bot.config,
    ) {
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
    let Some(session) = d.require(&req.headers, 2).await else {
        return d.auth_failure(req, "Permissão insuficiente para remover do regimento.");
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
        return ok(200, json!({ "bot": d.public_status().await }));
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
            serde_json::from_str(r#"{"level1RoleIds":[2],"level3RoleIds":[6]}"#).unwrap();
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
        assert_eq!(
            refuse(6, none),
            Some("Este cargo dá acesso ao próprio painel.")
        );
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
