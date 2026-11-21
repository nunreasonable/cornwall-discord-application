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
                    >ia on        - ligar a IA local\n\
                    >ia off       - desligar a IA local\n\
                    >ia status    - estado da IA local\n\
                    >help         - esta ajuda\n\
                    >exit         - encerrar esta sessao";

type Llm = Option<Arc<crate::llm::Llm>>;

/// ">ia on|off|status". O socket e 0600: quem chega aqui e o dono da maquina.
async fn ia_command(llm: &Llm, arg: &str) -> String {
    let Some(llm) = llm else {
        return "A IA local nao esta configurada (falta a secao llm no config).".to_owned();
    };
    match arg {
        "on" => match llm.turn_on("terminal").await {
            Ok(()) => {
                "IA ligada. O modelo leva uns 20 s para carregar; veja com >ia status.".to_owned()
            }
            Err(cause) => format!("Nao consegui ligar: {cause}"),
        },
        "off" => match llm.turn_off("terminal").await {
            Ok(()) => "IA desligada. Placa e RAM liberadas.".to_owned(),
            Err(cause) => format!("A IA parou de responder, mas o servico nao parou: {cause}"),
        },
        "status" => {
            let health = llm.health().await;
            let yes = |b: bool| if b { "sim" } else { "nao" };
            let mut out = format!(
                "Respondendo: {}\nServico ativo: {}\nServidor pronto: {}\nModelo: {}",
                yes(health.enabled),
                yes(health.service_active),
                yes(health.server_ready),
                health.model.as_deref().unwrap_or("-")
            );
            for (label, value) in llm
                .memory_lines()
                .await
                .into_iter()
                .chain(llm.usage_lines())
            {
                out.push_str(&format!("\n{label}: {value}"));
            }
            out
        }
        _ => "Uso: >ia on | >ia off | >ia status".to_owned(),
    }
}

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
pub fn start(http: Arc<serenity::Http>, llm: Llm) {
    match bind() {
        Ok(listener) => {
            info!("terminal de controle em {}", socket_path().display());
            tokio::spawn(accept_loop(listener, http.clone(), llm.clone()));
        }
        Err(cause) => warn!("terminal de controle indisponivel: {cause}"),
    }

    if std::io::stdin().is_terminal() {
        tokio::spawn(async move {
            let _ = session(tokio::io::stdin(), tokio::io::stdout(), &http, &llm).await;
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

async fn accept_loop(listener: UnixListener, http: Arc<serenity::Http>, llm: Llm) {
    loop {
        match listener.accept().await {
            Ok((stream, _)) => {
                let http = http.clone();
                let llm = llm.clone();
                tokio::spawn(async move {
                    info!("sessao conectada pelo terminal de controle");
                    let (read, write) = stream.into_split();
                    if let Err(cause) = session(read, write, &http, &llm).await {
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
    llm: &Llm,
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
            Some(cmd) if cmd == "ia" || cmd.starts_with("ia ") => {
                ia_command(llm, cmd.trim_start_matches("ia").trim()).await
            }
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
