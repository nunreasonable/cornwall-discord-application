//! Tudo o que decide se uma requisicao passa antes de chegar na rota: sessoes,
//! codigos de login, limites de taxa, CORS e a trava do tunnel.

use std::{
    collections::{HashMap, HashSet},
    net::IpAddr,
    sync::{Arc, Mutex, MutexGuard},
    time::{Duration, Instant},
};

use axum::http::HeaderMap;
use base64::{Engine, engine::general_purpose::URL_SAFE_NO_PAD};
use chrono::{DateTime, Utc};
use tokio::sync::Semaphore;

use super::auth::CoreConfig;

/// Sem 0/O e 1/I/L: o codigo e digitado a mao.
pub const CODE_ALPHABET: &[u8] = b"ABCDEFGHJKMNPQRSTUVWXYZ23456789";

pub const MAX_IN_FLIGHT: usize = 64;
const LOGIN_LIMIT: u32 = 10;
const LOGIN_WINDOW: Duration = Duration::from_secs(5 * 60);
pub const ROUTE_BUDGET: u32 = 30;
/// /api/dm manda ate 500 DMs, /api/audit/push escreve no GitHub e
/// /api/deployment pinga o regimento: no teto padrao seriam 30 por minuto.
pub const EXPENSIVE_BUDGET: u32 = 3;
pub const EXPENSIVE_ROUTES: [&str; 3] = ["/api/dm", "/api/audit/push", "/api/deployment"];
const ROUTE_WINDOW: Duration = Duration::from_secs(60);
/// O painel faz polling de 4s e 10s; a pagina de status, uma leitura a cada 15s.
const CLIENT_BUDGET: u32 = 240;
const CLIENT_WINDOW: Duration = Duration::from_secs(60);
/// Teto de chaves por limitador. Cheio, sai a mais antiga: recusar a nova
/// deixaria quem tem 4096 IPs (IPv6 basta) trancar o painel de todo mundo.
const MAX_KEYS: usize = 4096;
/// A limpeza varre sessoes e limitadores; nao precisa rodar a cada requisicao.
const CLEANUP_EVERY: Duration = Duration::from_secs(30);

const TUNNEL_HEADER: &str = "x-ccore-tunnel";

/// Um Mutex envenenado so quer dizer que alguem entrou em panic com ele: o
/// mapa continua valido e o painel nao pode morrer por isso.
fn lock<T>(m: &Mutex<T>) -> MutexGuard<'_, T> {
    m.lock().unwrap_or_else(|e| e.into_inner())
}

pub fn header<'a>(headers: &'a HeaderMap, name: &str) -> Option<&'a str> {
    headers
        .get(name)
        .and_then(|v| v.to_str().ok())
        .filter(|v| !v.is_empty())
}

/// Segundos inteiros para o Retry-After, arredondando para cima.
pub fn retry_secs(wait: Duration) -> u64 {
    wait.as_secs() + u64::from(wait.subsec_nanos() > 0)
}

/// Origens liberadas, ja validadas. O curinga e recusado: com ele qualquer
/// site leria as rotas publicas e, com um token vazado, pilotaria a API.
pub fn origins(core: &CoreConfig) -> Result<HashSet<String>, String> {
    let mut set: HashSet<String> = core
        .allowed_origins
        .iter()
        .map(|o| o.trim().to_lowercase())
        .filter(|o| !o.is_empty())
        .collect();
    if set.contains("*") {
        return Err("allowedOrigins contem \"*\" em dashboard_auth.json. Liste as origens reais (https://dashboard.daeese.me e https://ccore.daeese.me) em vez do curinga.".to_owned());
    }
    if set.is_empty() {
        set.insert("https://dashboard.daeese.me".to_owned());
        set.insert("https://ccore.daeese.me".to_owned());
    }
    Ok(set)
}

pub struct PendingCode {
    pub user_id: u64,
    pub username: String,
    pub avatar_url: String,
    pub expires_at: DateTime<Utc>,
}

struct Check {
    level: i32,
    next: Instant,
}

