//! A API HTTP que o painel (dashboard.daeese.me e ccore.daeese.me) consome.
//! Rotas, formatos e mensagens sao os do bot em C#: o front nao mudou.

mod audit_routes;
pub mod auth;
mod gate;
mod jobs;
mod request;
mod routes;

use std::{
    collections::HashSet,
    net::SocketAddr,
    sync::{Arc, Mutex},
    time::{Duration, Instant},
};

use axum::{
    Router,
    extract::{Request, State},
    http::{HeaderMap, HeaderValue, Method, StatusCode, header},
    middleware::{self, Next},
    response::{IntoResponse, Response},
};
use chrono::Utc;
use poise::serenity_prelude as serenity;
use serde_json::{Value, json};
use tokio::{net::TcpListener, sync::oneshot, task::JoinHandle};
use tracing::{error, info, warn};

use self::{
    auth::{AuditEntry, AuthStore, Resolution},
    gate::{Gate, PendingCode, Session, retry_secs},
    jobs::Jobs,
    request::{Fail, Req, error as error_response, json_response, with_retry_after},
};
use crate::{audit::store::AuditStore, config::Config, dm::DmLimiter, log_buffer::LogBuffer};

/// De quanto em quanto tempo o nivel de uma sessao viva e reconferido.
const PERMISSION_REFRESH: Duration = Duration::from_secs(5 * 60);
/// Espera curta depois de uma consulta que FALHOU: e o tempo em que quem
/// perdeu o cargo segue com o nivel antigo, mas evita repagar as tres
/// tentativas em cada request durante uma instabilidade do Discord.
const PERMISSION_RETRY: Duration = Duration::from_secs(30);
const IN_FLIGHT_WAIT: Duration = Duration::from_secs(10);
/// Teto da espera pelas requisicoes em voo: o systemd da 30s ao processo todo.
const STOP_LIMIT: Duration = Duration::from_secs(3);

/// O que o dashboard usa do bot.
pub struct Deps {
    pub ctx: serenity::Context,
    pub shards: Arc<serenity::ShardManager>,
    pub config: Arc<Config>,
    pub logs: Arc<LogBuffer>,
    pub http: reqwest::Client,
    pub dm_limiter: Arc<DmLimiter>,
    pub audit: Arc<AuditStore>,
    pub github_token: Arc<tokio::sync::Mutex<Option<String>>>,
}

pub struct Dashboard {
    bot: Deps,
    auth: AuthStore,
    gate: Arc<Gate>,
    grantable: HashSet<u64>,
    jobs: Jobs,
    /// A pagina publica de status nao tem login: o cache impede que refresh
    /// agressivo vire trabalho repetido. 10s ainda mostra queda na hora.
    status: Mutex<Option<(Instant, Value)>>,
}

/// O servidor em pe, para o desligamento.
pub struct Server {
    stop: oneshot::Sender<()>,
    task: JoinHandle<()>,
}

impl Server {
    /// Para de aceitar e espera o que esta em voo, com teto.
    pub async fn stop(self) {
        let _ = self.stop.send(());
        if tokio::time::timeout(STOP_LIMIT, self.task).await.is_err() {
            warn!("dashboard: requisicoes em voo nao terminaram em {STOP_LIMIT:?}");
        }
    }
}

/// "http://127.0.0.1:5056/" vira o endereco do bind. `+` e `*` do
/// HttpListener querem dizer todas as interfaces.
fn bind_address(listen_url: &str) -> Result<String, String> {
    let url = listen_url.trim();
    let url = if url.is_empty() {
        auth::DEFAULT_LISTEN
    } else {
        url
    };
    let rest = url
        .strip_prefix("http://")
        .ok_or_else(|| format!("listenUrl precisa comecar com http:// ({url})"))?;
    let authority = rest.split('/').next().unwrap_or_default();
    let (host, port) = match authority.rsplit_once(':') {
        Some((host, port)) if !port.contains(']') => (host, port),
        _ => (authority, "80"),
    };
    let host = match host {
        "+" | "*" => "0.0.0.0",
        "" => return Err(format!("listenUrl sem host ({url})")),
        other => other,
    };
    Ok(format!("{host}:{port}"))
}

