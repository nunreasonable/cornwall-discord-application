//! Leitura do corpo, campos do JSON e as respostas, com as mesmas tolerancias
//! do bot em C#: tipo inesperado vira campo ausente e responde 400, nao 500.

use std::{net::IpAddr, time::Duration};

use axum::{
    body::Body,
    extract::{ConnectInfo, Request},
    http::{HeaderMap, HeaderValue, StatusCode, header},
    response::{IntoResponse, Response},
};
use poise::serenity_prelude as serenity;
use serde_json::{Map, Value, json};

/// O maior corpo legitimo e o bloco de auditoria, de poucos KB. Sem teto, o
/// login (que roda antes de qualquer autenticacao) bufferizava GBs.
pub const MAX_BODY: usize = 64 * 1024;
/// 64 KB em 10s e ~6 KB/s. Sem prazo, conexoes de um byte por segundo
/// seguravam as vagas de requisicao em voo.
const BODY_DEADLINE: Duration = Duration::from_secs(10);

/// O que derruba a rota inteira. O detalhe de Internal vai so para o log.
#[derive(Debug)]
pub enum Fail {
    TooLarge,
    Timeout,
    BadJson,
    Internal(String),
}

impl From<String> for Fail {
    fn from(cause: String) -> Self {
        Self::Internal(cause)
    }
}

impl From<serenity::Error> for Fail {
    fn from(cause: serenity::Error) -> Self {
        Self::Internal(cause.to_string())
    }
}

pub type Reply = Result<Response, Fail>;

pub struct Req {
    pub headers: HeaderMap,
    pub peer: Option<IpAddr>,
    query: Vec<(String, String)>,
    body: Option<Body>,
}

impl Req {
    pub fn new(req: Request) -> Self {
        let peer = req
            .extensions()
            .get::<ConnectInfo<std::net::SocketAddr>>()
            .map(|c| c.0.ip());
        let query = req
            .uri()
            .query()
            .map(|q| {
                url::form_urlencoded::parse(q.as_bytes())
                    .into_owned()
                    .collect()
            })
            .unwrap_or_default();
        let (parts, body) = req.into_parts();
        Self {
            headers: parts.headers,
            peer,
            query,
            body: Some(body),
        }
    }

    pub fn query(&self, key: &str) -> Option<&str> {
        self.query
            .iter()
            .find(|(k, _)| k.eq_ignore_ascii_case(key))
            .map(|(_, v)| v.as_str())
    }

    /// O corpo como objeto JSON. Vazio e None; o resto que nao for objeto e
    /// BadJson. So e lido quando a rota pede, depois da autorizacao.
    pub async fn json(&mut self) -> Result<Fields, Fail> {
        let Some(body) = self.body.take() else {
            return Ok(Fields(None));
        };
        // Content-Length e so o primeiro filtro: chunked nao tem, e cliente
        // hostil mente. O to_bytes tambem para no teto.
        let declared = self
            .headers
            .get(header::CONTENT_LENGTH)
            .and_then(|v| v.to_str().ok())
            .and_then(|v| v.parse::<u64>().ok());
        if declared.is_some_and(|len| len > MAX_BODY as u64) {
            return Err(Fail::TooLarge);
        }

        // O to_bytes so falha por teto ou por conexao caida, e na conexao caida
        // ninguem le a resposta.
        let bytes = tokio::time::timeout(BODY_DEADLINE, axum::body::to_bytes(body, MAX_BODY))
            .await
            .map_err(|_| Fail::Timeout)?
            .map_err(|_| Fail::TooLarge)?;
        parse_object(&bytes)
    }
}

pub fn parse_object(bytes: &[u8]) -> Result<Fields, Fail> {
    if bytes.iter().all(u8::is_ascii_whitespace) {
        return Ok(Fields(None));
    }
    match serde_json::from_slice::<Value>(bytes) {
        Ok(Value::Object(map)) => Ok(Fields(Some(map))),
        _ => Err(Fail::BadJson),
    }
}

/// Os campos do corpo. None e "nao veio corpo": todo campo e ausente.
pub struct Fields(pub Option<Map<String, Value>>);

impl Fields {
    pub fn get(&self, field: &str) -> Option<&Value> {
        self.0.as_ref()?.get(field)
    }

    /// Texto do campo. Objeto e lista viram None, como no C#.
    pub fn string(&self, field: &str) -> Option<String> {
        as_string(self.get(field)?)
    }

    pub fn trimmed(&self, field: &str) -> Option<String> {
        self.string(field).map(|s| s.trim().to_owned())
    }

    /// Snowflake como numero ou texto. Zero conta como ausente: nenhum id do
    /// Discord e zero, e o serenity entra em panic com ele.
    pub fn snowflake(&self, field: &str) -> Option<u64> {
        match self.get(field)? {
            Value::Number(n) => n.as_u64(),
            Value::String(s) => s.trim().parse().ok(),
            _ => None,
        }
        .filter(|&id| id != 0)
    }