pub struct Session {
    pub token: String,
    pub user_id: u64,
    pub username: String,
    pub avatar_url: String,
    pub expires_at: DateTime<Utc>,
    check: Mutex<Check>,
    /// Uma reconferencia por vez: com varias abas abertas, duas requisicoes
    /// venciam juntas e consultavam o Discord em dobro.
    pub refresh: tokio::sync::Mutex<()>,
}

impl Session {
    pub fn level(&self) -> i32 {
        lock(&self.check).level
    }

    pub fn needs_check(&self) -> bool {
        Instant::now() >= lock(&self.check).next
    }

    pub fn checked(&self, level: Option<i32>, wait: Duration) {
        let mut check = lock(&self.check);
        if let Some(level) = level {
            check.level = level;
        }
        check.next = Instant::now() + wait;
    }
}

struct Bucket {
    start: Instant,
    count: u32,
}

/// Janela fixa por chave: deixa passar as primeiras `limit` da janela.
pub struct Buckets {
    window: Duration,
    map: Mutex<HashMap<String, Bucket>>,
}

impl Buckets {
    pub fn new(window: Duration) -> Self {
        Self {
            window,
            map: Mutex::default(),
        }
    }

    /// Err traz quanto falta para a janela reabrir.
    pub fn hit(&self, key: &str, limit: u32, now: Instant) -> Result<(), Duration> {
        let mut map = lock(&self.map);
        if map.len() >= MAX_KEYS
            && !map.contains_key(key)
            && let Some(oldest) = map
                .iter()
                .min_by_key(|(_, b)| b.start)
                .map(|(k, _)| k.clone())
        {
            map.remove(&oldest);
        }

        let bucket = map.entry(key.to_owned()).or_insert(Bucket {
            start: now,
            count: 0,
        });
        let elapsed = now.saturating_duration_since(bucket.start);
        if elapsed >= self.window {
            bucket.start = now;
            bucket.count = 0;
        }
        if bucket.count >= limit {
            return Err(self.window.saturating_sub(elapsed));
        }
        bucket.count += 1;
        Ok(())
    }

    pub fn forget(&self, key: &str) {
        lock(&self.map).remove(key);
    }

    pub fn prune(&self, now: Instant) {
        lock(&self.map).retain(|_, b| now.saturating_duration_since(b.start) < self.window);
    }

    #[cfg(test)]
    fn len(&self) -> usize {
        lock(&self.map).len()
    }
}

pub struct Gate {
    origins: HashSet<String>,
    tunnel_secret: String,
    pub in_flight: Arc<Semaphore>,
    sessions: Mutex<HashMap<String, Arc<Session>>>,
    codes: Mutex<HashMap<String, PendingCode>>,
    login: Buckets,
    routes: Buckets,
    clients: Buckets,
    last_cleanup: Mutex<Instant>,
}

impl Gate {
    pub fn new(origins: HashSet<String>, tunnel_secret: &str) -> Self {
        Self {
            origins,
            tunnel_secret: tunnel_secret.trim().to_owned(),
            in_flight: Arc::new(Semaphore::new(MAX_IN_FLIGHT)),
            sessions: Mutex::default(),
            codes: Mutex::default(),
            login: Buckets::new(LOGIN_WINDOW),
            routes: Buckets::new(ROUTE_WINDOW),
            clients: Buckets::new(CLIENT_WINDOW),
            last_cleanup: Mutex::new(Instant::now()),
        }
    }

    pub fn has_tunnel_secret(&self) -> bool {
        !self.tunnel_secret.is_empty()
    }

