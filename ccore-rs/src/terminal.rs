//! Interface de texto do bot: ">channel ID" escolhe o canal e texto solto vira
//! mensagem nele. Sob o systemd o stdin e /dev/null, entao a sessao vem por um
//! socket Unix: `ccore --terminal` conecta no bot que ja esta rodando. O
//! caminho e o mesmo do bot em C#.

use std::{io::IsTerminal, os::unix::fs::PermissionsExt, path::PathBuf, sync::Arc};

use poise::serenity_prelude as serenity;
use tokio::{
    io::{AsyncBufReadExt, AsyncRead, AsyncWrite, AsyncWriteExt, BufReader},
    net::{UnixListener, UnixStream},
};
use tracing::{info, warn};

const HELP: &str = "Comandos:\n\
                    >channel ID   - definir canal\n\
                    Texto normal  - enviar mensagem ao canal definido\n\
                    >help         - esta ajuda\n\
                    >exit         - encerrar esta sessao";

/// XDG_RUNTIME_DIR ja e 0700 e some no logout, a vida util certa para isto.
pub fn socket_path() -> PathBuf {
    if let Some(custom) = std::env::var_os("CCORE_TERMINAL_SOCKET").filter(|v| !v.is_empty()) {
        return PathBuf::from(custom);
    }
    let base = match std::env::var_os("XDG_RUNTIME_DIR").filter(|v| !v.is_empty()) {
        Some(dir) => PathBuf::from(dir).join("ccore-bot"),
        None => std::env::temp_dir().join(format!(
            "ccore-bot-{}",
            std::env::var("USER").unwrap_or_default()
        )),
    };
    base.join("terminal.sock")
}

/// Sobe o socket e, se houver terminal de verdade, a sessao no stdin.
pub fn start(http: Arc<serenity::Http>) {
    match bind() {
        Ok(listener) => {
            info!("terminal de controle em {}", socket_path().display());
            let http = http.clone();
            tokio::spawn(accept_loop(listener, http));
        }
        Err(cause) => warn!("terminal de controle indisponivel: {cause}"),
    }

    if std::io::stdin().is_terminal() {
        tokio::spawn(async move {
            let _ = session(tokio::io::stdin(), tokio::io::stdout(), &http).await;
        });
    }
}

fn bind() -> std::io::Result<UnixListener> {
    let path = socket_path();
    if let Some(dir) = path.parent() {
        std::fs::create_dir_all(dir)?;
        std::fs::set_permissions(dir, std::fs::Permissions::from_mode(0o700))?;
    }

    // Arquivo velho de um processo morto impede o bind; um que ainda aceita
    // conexao e outro bot vivo, e esse nao se apaga.
    if path.exists() {
        if std::os::unix::net::UnixStream::connect(&path).is_ok() {
            return Err(std::io::Error::other(format!(
                "ja existe um bot escutando em {}",
                path.display()
            )));
        }
        std::fs::remove_file(&path)?;
    }

    let listener = UnixListener::bind(&path)?;
    std::fs::set_permissions(&path, std::fs::Permissions::from_mode(0o600))?;
    Ok(listener)
}

/// No desligamento: o arquivo do socket nao some sozinho.
pub fn cleanup() {
    let _ = std::fs::remove_file(socket_path());
}

async fn accept_loop(listener: UnixListener, http: Arc<serenity::Http>) {
    loop {
        match listener.accept().await {
            Ok((stream, _)) => {
                let http = http.clone();
                tokio::spawn(async move {
                    info!("sessao conectada pelo terminal de controle");
                    let (read, write) = stream.into_split();
                    if let Err(cause) = session(read, write, &http).await {
                        warn!("sessao do terminal terminou com erro: {cause}");
                    }
                    info!("sessao do terminal encerrada");
                });
            }
            Err(cause) => {
                // Uma falha isolada nao pode aposentar o socket; a pausa evita
                // girar sem parar se ele quebrou de vez.
                warn!("accept do terminal falhou: {cause}");
                tokio::time::sleep(std::time::Duration::from_secs(1)).await;
            }
        }
    }
}

/// Cada sessao tem o proprio canal: duas pessoas conectadas nao se atropelam.
async fn session(
    input: impl AsyncRead + Unpin,
    mut output: impl AsyncWrite + Unpin,
    http: &serenity::Http,
) -> std::io::Result<()> {
    let mut lines = BufReader::new(input).lines();
    let mut channel: Option<serenity::ChannelId> = None;

    output
        .write_all(b"Interface terminal ativada. Digite >help para comandos.\n")
        .await?;

    while let Some(line) = lines.next_line().await? {
        let line = line.trim();
        if line.is_empty() {
            continue;
        }

        let reply = match line.strip_prefix('>').map(str::trim) {
            Some("exit") => {
                output
                    .write_all(b"Encerrando interface terminal...\n")
                    .await?;
                break;
            }
            Some("help") => HELP.to_owned(),
            Some(cmd) => match cmd
                .strip_prefix("channel ")
                .map(|id| id.trim().parse::<u64>())
            {
                Some(Ok(id)) if id != 0 => {
                    match serenity::ChannelId::new(id).to_channel(http).await {
                        Ok(found) => {
                            channel = Some(found.id());
                            let name = found.guild().map_or_else(|| id.to_string(), |c| c.name);
                            format!("Canal definido: {name}")
                        }
                        Err(_) => "ID de canal invalido.".to_owned(),
                    }
                }
                Some(_) => "ID de canal invalido.".to_owned(),
                None => format!("Comando desconhecido: >{cmd}. Use >help."),
            },
            None => match channel {
                None => "Defina um canal primeiro com >channel ID".to_owned(),
                Some(id) => match id.say(http, line).await {
                    Ok(_) => continue,
                    Err(cause) => format!("[terminal] erro: {cause}"),
                },
            },
        };

        output.write_all(format!("{reply}\n").as_bytes()).await?;
        output.flush().await?;
    }

    Ok(())
}

/// `ccore --terminal`: liga este terminal no socket do bot que esta rodando.
pub async fn attach() -> Result<(), crate::Error> {
    let path = socket_path();
    let stream = UnixStream::connect(&path).await.map_err(|e| {
        format!(
            "nao foi possivel conectar em {}: {e}. O bot esta rodando?",
            path.display()
        )
    })?;
    let (mut from_bot, mut to_bot) = stream.into_split();

    // No fim do stdin fecha so a escrita: o bot ve EOF, responde o que faltava
    // e encerra a sessao.
    tokio::spawn(async move {
        let _ = tokio::io::copy(&mut tokio::io::stdin(), &mut to_bot).await;
        let _ = to_bot.shutdown().await;
    });

    tokio::io::copy(&mut from_bot, &mut tokio::io::stdout()).await?;
    Ok(())
}