    /// Ausente ou ilegivel e false.
    pub fn boolean(&self, field: &str) -> bool {
        match self.get(field) {
            Some(Value::Bool(b)) => *b,
            Some(Value::Number(n)) => n
                .as_i64()
                .map(|v| v != 0)
                .or_else(|| n.as_u64().map(|v| v != 0))
                .unwrap_or(false),
            Some(Value::String(s)) => s.trim().eq_ignore_ascii_case("true"),
            _ => false,
        }
    }

    /// Contador nao negativo, separando AUSENTE (Ok(None)) de ZERO: campo
    /// omitido mantem o valor antigo em vez de zerar. Err e "veio invalido".
    pub fn count(&self, field: &str) -> Result<Option<i32>, ()> {
        let parsed = match self.get(field) {
            None | Some(Value::Null) => return Ok(None),
            Some(Value::Number(n)) => n.as_i64().and_then(|v| i32::try_from(v).ok()),
            Some(Value::String(s)) => s.trim().parse::<i32>().ok(),
            Some(_) => None,
        };
        match parsed {
            Some(v) if v >= 0 => Ok(Some(v)),
            _ => Err(()),
        }
    }
}

fn as_string(value: &Value) -> Option<String> {
    match value {
        Value::Null | Value::Object(_) | Value::Array(_) => None,
        Value::String(s) => Some(s.clone()),
        // O ToString do .NET escreve booleano com maiuscula.
        Value::Bool(true) => Some("True".to_owned()),
        Value::Bool(false) => Some("False".to_owned()),
        Value::Number(n) => Some(n.to_string()),
    }
}

/// Comprimento como o C# contava (unidades UTF-16): os tetos e as mensagens de
/// erro do painel sao nessa unidade.
pub fn utf16_len(text: &str) -> usize {
    text.encode_utf16().count()
}

pub fn json_response(status: u16, body: &Value) -> Response {
    let text = serde_json::to_string_pretty(body).unwrap_or_default();
    (
        StatusCode::from_u16(status).unwrap_or(StatusCode::INTERNAL_SERVER_ERROR),
        [(header::CONTENT_TYPE, "application/json; charset=utf-8")],
        text,
    )
        .into_response()
}

pub fn ok(status: u16, body: Value) -> Reply {
    Ok(json_response(status, &body))
}

pub fn error(status: u16, message: impl Into<String>) -> Response {
    json_response(status, &json!({ "error": message.into() }))
}

pub fn err(status: u16, message: impl Into<String>) -> Reply {
    Ok(error(status, message))
}

pub fn with_retry_after(mut response: Response, secs: u64) -> Response {
    if let Ok(value) = HeaderValue::from_str(&secs.to_string()) {
        response.headers_mut().insert(header::RETRY_AFTER, value);
    }
    response
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fields(json: &str) -> Fields {
        parse_object(json.as_bytes()).unwrap()
    }

    #[test]
    fn corpo_vazio_e_ausente_e_nao_objeto_e_json_invalido() {
        assert!(parse_object(b"  ").unwrap().0.is_none());
        assert!(matches!(parse_object(b"[1]"), Err(Fail::BadJson)));
        assert!(matches!(parse_object(b"{ruim"), Err(Fail::BadJson)));
    }

    #[test]
    fn campos_toleram_tipo_errado_como_o_csharp() {
        let f = fields(
            r#"{"a":"x","n":12,"o":{},"l":[],"b":true,"id":"  1487938282200236224 ",
                "zero":0,"neg":-1,"s":"7","bad":"x","f":1.5,"t":"TRUE","i":1}"#,
        );
        assert_eq!(f.string("a").as_deref(), Some("x"));
        assert_eq!(f.string("n").as_deref(), Some("12"));
        assert_eq!(f.string("b").as_deref(), Some("True"));
        assert!(f.string("o").is_none() && f.string("l").is_none() && f.string("nada").is_none());

        assert_eq!(f.snowflake("id"), Some(1487938282200236224));
        assert_eq!(f.snowflake("n"), Some(12));
        assert!(f.snowflake("zero").is_none() && f.snowflake("neg").is_none());

        assert!(f.boolean("b") && f.boolean("t") && f.boolean("i"));
        assert!(!f.boolean("a") && !f.boolean("nada"));

        assert_eq!(f.count("nada"), Ok(None));
        assert_eq!(f.count("s"), Ok(Some(7)));
        assert_eq!(f.count("zero"), Ok(Some(0)));
        assert!(f.count("neg").is_err() && f.count("bad").is_err() && f.count("f").is_err());
    }

    #[test]
    fn tamanho_em_utf16() {
        assert_eq!(utf16_len("aç😀"), 4);
    }
}