    /// A origem a ecoar no Access-Control-Allow-Origin, se for liberada.
    pub fn cors_origin<'a>(&self, origin: Option<&'a str>) -> Option<&'a str> {
        origin.filter(|o| self.origins.contains(&o.to_lowercase()))
    }

    /// A borda da Cloudflare sempre poe estes dois e recusa quem tenta manda-los
    /// de fora: a presenca deles separa o tunnel de uma chamada local.
    pub fn came_through_cloudflare(headers: &HeaderMap) -> bool {
        header(headers, "cf-ray").is_some() || header(headers, "cf-connecting-ip").is_some()
    }

    /// So o Worker conhece o segredo. Comparacao de tempo fixo, para nao vazar
    /// o tamanho do prefixo certo a quem mede.
    pub fn is_from_tunnel(&self, headers: &HeaderMap) -> bool {
        if self.tunnel_secret.is_empty() {
            return false;
        }
        let Some(supplied) = header(headers, TUNNEL_HEADER) else {
            return false;
        };
        let (a, b) = (supplied.as_bytes(), self.tunnel_secret.as_bytes());
        a.len() == b.len() && a.iter().zip(b).fold(0u8, |acc, (x, y)| acc | (x ^ y)) == 0
    }

    /// Veio pela Cloudflare sem o segredo do Worker: alguem chamou o hostname
    /// do tunnel direto, pulando o rate limit e as checagens do Worker.
    pub fn is_direct_hit(&self, headers: &HeaderMap) -> bool {
        self.has_tunnel_secret()
            && Self::came_through_cloudflare(headers)
            && !self.is_from_tunnel(headers)
    }

    /// Quem e o cliente, para os limitadores. Pelo tunnel o peer TCP e sempre
    /// 127.0.0.1, entao vale o CF-Connecting-IP - mas so com o segredo
    /// batendo, senao qualquer processo local trocaria de identidade a cada
    /// tentativa. X-Forwarded-For nunca: o primeiro item e do atacante.
    pub fn client_key(&self, headers: &HeaderMap, peer: Option<IpAddr>) -> String {
        if self.is_from_tunnel(headers)
            && let Some(ip) = header(headers, "cf-connecting-ip").map(str::trim)
            && !ip.is_empty()
        {
            return group_ipv6(ip);
        }
        peer.map_or_else(|| "desconhecido".to_owned(), |ip| ip.to_string())
    }

    /// Sem o segredo, todo mundo e 127.0.0.1 e o teto viraria global: alguns
    /// visitantes da pagina de status travariam o painel. Melhor sem teto.
    pub fn client_budget(&self, headers: &HeaderMap, peer: Option<IpAddr>) -> Result<(), Duration> {
        if !self.has_tunnel_secret() {
            return Ok(());
        }
        self.clients.hit(
            &self.client_key(headers, peer),
            CLIENT_BUDGET,
            Instant::now(),
        )
    }

    /// Limite das rotas que MUDAM algo. A chave e a sessao quando existe:
    /// sem login, cada IP tem o seu balde.
    pub fn route_budget(
        &self,
        headers: &HeaderMap,
        peer: Option<IpAddr>,
        path: &str,
    ) -> Result<(), Duration> {
        let limit = if EXPENSIVE_ROUTES.contains(&path) {
            EXPENSIVE_BUDGET
        } else {
            ROUTE_BUDGET
        };
        let who = self
            .session(headers)
            .map_or_else(|| self.client_key(headers, peer), |s| s.token.clone());
        self.routes
            .hit(&format!("{who}|{path}"), limit, Instant::now())
    }

    /// O codigo e curto porque alguem digita; o que segura a forca bruta e
    /// esta janela por cliente.
    pub fn login_attempt(&self, headers: &HeaderMap, peer: Option<IpAddr>) -> Result<(), Duration> {
        let now = Instant::now();
        self.login.prune(now);
        self.login
            .hit(&self.client_key(headers, peer), LOGIN_LIMIT, now)
    }

    pub fn login_succeeded(&self, headers: &HeaderMap, peer: Option<IpAddr>) {
        self.login.forget(&self.client_key(headers, peer));
    }

    /// Um codigo por pessoa: pedir outro invalida o anterior.
    pub fn issue_code(&self, pending: PendingCode) -> String {
        let mut codes = lock(&self.codes);
        let now = Utc::now();
        codes.retain(|_, c| c.expires_at > now && c.user_id != pending.user_id);
        // Duas pessoas podem sortear o mesmo codigo: sobrescrever faria o da
        // primeira logar como a segunda.
        let mut code = link_code();
        while codes.contains_key(&code) {
            code = link_code();
        }
        codes.insert(code.clone(), pending);
        code
    }

    /// Consome o codigo: vale uma vez so.
    pub fn take_code(&self, code: &str) -> Option<PendingCode> {
        let mut codes = lock(&self.codes);
        let now = Utc::now();
        codes.retain(|_, c| c.expires_at > now);
        codes.remove(&code.trim().to_uppercase())
    }

    pub fn open_session(
        &self,
        pending: PendingCode,
        level: i32,
        lifetime: chrono::Duration,
        recheck: Duration,
    ) -> Arc<Session> {
        let session = Arc::new(Session {
            token: session_token(),
            user_id: pending.user_id,
            username: pending.username,
            avatar_url: pending.avatar_url,
            expires_at: Utc::now() + lifetime,
            check: Mutex::new(Check {
                level,
                next: Instant::now() + recheck,
            }),
            refresh: tokio::sync::Mutex::default(),
        });
        lock(&self.sessions).insert(session.token.clone(), session.clone());
        session
    }

    pub fn close_session(&self, token: &str) {
        lock(&self.sessions).remove(token);
    }

    /// A sessao do Bearer, se existe e nao venceu. Nivel NAO e conferido aqui.
    pub fn session(&self, headers: &HeaderMap) -> Option<Arc<Session>> {
        self.cleanup();
        let auth = header(headers, "authorization")?;
        let token = auth
            .get(..7)
            .filter(|p| p.eq_ignore_ascii_case("Bearer "))
            .map(|_| auth[7..].trim())?;

        let mut sessions = lock(&self.sessions);
        let session = sessions.get(token)?.clone();
        if session.expires_at <= Utc::now() {
            sessions.remove(token);
            return None;
        }
        Some(session)
    }

    fn cleanup(&self) {
        let now = Instant::now();
        {
            let mut last = lock(&self.last_cleanup);
            if now.saturating_duration_since(*last) < CLEANUP_EVERY {
                return;
            }
            *last = now;
        }
        let utc = Utc::now();
        lock(&self.sessions).retain(|_, s| s.expires_at > utc);
        self.login.prune(now);
        self.routes.prune(now);
        self.clients.prune(now);
    }
}

