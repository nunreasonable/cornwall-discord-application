use std::{fmt, time::Duration};

use base64::{Engine, engine::general_purpose::STANDARD as BASE64};
use reqwest::{Client, RequestBuilder, Response, Url, header::ACCEPT};
use serde::Deserialize;
use serde_json::json;
use tokio::sync::Mutex;
use tracing::{info, warn};

use crate::config::AuditConfig;

const API: &str = "https://api.github.com";

/// Falha de configuracao, local ou do GitHub. O Display e a frase para o staff.
#[derive(Debug)]
pub enum PushError {
    Config(String),
    Local(String),
    Unauthorized,
    NotFound,
    RateLimited,
    Rejected(String),
    Api(u16, String),
    Network(String),
}

impl From<String> for PushError {
    fn from(text: String) -> Self {
        Self::Local(text)
    }
}

impl fmt::Display for PushError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Config(text) | Self::Local(text) | Self::Network(text) => f.write_str(text),
            Self::Unauthorized => f.write_str(
                "Credencial do GitHub inválida ou expirada. Rode `gh auth login` na máquina do bot \
                 (ou atualize `GH_TOKEN` / `audit.githubToken`, se estiver usando um deles) e tente de novo.",
            ),
            Self::NotFound => f.write_str(
                "Repositório, dono ou branch não encontrado. Verifique `audit.githubOwner`, \
                 `audit.githubRepo` e as permissões do token.",
            ),
            Self::RateLimited => {
                f.write_str("Limite de requisições do GitHub atingido. Tente novamente mais tarde.")
            }
            Self::Rejected(text) => write!(f, "O GitHub recusou a requisição: {text}"),
            Self::Api(status, text) => write!(f, "Erro da API do GitHub ({status}): {text}"),
        }
    }
}

/// Descobre o token sem exigir um PAT novo: o normal e o login que ja existe
/// no `gh` da maquina. O valor nunca vai para o log, so a origem.
pub async fn resolve_token(
    cache: &Mutex<Option<String>>,
    config: &AuditConfig,
) -> Result<String, PushError> {
    let mut cached = cache.lock().await;
    if let Some(token) = cached.as_ref() {
        return Ok(token.clone());
    }

    let from_config = config
        .github_token
        .as_deref()
        .map(str::trim)
        .filter(|t| !t.is_empty())
        .map(|t| ("config.jsonc", t.to_owned()));
    let from_env = || {
        ["GH_TOKEN", "GITHUB_TOKEN"].into_iter().find_map(|name| {
            let value = std::env::var(name).ok()?.trim().to_owned();
            (!value.is_empty()).then_some((name, value))
        })
    };

    let (source, token) = match from_config.or_else(from_env) {
        Some(found) => found,
        None => match from_gh_cli().await {
            Some(token) => ("gh CLI", token),
            None => {
                return Err(PushError::Config(
                    "Nenhuma credencial do GitHub disponível. Resolva de uma destas formas:\n\
                     • rode `gh auth login` na máquina do bot (recomendado, não precisa de token novo);\n\
                     • defina a variável de ambiente `GH_TOKEN`;\n\
                     • preencha `audit.githubToken` no config.jsonc."
                        .to_owned(),
                ));
            }
        },
    };

    info!("token do GitHub obtido de {source}");
    *cached = Some(token.clone());
    Ok(token)
}

async fn from_gh_cli() -> Option<String> {
    let run = tokio::process::Command::new("gh")
        .args(["auth", "token"])
        .kill_on_drop(true)
        .output();

    match tokio::time::timeout(Duration::from_secs(10), run).await {
        Err(_) => {
            warn!("`gh auth token` demorou demais e foi encerrado");
            None
        }
        Ok(Err(cause)) => {
            warn!("nao foi possivel executar o gh CLI: {cause}");
            None
        }
        // So o stderr: o stdout e o proprio token.
        Ok(Ok(out)) if !out.status.success() => {
            let stderr = String::from_utf8_lossy(&out.stderr);
            warn!("`gh auth token` falhou ({}): {}", out.status, stderr.trim());
            None
        }
        Ok(Ok(out)) => {
            let token = String::from_utf8_lossy(&out.stdout).trim().to_owned();
            (!token.is_empty()).then_some(token)
        }
    }
}

#[derive(Deserialize)]
struct Sha {
    sha: String,
}

#[derive(Deserialize)]
struct Commit {
    commit: Sha,
}

#[derive(Deserialize)]
struct Reference {
    object: Sha,
}

#[derive(Deserialize)]
struct Repository {
    default_branch: String,
}

/// Os arquivos da auditoria numa branch dedicada, pela API REST. Nada de git
/// local: so o token.
pub struct GitHub {
    http: Client,
    token: String,
    owner: String,
    repo: String,
    branch: String,
}

impl GitHub {
    pub fn new(http: Client, token: String, owner: &str, repo: &str, branch: &str) -> Self {
        Self {
            http,
            token,
            owner: owner.to_owned(),
            repo: repo.to_owned(),
            branch: branch.to_owned(),
        }
    }

    fn repo_url(&self) -> String {
        format!("{API}/repos/{}/{}", self.owner, self.repo)
    }