/// Sobe o servidor. Err e o motivo para o log: o bot segue sem o dashboard.
pub async fn start(bot: Deps) -> Result<(Arc<Dashboard>, Server), String> {
    let auth = AuthStore::new(auth::AUTH_PATH);
    let core = auth.read().await.core;
    let origins = gate::origins(&core)?;
    let address = bind_address(&core.listen_url)?;

    if core.tunnel_secret.trim().is_empty() {
        warn!(
            "dashboard: tunnelSecret vazio: o limite de login volta a ser global, o orcamento por cliente fica desligado e o acesso direto a api.daeese.me (sem passar pelo Worker) deixa de ser recusado"
        );
    }
    if core.grantable_role_ids.is_empty() {
        warn!(
            "dashboard: grantableRoleIds vazio: /api/roles/add e /api/roles/remove vao recusar tudo"
        );
    }

    let listener = TcpListener::bind(&address)
        .await
        .map_err(|e| format!("nao consegui escutar em {address}: {e}"))?;

    let dashboard = Arc::new(Dashboard {
        bot,
        gate: Arc::new(Gate::new(origins, &core.tunnel_secret)),
        grantable: core.grantable_role_ids.iter().copied().collect(),
        auth,
        jobs: Jobs::default(),
        status: Mutex::default(),
    });

    let app = router(dashboard.clone()).into_make_service_with_connect_info::<SocketAddr>();
    let (stop, stopped) = oneshot::channel::<()>();
    let task = tokio::spawn(async move {
        let result = axum::serve(listener, app)
            .with_graceful_shutdown(async {
                let _ = stopped.await;
            })
            .await;
        if let Err(cause) = result {
            error!("dashboard: o servidor caiu: {cause}");
        }
    });

    info!("dashboard API online em http://{address}/");
    Ok((dashboard, Server { stop, task }))
}

fn router(dashboard: Arc<Dashboard>) -> Router {
    let gate = dashboard.gate.clone();
    Router::new()
        .fallback(dispatch)
        .with_state(dashboard)
        .layer(middleware::from_fn_with_state(gate, guard))
}

/// "/api/x/" e "/api/x" sao a mesma rota, como no HttpListener.
fn normalize(path: &str) -> &str {
    let trimmed = path.trim_end_matches('/');
    if trimmed.is_empty() { "/" } else { trimmed }
}

/// Tudo o que vem antes da rota, na ordem do C#: vaga em voo, CORS, trava do
/// tunnel, preflight, orcamento por cliente, health e orcamento por rota.
async fn guard(State(gate): State<Arc<Gate>>, req: Request, next: Next) -> Response {
    let origin = gate
        .cors_origin(gate::header(req.headers(), "origin"))
        .map(str::to_owned);

    // O painel e um punhado de requisicoes por minuto; 64 em voo e folga, e o
    // teto impede que uma enxurrada dispute o runtime com o gateway.
    let mut response =
        match tokio::time::timeout(IN_FLIGHT_WAIT, gate.in_flight.clone().acquire_owned()).await {
            Ok(Ok(_permit)) => screen(&gate, req, next).await,
            _ => with_retry_after(error_response(503, "Dashboard ocupado, tente de novo."), 5),
        };

    // Em toda resposta, senao o painel nem le o motivo e mostra erro de rede.
    let headers = response.headers_mut();
    if let Some(origin) = origin.and_then(|o| HeaderValue::from_str(&o).ok()) {
        headers.insert(header::ACCESS_CONTROL_ALLOW_ORIGIN, origin);
        headers.insert(header::VARY, HeaderValue::from_static("Origin"));
    }
    headers.insert(
        header::ACCESS_CONTROL_ALLOW_HEADERS,
        HeaderValue::from_static("Authorization, Content-Type"),
    );
    headers.insert(
        header::ACCESS_CONTROL_ALLOW_METHODS,
        HeaderValue::from_static("GET, POST, OPTIONS"),
    );
    response
}