fn random_bytes<const N: usize>() -> [u8; N] {
    let mut buf = [0u8; N];
    getrandom::fill(&mut buf).expect("sem fonte de aleatoriedade do sistema");
    buf
}

/// CORN-XXXXX-XXXXX do sistema operacional, nunca de gerador previsivel: 10
/// caracteres de 31 sao ~49 bits, inviavel de varrer nos minutos de vida do
/// codigo. Bytes acima de 247 sao descartados para nao viciar o sorteio.
pub fn link_code() -> String {
    let limit = (256 / CODE_ALPHABET.len() * CODE_ALPHABET.len()) as u8;
    let mut chars = String::with_capacity(10);
    while chars.len() < 10 {
        for byte in random_bytes::<16>() {
            if byte < limit && chars.len() < 10 {
                chars.push(CODE_ALPHABET[byte as usize % CODE_ALPHABET.len()] as char);
            }
        }
    }
    format!("CORN-{}-{}", &chars[..5], &chars[5..])
}

/// 32 bytes em base64 de URL: o token autoriza cargo, timeout e DM em massa.
pub fn session_token() -> String {
    URL_SAFE_NO_PAD.encode(random_bytes::<32>())
}

/// Id de job no formato do Guid "N" do C#: 32 hex.
pub fn job_id() -> String {
    random_bytes::<16>()
        .iter()
        .map(|b| format!("{b:02x}"))
        .collect()
}