    fn contents_url(&self, path: &str) -> Result<Url, PushError> {
        let mut url = Url::parse(API).map_err(|e| PushError::Local(e.to_string()))?;
        url.path_segments_mut()
            .map_err(|()| PushError::Local("URL da API inválida".to_owned()))?
            .extend(["repos", &self.owner, &self.repo, "contents"])
            .extend(path.split('/'));
        Ok(url)
    }

    async fn send(&self, request: RequestBuilder, accept: &str) -> Result<Response, PushError> {
        let response = request
            .bearer_auth(&self.token)
            .header(ACCEPT, accept)
            .header("X-GitHub-Api-Version", "2022-11-28")
            .send()
            .await
            .map_err(|e| PushError::Network(e.without_url().to_string()))?;

        let status = response.status();
        if status.is_success() {
            return Ok(response);
        }

        let limited = response
            .headers()
            .get("x-ratelimit-remaining")
            .is_some_and(|v| v == "0");
        let body = response.text().await.unwrap_or_default();
        let message = serde_json::from_str::<serde_json::Value>(&body)
            .ok()
            .and_then(|v| v["message"].as_str().map(str::to_owned))
            .unwrap_or(body);

        Err(match status.as_u16() {
            401 => PushError::Unauthorized,
            404 => PushError::NotFound,
            429 => PushError::RateLimited,
            403 if limited => PushError::RateLimited,
            422 => PushError::Rejected(message),
            code => PushError::Api(code, message),
        })
    }

    async fn json<T: for<'de> Deserialize<'de>>(
        &self,
        request: RequestBuilder,
    ) -> Result<T, PushError> {
        self.send(request, "application/vnd.github+json")
            .await?
            .json()
            .await
            .map_err(|e| PushError::Network(e.without_url().to_string()))
    }

    /// Cria a branch de dados a partir da branch padrao, se ainda nao existe.
    pub async fn ensure_branch(&self) -> Result<(), PushError> {
        let url = format!("{}/git/ref/heads/{}", self.repo_url(), self.branch);
        match self.json::<Reference>(self.http.get(url)).await {
            Ok(_) => return Ok(()),
            Err(PushError::NotFound) => {}
            Err(e) => return Err(e),
        }

        let repo: Repository = self.json(self.http.get(self.repo_url())).await?;
        let url = format!("{}/git/ref/heads/{}", self.repo_url(), repo.default_branch);
        let base: Reference = self.json(self.http.get(url)).await?;

        let body = json!({ "ref": format!("refs/heads/{}", self.branch), "sha": base.object.sha });
        let url = format!("{}/git/refs", self.repo_url());
        self.json::<Reference>(self.http.post(url).json(&body))
            .await?;
        Ok(())
    }

    async fn file_sha(&self, path: &str) -> Result<Option<String>, PushError> {
        let request = self
            .http
            .get(self.contents_url(path)?)
            .query(&[("ref", &self.branch)]);
        match self.json::<Sha>(request).await {
            Ok(found) => Ok(Some(found.sha)),
            Err(PushError::NotFound) => Ok(None),
            Err(e) => Err(e),
        }
    }

    /// O conteudo publicado, ou None se o arquivo ainda nao existe.
    pub async fn file_content(&self, path: &str) -> Result<Option<String>, PushError> {
        let request = self
            .http
            .get(self.contents_url(path)?)
            .query(&[("ref", &self.branch)]);
        match self.send(request, "application/vnd.github.raw+json").await {
            Ok(response) => response
                .text()
                .await
                .map(Some)
                .map_err(|e| PushError::Network(e.without_url().to_string())),
            Err(PushError::NotFound) => Ok(None),
            Err(e) => Err(e),
        }
    }

    /// Cria ou atualiza o arquivo e devolve o sha do commit. O sha do arquivo
    /// e relido a cada tentativa: um sha velho da 409/422, e a nova resolve.
    pub async fn put_file(
        &self,
        path: &str,
        content: &str,
        message: &str,
    ) -> Result<String, PushError> {
        let mut attempt = 0;
        loop {
            let mut body = json!({
                "message": message,
                "content": BASE64.encode(content),
                "branch": self.branch,
            });
            if let Some(sha) = self.file_sha(path).await? {
                body["sha"] = sha.into();
            }

            match self
                .json::<Commit>(self.http.put(self.contents_url(path)?).json(&body))
                .await
            {
                Ok(done) => return Ok(done.commit.sha),
                Err(PushError::Rejected(_) | PushError::Api(409, _)) if attempt < 2 => {
                    attempt += 1;
                    tokio::time::sleep(Duration::from_millis(500 * attempt)).await;
                }
                Err(e) => return Err(e),
            }
        }
    }

    /// Arquivo do lote do dia. Se ja existe um hoje, ganha sufixo em vez de
    /// sobrescrever.
    pub async fn archive_path(&self, directory: &str, date: &str) -> Result<String, PushError> {
        let first = format!("{directory}/{date}.json");
        if self.file_sha(&first).await?.is_none() {
            return Ok(first);
        }

        for i in 2..=50 {
            let candidate = format!("{directory}/{date}-{i}.json");
            if self.file_sha(&candidate).await?.is_none() {
                return Ok(candidate);
            }
        }

        Err(PushError::Local(
            "Limite de arquivos de arquivamento para hoje atingido.".to_owned(),
        ))
    }

    pub fn commit_url(&self, sha: &str) -> String {
        format!(
            "https://github.com/{}/{}/commit/{sha}",
            self.owner, self.repo
        )
    }
}
