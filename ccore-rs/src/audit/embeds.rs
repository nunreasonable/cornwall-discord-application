use poise::serenity_prelude as serenity;

use super::model::{AuditEntry, AuditFile};
use crate::embeds::{fence_safe, truncate};

const MAX_EMBED_CHARS: usize = 5800;
const PAGE_CHARS: usize = 3500;

pub fn header() -> String {
    format!(
        "{:<18} {:<5} {:<5} {:<5} {:<5} Cargo",
        "Nome", "K", "D", "A", "Bat"
    )
}

/// Uma linha do relatorio. Nome e cargo passam pelo fence_safe: uma crase
/// tripla no nome fecharia o bloco de codigo.
pub fn format_entry(e: &AuditEntry) -> String {
    let rank = if e.rank.trim().is_empty() {
        "-"
    } else {
        &e.rank
    };
    format!(
        "{:<18} {:<5} {:<5} {:<5} {:<5} {}",
        fence_safe(&truncate(&e.username, 18)),
        e.kills,
        e.deaths,
        e.assists,
        e.battles,
        fence_safe(&truncate(rank, 18))
    )
}

/// Um jogador em bloco de codigo com cabecalho.
pub fn entry_block(e: &AuditEntry) -> String {
    format!("```\n{}\n{}\n```", header(), format_entry(e))
}

/// Junta as linhas em blocos de ate `max` caracteres, sem partir linha.
pub fn chunk(lines: impl IntoIterator<Item = String>, max: usize) -> Vec<String> {
    let mut chunks = Vec::new();
    let mut current = String::new();

    for line in lines {
        if !current.is_empty() && current.chars().count() + line.chars().count() > max {
            chunks.push(std::mem::take(&mut current));
        }
        current.push_str(&line);
    }
    if !current.is_empty() {
        chunks.push(current);
    }
    chunks
}

/// O relatorio vai num bloco de codigo na DESCRICAO, e nao em fields: as
/// colunas alinham e o limite de 25 fields fica fora de alcance.
pub fn audit_pages(
    audit: &AuditFile,
    rows: &[&AuditEntry],
    subtitle: &str,
) -> Vec<serenity::CreateEmbed> {
    if rows.is_empty() {
        return vec![
            serenity::CreateEmbed::new()
                .title("Auditoria")
                .description("Nenhum jogador registrado ainda. Use `/audit-add` para começar.")
                .colour(serenity::Colour::ORANGE),
        ];
    }

    let battles: i64 = audit.entries.iter().map(|e| e.battles).sum();
    let kills: i64 = audit.entries.iter().map(|e| e.kills).sum();
    let chunks = chunk(rows.iter().map(|e| format_entry(e) + "\n"), PAGE_CHARS);

    chunks
        .iter()
        .enumerate()
        .map(|(i, chunk)| {
            serenity::CreateEmbed::new()
                .title("Auditoria do regimento")
                .description(format!("{subtitle}\n```\n{}\n{chunk}```", header()))
                .colour(serenity::Colour::BLURPLE)
                .footer(serenity::CreateEmbedFooter::new(format!(
                    "Página {}/{} — {} jogadores, {battles} batalhas, {kills} kills",
                    i + 1,
                    chunks.len(),
                    audit.entries.len()
                )))
                .timestamp(audit.timestamp())
        })
        .collect()
}

/// Lista para um field (teto de 1024).
pub fn field_value<S: AsRef<str>>(items: impl IntoIterator<Item = S>) -> String {
    let text = items
        .into_iter()
        .map(|s| s.as_ref().to_owned())
        .collect::<Vec<_>>()
        .join("\n");

    if text.trim().is_empty() {
        "—".to_owned()
    } else if text.chars().count() <= 1024 {
        text
    } else {
        format!(
            "{}\n… (truncado)",
            text.chars().take(1000).collect::<String>()
        )
    }
}

/// Embed montado em partes, para poder ser medido antes de ir ao Discord: ele
/// recusa a resposta inteira se a soma passar de 6000 caracteres.
pub struct Draft {
    title: String,
    description: String,
    colour: serenity::Colour,
    footer: Option<String>,
    fields: Vec<(String, String, bool)>,
}

impl Draft {
    pub fn new(
        title: impl Into<String>,
        description: impl Into<String>,
        colour: serenity::Colour,
    ) -> Self {
        Self {
            title: title.into(),
            description: description.into(),
            colour,
            footer: None,
            fields: Vec::new(),
        }
    }

    pub fn field(
        mut self,
        name: impl Into<String>,
        value: impl Into<String>,
        inline: bool,
    ) -> Self {
        self.fields.push((name.into(), value.into(), inline));
        self
    }

    pub fn footer(mut self, text: impl Into<String>) -> Self {
        self.footer = Some(text.into());
        self
    }

    fn size(&self) -> usize {
        let fields: usize = self
            .fields
            .iter()
            .map(|(n, v, _)| n.chars().count() + v.chars().count())
            .sum();
        self.title.chars().count()
            + self.description.chars().count()
            + self.footer.as_ref().map_or(0, |f| f.chars().count())
            + fields
    }

    /// Tira fields do fim ate caber, e avisa no lugar deles.
    fn fit(&mut self) {
        let mut dropped = 0;
        while self.size() > MAX_EMBED_CHARS && self.fields.pop().is_some() {
            dropped += 1;
        }
        if dropped > 0 && self.size() + 80 <= MAX_EMBED_CHARS {
            self.fields.push((
                "…".to_owned(),
                format!("{dropped} bloco(s) omitido(s): a resposta não cabe no limite do Discord."),
                false,
            ));
        }
        if self.size() > MAX_EMBED_CHARS {
            let room = MAX_EMBED_CHARS.saturating_sub(self.title.chars().count() + 200);
            self.description = truncate(&self.description, room);
        }
    }

    pub fn build(mut self) -> serenity::CreateEmbed {
        self.fit();
        let mut embed = serenity::CreateEmbed::new()
            .title(self.title)
            .description(self.description)
            .colour(self.colour)
            .timestamp(serenity::Timestamp::now())
            .fields(self.fields);
        if let Some(footer) = self.footer {
            embed = embed.footer(serenity::CreateEmbedFooter::new(footer));
        }
        embed
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn colunas_alinham_e_crase_nao_escapa() {
        let plain = AuditEntry {
            username: "Ana".to_owned(),
            kills: 12,
            ..AuditEntry::default()
        };
        assert_eq!(header().find('K'), format_entry(&plain).find("12"));

        let fenced = AuditEntry {
            username: "a```b".to_owned(),
            ..AuditEntry::default()
        };
        assert!(!format_entry(&fenced).contains("```"));
    }

    #[test]
    fn blocos_nao_partem_linha() {
        let lines = (0..10).map(|i| format!("{i}{}\n", "x".repeat(8)));
        let chunks = chunk(lines, 25);
        assert!(
            chunks
                .iter()
                .all(|c| c.chars().count() <= 25 && c.ends_with('\n'))
        );
        assert_eq!(chunks.concat().lines().count(), 10);
    }

    #[test]
    fn draft_tira_fields_ate_caber_e_avisa() {
        let mut draft = Draft::new("t", "d", serenity::Colour::BLURPLE);
        for i in 0..10 {
            draft = draft.field(format!("f{i}"), "x".repeat(1000), false);
        }
        draft.fit();
        assert!(draft.size() <= MAX_EMBED_CHARS);
        assert_eq!(draft.fields.len(), 6);
        assert!(draft.fields[5].1.starts_with("5 bloco(s)"));
    }
}