/// IPv6 vira o /64: quem tem um /64 troca de endereco a cada requisicao.
pub fn group_ipv6(ip: &str) -> String {
    match ip.parse::<IpAddr>() {
        Ok(IpAddr::V6(v6)) if v6.to_ipv4_mapped().is_none() => {
            let mut octets = v6.octets();
            octets[8..].fill(0);
            format!("{}/64", std::net::Ipv6Addr::from(octets))
        }
        _ => ip.to_owned(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use axum::http::HeaderValue;

    fn headers(pairs: &[(&'static str, &str)]) -> HeaderMap {
        let mut map = HeaderMap::new();
        for (k, v) in pairs {
            map.insert(*k, HeaderValue::from_str(v).unwrap());
        }
        map
    }

    #[test]
    fn codigo_tem_o_formato_e_o_alfabeto_certos() {
        for _ in 0..200 {
            let code = link_code();
            assert_eq!(code.len(), 16);
            assert!(code.starts_with("CORN-") && &code[10..11] == "-");
            assert!(
                code[5..10]
                    .bytes()
                    .chain(code[11..].bytes())
                    .all(|b| CODE_ALPHABET.contains(&b))
            );
        }
        assert_eq!(CODE_ALPHABET.len(), 31);
        assert!(!CODE_ALPHABET.iter().any(|b| b"01OIL".contains(b)));
    }

    #[test]
    fn token_e_job_id_sao_aleatorios_e_no_formato() {
        let (a, b) = (session_token(), session_token());
        assert_ne!(a, b);
        assert_eq!(a.len(), 43);
        assert!(!a.contains(['+', '/', '=']));
        let id = job_id();
        assert_eq!(id.len(), 32);
        assert!(id.bytes().all(|b| b.is_ascii_hexdigit()));
    }

    #[test]
    fn curinga_e_recusado_e_vazio_vira_as_origens_reais() {
        let mut core = CoreConfig {
            allowed_origins: vec!["*".to_owned()],
            ..CoreConfig::default()
        };
        assert!(origins(&core).is_err());
        core.allowed_origins = vec![" ".to_owned()];
        let set = origins(&core).unwrap();
        assert!(
            set.contains("https://dashboard.daeese.me") && set.contains("https://ccore.daeese.me")
        );

        let gate = Gate::new(set, "");
        assert_eq!(
            gate.cors_origin(Some("https://CCORE.daeese.me")),
            Some("https://CCORE.daeese.me")
        );
        assert_eq!(gate.cors_origin(Some("https://evil.test")), None);
        assert_eq!(gate.cors_origin(None), None);
    }

    #[test]
    fn tunnel_exige_o_segredo_e_so_ai_confia_no_ip_da_cloudflare() {
        let gate = Gate::new(HashSet::new(), "segredo");
        let peer = Some(IpAddr::from([127, 0, 0, 1]));

        let direct = headers(&[("cf-ray", "x"), ("cf-connecting-ip", "1.2.3.4")]);
        assert!(gate.is_direct_hit(&direct));
        assert_eq!(gate.client_key(&direct, peer), "127.0.0.1");

        let worker = headers(&[
            ("cf-connecting-ip", "1.2.3.4"),
            ("x-ccore-tunnel", "segredo"),
        ]);
        assert!(!gate.is_direct_hit(&worker));
        assert_eq!(gate.client_key(&worker, peer), "1.2.3.4");

        let wrong = headers(&[("cf-ray", "x"), ("x-ccore-tunnel", "segredO")]);
        assert!(gate.is_direct_hit(&wrong));

        // Local, sem headers da Cloudflare, sempre passa.
        assert!(!gate.is_direct_hit(&HeaderMap::new()));

        // Sem segredo configurado a trava fica desligada.
        let open = Gate::new(HashSet::new(), " ");
        assert!(!open.is_direct_hit(&direct));
        assert_eq!(open.client_key(&worker, None), "desconhecido");
    }

    #[test]
    fn ipv6_agrupa_no_64() {
        assert_eq!(
            group_ipv6("2001:db8:1:2:aaaa:bbbb:cccc:dddd"),
            "2001:db8:1:2::/64"
        );
        assert_eq!(group_ipv6("1.2.3.4"), "1.2.3.4");
        assert_eq!(group_ipv6("::ffff:1.2.3.4"), "::ffff:1.2.3.4");
        assert_eq!(group_ipv6("lixo"), "lixo");
    }

    #[test]
    fn balde_deixa_passar_o_limite_e_reabre_com_a_janela() {
        let buckets = Buckets::new(Duration::from_secs(60));
        let t0 = Instant::now();
        for _ in 0..3 {
            assert!(buckets.hit("a", 3, t0).is_ok());
        }
        let wait = buckets
            .hit("a", 3, t0 + Duration::from_secs(20))
            .unwrap_err();
        assert_eq!(wait, Duration::from_secs(40));
        assert!(buckets.hit("b", 3, t0).is_ok());
        assert!(buckets.hit("a", 3, t0 + Duration::from_secs(60)).is_ok());
        assert_eq!(retry_secs(Duration::from_millis(40_001)), 41);

        buckets.prune(t0 + Duration::from_secs(200));
        assert_eq!(buckets.len(), 0);
    }

    #[test]
    fn balde_cheio_despeja_o_mais_antigo_em_vez_de_recusar() {
        let buckets = Buckets::new(Duration::from_secs(60));
        let t0 = Instant::now();
        for i in 0..MAX_KEYS {
            buckets
                .hit(&i.to_string(), 1, t0 + Duration::from_millis(i as u64))
                .unwrap();
        }
        assert!(buckets.hit("novo", 1, t0).is_ok());
        assert_eq!(buckets.len(), MAX_KEYS);
        // A chave 0 era a mais antiga e saiu: o balde dela recomecou.
        assert!(buckets.hit("0", 1, t0).is_ok());
    }

    #[test]
    fn rota_cara_tem_teto_menor_e_chave_por_sessao() {
        let gate = Gate::new(HashSet::new(), "");
        let none = HeaderMap::new();
        for _ in 0..EXPENSIVE_BUDGET {
            assert!(gate.route_budget(&none, None, "/api/dm").is_ok());
        }
        assert!(gate.route_budget(&none, None, "/api/dm").is_err());
        assert!(gate.route_budget(&none, None, "/api/roles/add").is_ok());

        let session = gate.open_session(
            PendingCode {
                user_id: 1,
                username: "a".to_owned(),
                avatar_url: String::new(),
                expires_at: Utc::now(),
            },
            2,
            chrono::Duration::minutes(5),
            Duration::from_secs(300),
        );
        let auth = headers(&[("authorization", &format!("bearer {}", session.token))]);
        assert_eq!(gate.session(&auth).unwrap().user_id, 1);
        assert!(gate.route_budget(&auth, None, "/api/dm").is_ok());
    }

    #[test]
    fn um_codigo_por_pessoa_e_uso_unico() {
        let gate = Gate::new(HashSet::new(), "");
        let pending = |user_id| PendingCode {
            user_id,
            username: String::new(),
            avatar_url: String::new(),
            expires_at: Utc::now() + chrono::Duration::minutes(5),
        };
        let first = gate.issue_code(pending(1));
        let second = gate.issue_code(pending(1));
        let other = gate.issue_code(pending(2));
        assert!(gate.take_code(&first).is_none());
        assert_eq!(gate.take_code(&second.to_lowercase()).unwrap().user_id, 1);
        assert!(gate.take_code(&second).is_none());
        assert_eq!(gate.take_code(&format!(" {other} ")).unwrap().user_id, 2);
    }

    #[test]
    fn login_trava_depois_de_dez_e_limpa_no_sucesso() {
        let gate = Gate::new(HashSet::new(), "");
        let h = HeaderMap::new();
        for _ in 0..LOGIN_LIMIT {
            assert!(gate.login_attempt(&h, None).is_ok());
        }
        assert!(gate.login_attempt(&h, None).is_err());
        gate.login_succeeded(&h, None);
        assert!(gate.login_attempt(&h, None).is_ok());
    }
}
