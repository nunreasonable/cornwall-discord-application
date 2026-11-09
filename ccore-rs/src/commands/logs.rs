use poise::{CreateReply, serenity_prelude as serenity};

use crate::{
    Context, Error,
    log_buffer::{LogBuffer, LogLine, Severity, truncate},
    paginate,
};

const MAX_CHARS_PER_PAGE: usize = 3200;

#[derive(poise::ChoiceParameter, Clone, Copy, PartialEq, Eq)]
pub enum LevelFilter {
    #[name = "Todos"]
    Todos,
    #[name = "Apenas erros"]
    Erros,
    #[name = "Apenas avisos"]
    Avisos,
    #[name = "Apenas informativos"]
    Informativos,
}

impl LevelFilter {
    fn severity(self) -> Option<Severity> {
        match self {
            Self::Todos => None,
            Self::Erros => Some(Severity::Erro),
            Self::Avisos => Some(Severity::Aviso),
            Self::Informativos => Some(Severity::Info),
        }
    }

    fn label(self) -> &'static str {
        match self {
            Self::Todos => "todos",
            Self::Erros => "erro",
            Self::Avisos => "aviso",
            Self::Informativos => "info",
        }
    }
}

/// Mostra os últimos logs do bot
#[poise::command(slash_command)]
pub async fn logs(
    ctx: Context<'_>,
    #[description = "Quantas linhas mostrar (1 a 100, padrão 25)"]
    #[min = 1]
    #[max = 100]
    quantidade: Option<u32>,
    #[description = "Filtrar por severidade"] nivel: Option<LevelFilter>,
    #[description = "Mostrar apenas linhas que contenham este texto"] filtro: Option<String>,
) -> Result<(), Error> {
    // Sempre efemero: os logs carregam ids, excecoes e caminhos da maquina.
    ctx.defer_ephemeral().await?;

    if let Some(denied) = check_staff(ctx).await {
        ctx.send(CreateReply::default().embed(denied).ephemeral(true))
            .await?;
        return Ok(());
    }

    let take = quantidade.unwrap_or(25).clamp(1, 100) as usize;
    let nivel = nivel.unwrap_or(LevelFilter::Todos);
    let filtro = filtro
        .as_deref()
        .map(str::trim)
        .filter(|text| !text.is_empty());

    let buffer = &ctx.data().logs;
    let lines = buffer.snapshot(take, nivel.severity(), filtro);
    let scope = scope_text(buffer, nivel, filtro, take, lines.len());

    if lines.is_empty() {
        let text = if buffer.total_seen() == 0 {
            "Nenhuma linha capturada ainda. O buffer começa vazio a cada reinício do bot."
                .to_owned()
        } else {
            format!("Nenhuma linha corresponde ao filtro.\n{scope}")
        };

        let embed = serenity::CreateEmbed::new()
            .title("Logs do bot")
            .description(text)
            .colour(serenity::Colour::ORANGE);
        ctx.send(CreateReply::default().embed(embed).ephemeral(true))
            .await?;
        return Ok(());
    }

    let chunks = split_pages(&lines, MAX_CHARS_PER_PAGE);
    let total = chunks.len();
    let pages = chunks
        .iter()
        .enumerate()
        .map(|(i, chunk)| {
            serenity::CreateEmbed::new()
                .title("Logs do bot")
                .description(format!("{scope}\n```\n{}```", fence_safe(chunk)))
                .colour(serenity::Colour::BLURPLE)
                .footer(serenity::CreateEmbedFooter::new(format!(
                    "Página {}/{total} — mais recente primeiro",
                    i + 1
                )))
                .timestamp(serenity::Timestamp::now())
        })
        .collect();

    paginate::ephemeral(ctx, pages).await
}

/// None quando pode usar; senao, o embed de recusa. Mesmo criterio do portao
/// de staff do C#: ter o cargo em `enlistPermissionRoleId`.
async fn check_staff(ctx: Context<'_>) -> Option<serenity::CreateEmbed> {
    let error = |title: &str, text: String| {
        Some(
            serenity::CreateEmbed::new()
                .title(title)
                .description(text)
                .colour(serenity::Colour::new(0xCD5C5C)),
        )
    };

    let Some(member) = ctx.author_member().await else {
        return error(
            "Comando indisponível",
            "Este comando só pode ser usado dentro do servidor.".to_owned(),
        );
    };

    let Some(role_id) = ctx.data().config.enlist_permission_role_id else {
        return error(
            "Configuração ausente",
            "`enlistPermissionRoleId` não está definido no config.jsonc.".to_owned(),
        );
    };

    if member.roles.contains(&serenity::RoleId::new(role_id)) {
        return None;
    }

    error(
        "Permissão negada",
        format!("Você precisa do cargo <@&{role_id}> para usar este comando."),
    )
}

fn scope_text(
    buffer: &LogBuffer,
    nivel: LevelFilter,
    filtro: Option<&str>,
    take: usize,
    shown: usize,
) -> String {
    let counts = buffer.counts();
    let totals = format!(
        "Buffer: {} info · {} aviso · {} erro.",
        counts.info, counts.aviso, counts.erro
    );

    let mut parts = Vec::new();
    if nivel != LevelFilter::Todos {
        parts.push(format!("nível: `{}`", nivel.label()));
    }
    if let Some(text) = filtro {
        parts.push(format!("texto: `{}`", truncate(text, 40)));
    }

    if parts.is_empty() {
        format!("Últimas {shown} de até {take} linha(s). {totals}")
    } else {
        format!(
            "Filtro — {} · {shown} linha(s). {totals}",
            parts.join(" · ")
        )
    }
}

fn split_pages(lines: &[LogLine], max_chars: usize) -> Vec<String> {
    let mut pages = Vec::new();
    let mut page = String::new();
    let mut page_chars = 0;

    for line in lines {
        let text = format!(
            "[{}] {:<5} {}\n",
            line.at.format("%H:%M:%S"),
            line.severity.name(),
            line.text
        );
        let chars = text.chars().count();

        if page_chars + chars > max_chars && !page.is_empty() {
            pages.push(std::mem::take(&mut page));
            page_chars = 0;
        }

        page.push_str(&text);
        page_chars += chars;
    }

    if !page.is_empty() {
        pages.push(page);
    }
    pages
}

/// Uma crase tripla dentro do log fecharia o bloco de codigo do embed.
fn fence_safe(text: &str) -> String {
    text.replace("```", "`\u{200b}`\u{200b}`")
}

#[cfg(test)]
mod tests {
    use chrono::Local;

    use super::*;

    fn line(text: &str) -> LogLine {
        LogLine {
            at: Local::now(),
            severity: Severity::Erro,
            text: text.to_owned(),
        }
    }

    #[test]
    fn paginas_respeitam_o_limite_e_nao_perdem_linhas() {
        let lines: Vec<_> = (0..100).map(|_| line(&"é".repeat(100))).collect();
        let pages = split_pages(&lines, 3200);

        assert!(pages.len() > 1);
        assert!(pages.iter().all(|p| p.chars().count() <= 3200));
        assert_eq!(pages.iter().map(|p| p.lines().count()).sum::<usize>(), 100);
    }

    #[test]
    fn linha_vira_hora_nivel_e_texto() {
        let pages = split_pages(&[line("falhou")], 3200);
        assert!(pages[0].contains("] ERRO  falhou\n"));
    }

    #[test]
    fn crase_tripla_nao_fecha_o_bloco() {
        assert!(!fence_safe("a```b").contains("```"));
    }
}