async fn screen(gate: &Gate, req: Request, next: Next) -> Response {
    let peer = req
        .extensions()
        .get::<axum::extract::ConnectInfo<SocketAddr>>()
        .map(|c| c.0.ip());

    // Veio pela Cloudflare sem o segredo do Worker: chamaram o hostname do
    // tunnel direto. A regra do WAF ja barra; esta e a segunda trava.
    if gate.is_direct_hit(req.headers()) {
        return error_response(403, "Acesso direto à API recusado.");
    }

    if req.method() == Method::OPTIONS {
        return StatusCode::NO_CONTENT.into_response();
    }

    if let Err(wait) = gate.client_budget(req.headers(), peer) {
        return with_retry_after(
            error_response(429, "Muitas requisições. Espere um pouco e tente de novo."),
            retry_secs(wait),
        );
    }

    let path = normalize(req.uri().path()).to_owned();
    if req.method() == Method::GET && path == "/api/health" {
        return json_response(200, &json!({ "ok": true, "timestamp": Utc::now() }));
    }

    // Fica no dispatch, e nao em cada rota, para rota nova nao nascer sem limite.
    if req.method() == Method::POST
        && path != "/api/auth/login"
        && path != "/api/auth/logout"
        && let Err(wait) = gate.route_budget(req.headers(), peer, &path)
    {
        return with_retry_after(
            error_response(
                429,
                "Muitas requisições nesta rota. Espere um pouco e tente de novo.",
            ),
            retry_secs(wait),
        );
    }

    next.run(req).await
}

async fn dispatch(State(dash): State<Arc<Dashboard>>, req: Request) -> Response {
    let method = req.method().clone();
    let path = normalize(req.uri().path()).to_owned();
    let mut req = Req::new(req);
    let d = dash.as_ref();
    let r = &mut req;

    let result = match (method.as_str(), path.as_str()) {
        ("POST", "/api/auth/login") => routes::login(d, r).await,
        ("POST", "/api/auth/logout") => routes::logout(d, r),
        ("GET", "/api/auth/me") => routes::me(d, r),
        ("GET", "/api/audit") => routes::panel_audit(d, r).await,
        ("POST", "/api/messages/send") => routes::send_message(d, r).await,
        ("POST", "/api/roles/add") => routes::change_role(d, r, true).await,
        ("POST", "/api/roles/remove") => routes::change_role(d, r, false).await,
        ("POST", "/api/punishments/timeout") => routes::timeout(d, r).await,
        ("POST", "/api/punishments/remove-from-regiment") => {
            routes::remove_from_regiment(d, r).await
        }
        ("GET", "/api/status") => routes::status(d, r).await,
        ("GET", "/api/logs") => routes::logs(d, r).await,
        ("GET", "/api/audit/roster") => audit_routes::roster(d, r).await,
        ("GET", "/api/audit/history") => audit_routes::history(d, r).await,
        ("POST", "/api/audit/entry") => audit_routes::entry(d, r).await,
        ("POST", "/api/audit/ranks") => audit_routes::ranks(d, r).await,
        ("POST", "/api/audit/push") => audit_routes::push(d, r).await,
        ("GET", "/api/promotions") => audit_routes::promotions(d, r).await,
        ("POST", "/api/deployment") => routes::deployment(d, r).await,
        ("POST", "/api/dm") => routes::dm_start(d, r).await,
        ("GET", "/api/dm/status") => routes::dm_status(d, r).await,
        ("POST", "/api/dm/cancel") => routes::dm_cancel(d, r).await,
        ("POST", "/api/enlist") => routes::enlist(d, r).await,
        _ => Ok(error_response(404, "Rota não encontrada.")),
    };

    match result {
        Ok(response) => response,
        Err(Fail::TooLarge) => error_response(413, "Corpo da requisição grande demais."),
        Err(Fail::Timeout) => {
            error_response(408, "O corpo da requisição demorou demais para chegar.")
        }
        Err(Fail::BadJson) => error_response(400, "Corpo da requisição não é um JSON válido."),
        Err(Fail::Internal(cause)) => {
            // O detalhe fica no log: mensagem de erro na resposta e informacao
            // de graca para quem sonda a API.
            error!("dashboard: erro em {method} {path}: {cause}");
            error_response(500, "Erro interno no dashboard.")
        }
    }
}

impl Dashboard {
    pub async fn resolve_level(&self, ctx: &serenity::Context, user_id: u64) -> Resolution {
        self.auth.resolve_level(ctx, user_id).await
    }

    /// Codigo do /dashboardlink e quando ele vence.
    pub async fn link_code(&self, user: &serenity::User) -> (String, chrono::DateTime<Utc>) {
        let minutes = self
            .auth
            .read()
            .await
            .core
            .link_code_lifetime_minutes
            .clamp(1, 30);
        let expires_at = Utc::now() + chrono::Duration::minutes(minutes);
        let code = self.gate.issue_code(PendingCode {
            user_id: user.id.get(),
            username: user.name.clone(),
            avatar_url: user.face(),
            expires_at,
        });
        (code, expires_at)
    }

    /// Sessao valida E com nivel suficiente. O nivel e reconferido no Discord
    /// de tempos em tempos: sem isso, quem perdesse o cargo mandaria no painel
    /// ate a sessao vencer. Discord fora do ar nao derruba ninguem.
    /// Recebe os headers, e nao o Req: o corpo nao e Sync e nao pode
    /// atravessar o await.
    async fn require(&self, headers: &HeaderMap, min: i32) -> Option<Arc<Session>> {
        let session = self.gate.session(headers)?;
        if session.needs_check() {
            let _refresh = session.refresh.lock().await;
            // Outra requisicao pode ter reconferido enquanto esperavamos.
            if session.needs_check() {
                let current = self
                    .auth
                    .resolve_level(&self.bot.ctx, session.user_id)
                    .await;
                if current.lookup_failed {
                    session.checked(None, PERMISSION_RETRY);
                } else {
                    session.checked(Some(current.level), PERMISSION_REFRESH);
                    if current.level <= 0 {
                        self.gate.close_session(&session.token);
                        return None;
                    }
                }
            }
        }
        (session.level() >= min).then_some(session)
    }

    /// Sem sessao e 401 (o painel volta para o login); com sessao e nivel
    /// baixo, 403 com o motivo da rota.
    fn auth_failure(&self, req: &Req, forbidden: &str) -> request::Reply {
        if self.gate.session(&req.headers).is_none() {
            return request::err(
                401,
                "Sessão inválida ou expirada. Rode /dashboardlink no Discord e entre de novo.",
            );
        }
        request::err(403, forbidden)
    }

    /// Registro no log do painel. Falha vira log: a acao ja aconteceu.
    async fn record(&self, actor: &Session, action: &str, details: &str) {
        let entry = AuditEntry {
            timestamp_utc: Utc::now(),
            actor_user_id: actor.user_id,
            actor_username: actor.username.clone(),
            actor_avatar_url: actor.avatar_url.clone(),
            action: action.to_owned(),
            details: details.to_owned(),
        };
        if let Err(cause) = self.auth.append_audit(entry).await {
            warn!("dashboard: falha ao registrar {action} no log do painel: {cause}");
        }
    }

    async fn public_status(&self) -> Value {
        let cached = {
            let status = self.status.lock().unwrap_or_else(|e| e.into_inner());
            status
                .as_ref()
                .filter(|(at, _)| at.elapsed() < Duration::from_secs(10))
                .map(|(_, value)| value.clone())
        };
        if let Some(value) = cached {
            return value;
        }

        let value = self.fresh_status().await;
        *self.status.lock().unwrap_or_else(|e| e.into_inner()) =
            Some((Instant::now(), value.clone()));
        value
    }

    async fn fresh_status(&self) -> Value {
        let latency = crate::status::latency(&self.bot.shards, self.bot.ctx.shard_id).await;
        serde_json::to_value(crate::status::public(&self.bot.ctx.cache, latency))
            .unwrap_or_default()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use axum::body::Body;
    use tower::ServiceExt;

    #[test]
    fn endereco_do_listen_url() {
        assert_eq!(
            bind_address("http://127.0.0.1:5056/").unwrap(),
            "127.0.0.1:5056"
        );
        assert_eq!(bind_address("").unwrap(), "127.0.0.1:5056");
        assert_eq!(bind_address("http://+:8080/").unwrap(), "0.0.0.0:8080");
        assert_eq!(bind_address("http://[::1]:5056/").unwrap(), "[::1]:5056");
        assert_eq!(bind_address("http://localhost/").unwrap(), "localhost:80");
        assert!(bind_address("https://x/").is_err());
    }

    #[test]
    fn barra_final_nao_muda_a_rota() {
        assert_eq!(normalize("/api/health/"), "/api/health");
        assert_eq!(normalize("/"), "/");
        assert_eq!(normalize(""), "/");
    }

    /// O guard na frente de uma rota falsa: testa a ordem das travas sem
    /// precisar do Discord.
    fn app(secret: &str) -> Router {
        let origins = gate::origins(&auth::CoreConfig::default()).unwrap();
        let gate = Arc::new(Gate::new(origins, secret));
        Router::new()
            .fallback(|| async { error_response(404, "Rota não encontrada.") })
            .layer(middleware::from_fn_with_state(gate, guard))
    }

    async fn call(
        app: &Router,
        req: axum::http::Request<Body>,
    ) -> (StatusCode, axum::http::HeaderMap, Value) {
        let response = app.clone().oneshot(req).await.unwrap();
        let status = response.status();
        let headers = response.headers().clone();
        let bytes = axum::body::to_bytes(response.into_body(), usize::MAX)
            .await
            .unwrap();
        let body = serde_json::from_slice(&bytes).unwrap_or(Value::Null);
        (status, headers, body)
    }

    fn get(path: &str) -> axum::http::request::Builder {
        axum::http::Request::get(path)
    }

    #[tokio::test]
    async fn health_cors_e_rota_desconhecida() {
        let app = app("");
        let (status, headers, body) = call(
            &app,
            get("/api/health/")
                .header("origin", "https://dashboard.daeese.me")
                .body(Body::empty())
                .unwrap(),
        )
        .await;
        assert_eq!(status, 200);
        assert_eq!(body["ok"], true);
        assert!(body["timestamp"].is_string());
        assert_eq!(
            headers["access-control-allow-origin"],
            "https://dashboard.daeese.me"
        );
        assert_eq!(
            headers["access-control-allow-methods"],
            "GET, POST, OPTIONS"
        );

        let (status, headers, body) = call(
            &app,
            get("/api/nada")
                .header("origin", "https://evil.test")
                .body(Body::empty())
                .unwrap(),
        )
        .await;
        assert_eq!(status, 404);
        assert_eq!(body["error"], "Rota não encontrada.");
        assert!(!headers.contains_key("access-control-allow-origin"));
        assert_eq!(headers["content-type"], "application/json; charset=utf-8");

        let (status, headers, _) = call(
            &app,
            axum::http::Request::options("/api/dm")
                .header("origin", "https://ccore.daeese.me")
                .body(Body::empty())
                .unwrap(),
        )
        .await;
        assert_eq!(status, 204);
        assert_eq!(
            headers["access-control-allow-headers"],
            "Authorization, Content-Type"
        );
    }

    #[tokio::test]
    async fn acesso_direto_pelo_tunnel_e_recusado() {
        let app = app("segredo");
        let (status, headers, body) = call(
            &app,
            get("/api/health")
                .header("cf-ray", "abc")
                .header("origin", "https://ccore.daeese.me")
                .body(Body::empty())
                .unwrap(),
        )
        .await;
        assert_eq!(status, 403);
        assert_eq!(body["error"], "Acesso direto à API recusado.");
        assert!(headers.contains_key("access-control-allow-origin"));

        let (status, _, _) = call(
            &app,
            get("/api/health")
                .header("cf-ray", "abc")
                .header("x-ccore-tunnel", "segredo")
                .body(Body::empty())
                .unwrap(),
        )
        .await;
        assert_eq!(status, 200);
    }

    #[tokio::test]
    async fn rota_cara_estoura_o_orcamento_com_retry_after() {
        let app = app("");
        for _ in 0..gate::EXPENSIVE_BUDGET {
            let (status, _, _) = call(
                &app,
                axum::http::Request::post("/api/deployment/")
                    .body(Body::empty())
                    .unwrap(),
            )
            .await;
            assert_eq!(status, 404);
        }
        let (status, headers, body) = call(
            &app,
            axum::http::Request::post("/api/deployment")
                .body(Body::empty())
                .unwrap(),
        )
        .await;
        assert_eq!(status, 429);
        assert_eq!(
            body["error"],
            "Muitas requisições nesta rota. Espere um pouco e tente de novo."
        );
        assert!(headers.contains_key("retry-after"));

        // Login nao entra no orcamento de rota: tem o limite proprio.
        for _ in 0..5 {
            let (status, _, _) = call(
                &app,
                axum::http::Request::post("/api/auth/login")
                    .body(Body::empty())
                    .unwrap(),
            )
            .await;
            assert_eq!(status, 404);
        }
    }
}
