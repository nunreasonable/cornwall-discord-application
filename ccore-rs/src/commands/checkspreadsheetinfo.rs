use poise::{CreateReply, futures_util::future::join_all, serenity_prelude as serenity};
use unicode_normalization::{UnicodeNormalization, char::is_combining_mark};

use crate::{
    Context, Error,
    config::SpreadsheetTab,
    embeds::{self, truncate},
    http,
    sheet::parse_csv,
    staff,
};

const USERNAME_HEADERS: [&str; 3] = ["NOME DE USUARIO", "NOME DO USUARIO", "USERNAME"];
const OLD_RANK_HEADER: &str = "PATENTE ANTIGA";
const NEW_RANK_HEADER: &str = "PROMOCAO";
const HEADER_SEARCH_ROWS: usize = 10;

const MAX_TOTAL_CHARS: usize = 5500;
const MAX_FIELD_CHARS: usize = 900;
const MAX_FIELDS: usize = 24;

struct Loaded {
    labels: Vec<String>,
    row: Option<Vec<String>>,
    username_column: usize,
}

type TabResult<'a> = (&'a SpreadsheetTab, Result<Loaded, String>);

/// Verifica as informações da planilha Regimental
#[poise::command(slash_command)]
pub async fn checkspreadsheetinfo(
    ctx: Context<'_>,
    #[description = "Seu nome de usuário na planilha."] username: String,
) -> Result<(), Error> {
    // Efemero: a linha da planilha traz observacoes internas.
    ctx.defer_ephemeral().await?;

    if let Some(denied) = staff::check(ctx).await {
        return send(ctx, denied).await;
    }

    let tabs: Vec<&SpreadsheetTab> = ctx
        .data()
        .config
        .spreadsheet_info_tabs
        .iter()
        .filter(|tab| !tab.csv_url.trim().is_empty())
        .collect();

    if tabs.is_empty() {
        return send(
            ctx,
            embeds::error(
                "Erro ao buscar planilha",
                "Nenhuma aba da planilha esta configurada. Preencha `spreadsheetInfoTabs` no config.json.",
            ),
        )
        .await;
    }

    // Juntas: em sequencia, as idas ao Google somariam dentro do mesmo prazo.
    let http = &ctx.data().http;
    let username = username.trim();
    let results: Vec<TabResult<'_>> = join_all(tabs.iter().map(|&tab| async move {
        let loaded = match http::fetch_text(http, &tab.csv_url).await {
            Ok(text) => load_tab(&text, username),
            Err(cause) => Err(cause),
        };
        (tab, loaded)
    }))
    .await;

    let failures: Vec<(&SpreadsheetTab, &str)> = results
        .iter()
        .filter_map(|(tab, r)| r.as_ref().err().map(|e| (*tab, e.as_str())))
        .collect();
    let matches: Vec<(&SpreadsheetTab, &Loaded)> = results
        .iter()
        .filter_map(|(tab, r)| {
            r.as_ref()
                .ok()
                .filter(|l| l.row.is_some())
                .map(|l| (*tab, l))
        })
        .collect();

    let embed = if matches.is_empty() {
        not_found_embed(username, &results, &failures, tabs.len())
    } else {
        found_embed(username, &matches, &failures)
    };

    send(ctx, embed).await
}

async fn send(ctx: Context<'_>, embed: serenity::CreateEmbed) -> Result<(), Error> {
    ctx.send(CreateReply::default().embed(embed).ephemeral(true))
        .await?;
    Ok(())
}

fn load_tab(text: &str, username: &str) -> Result<Loaded, String> {
    let records = parse_csv(text)?;
    if records.is_empty() {
        return Err("A aba retornou um CSV vazio.".to_owned());
    }

    let (header, username_column) =
        find_header(&records).ok_or("Não achei a coluna do nome de usuário nesta aba.")?;

    let wanted = username.to_lowercase();
    let row = records[header + 1..]
        .iter()
        .find(|row| {
            row.get(username_column)
                .is_some_and(|cell| cell.trim().to_lowercase() == wanted)
        })
        .cloned();

    Ok(Loaded {
        labels: records[header].clone(),
        row,
        username_column,
    })
}

/// As abas nao poem o cabecalho na mesma posicao: PROMOCOES comeca na coluna K.
fn find_header(records: &[Vec<String>]) -> Option<(usize, usize)> {
    records
        .iter()
        .take(HEADER_SEARCH_ROWS)
        .enumerate()
        .find_map(|(r, row)| {
            row.iter()
                .position(|cell| USERNAME_HEADERS.contains(&normalize(cell).as_str()))
                .map(|column| (r, column))
        })
}

/// Maiusculo, sem acento e com espacos colapsados, para comparar cabecalho.
fn normalize(value: &str) -> String {
    let plain: String = value
        .to_uppercase()
        .nfd()
        .filter(|c| !is_combining_mark(*c))
        .collect();

    plain.split_whitespace().collect::<Vec<_>>().join(" ")
}

fn not_found_embed(
    username: &str,
    results: &[TabResult<'_>],
    failures: &[(&SpreadsheetTab, &str)],
    tab_count: usize,
) -> serenity::CreateEmbed {
    let all_failed = failures.len() == tab_count;
    let (title, text) = if all_failed {
        (
            "Erro ao buscar planilha",
            "Não foi possível ler nenhuma aba da planilha. Verifique o URL e a disponibilidade dela.".to_owned(),
        )
    } else if failures.is_empty() {
        (
            "Usuário não encontrado",
            format!("Nenhum registro para **{username}** foi encontrado na planilha."),
        )
    } else {
        // Com aba fora do ar, "nao existe" seria mentira: ele pode estar nela.
        (
            "Usuário não encontrado",
            format!(
                "Nenhum registro para **{username}** nas abas lidas — mas {} aba(s) falharam, então ele ainda pode estar numa delas.",
                failures.len()
            ),
        )
    };

    let mut embed = embeds::error(title, text);

    let searched: Vec<&str> = results
        .iter()
        .filter(|(_, r)| r.is_ok())
        .map(|(tab, _)| tab.name.as_str())
        .collect();
    if !searched.is_empty() {
        embed = embed.field(
            "Abas pesquisadas",
            truncate(&searched.join(", "), 1024),
            false,
        );
    }

    for (tab, error) in failures.iter().take(4) {
        embed = embed.field(
            format!("Falha em {}", tab.name),
            truncate(error, 1024),
            false,
        );
    }

    embed
}

fn found_embed(
    username: &str,
    matches: &[(&SpreadsheetTab, &Loaded)],
    failures: &[(&SpreadsheetTab, &str)],
) -> serenity::CreateEmbed {
    let found_in: Vec<&str> = matches.iter().map(|(tab, _)| tab.name.as_str()).collect();
    let description = format!(
        "Dados encontrados para **{username}** em {}.",
        found_in.join(", ")
    );

    let mut budget = EmbedBudget::new(description.chars().count());

    for (tab, loaded) in matches.iter().filter(|(tab, _)| !tab.promotions) {
        let lines = describe_row(loaded, |_| false);
        if !lines.is_empty() {
            budget.add_section(&tab.name, &lines);
        }
    }

    for (_, loaded) in matches.iter().filter(|(tab, _)| tab.promotions) {
        let lines = describe_promotion(loaded);
        if !lines.is_empty() {
            budget.add_section("Promoção", &lines);
        }
    }

    for (tab, error) in failures.iter().take(2) {
        budget.add_section(&format!("Falha em {}", tab.name), &[(*error).to_owned()]);
    }

    let mut embed = serenity::CreateEmbed::new()
        .title("Informações da planilha")
        .description(description)
        .colour(serenity::Colour::BLURPLE)
        .timestamp(serenity::Timestamp::now())
        .fields(
            budget
                .fields
                .iter()
                .map(|(n, v)| (n.as_str(), v.as_str(), false)),
        );

    if budget.truncated {
        embed = embed.footer(serenity::CreateEmbedFooter::new(
            "Parte das colunas foi omitida: não cabe no limite de uma mensagem do Discord.",
        ));
    }

    embed
}

/// Uma linha "rotulo: valor" por coluna preenchida. A coluna do nome sai: o
/// proprio pedido ja disse quem e.
fn describe_row(loaded: &Loaded, skip: impl Fn(&str) -> bool) -> Vec<String> {
    let Some(row) = &loaded.row else {
        return Vec::new();
    };

    let mut lines = Vec::new();
    for i in 0..loaded.labels.len().max(row.len()) {
        if i == loaded.username_column {
            continue;
        }

        let label = loaded.labels.get(i).map_or("", |l| l.trim());
        let value = row.get(i).map_or("", |v| v.trim());

        if label.is_empty() && value.is_empty() || skip(&normalize(label)) {
            continue;
        }

        let label = if label.is_empty() {
            format!("Coluna {}", i + 1)
        } else {
            label.to_owned()
        };
        let value = if value.is_empty() { "(vazio)" } else { value };
        lines.push(format!("**{label}**: {value}"));
    }
    lines
}

/// "antiga -> nova" de relance, e o resto das colunas abaixo.
fn describe_promotion(loaded: &Loaded) -> Vec<String> {
    let value_of = |header: &str| -> &str {
        loaded
            .labels
            .iter()
            .position(|label| normalize(label) == header)
            .and_then(|i| loaded.row.as_ref()?.get(i))
            .map_or("", |v| v.trim())
    };

    let old = value_of(OLD_RANK_HEADER);
    let new = value_of(NEW_RANK_HEADER);

    let mut lines = Vec::new();
    if !old.is_empty() || !new.is_empty() {
        let or_unknown = |v: &'_ str| {
            if v.is_empty() {
                "?".to_owned()
            } else {
                v.to_owned()
            }
        };
        lines.push(format!("**{}** → **{}**", or_unknown(old), or_unknown(new)));
    }

    lines.extend(describe_row(loaded, |label| {
        label == OLD_RANK_HEADER || label == NEW_RANK_HEADER
    }));
    lines
}

/// Respeita os limites do Discord: 1024 por field e 6000 no embed inteiro.
struct EmbedBudget {
    fields: Vec<(String, String)>,
    total_chars: usize,
    truncated: bool,
}

impl EmbedBudget {
    fn new(used: usize) -> Self {
        Self {
            fields: Vec::new(),
            total_chars: used,
            truncated: false,
        }
    }

    fn add_section(&mut self, name: &str, lines: &[String]) {
        let name = if name.trim().is_empty() {
            "Planilha"
        } else {
            name
        };
        let mut text = String::new();
        let mut part = 1;

        for raw in lines {
            let line = truncate(raw, MAX_FIELD_CHARS);
            if text.chars().count() + line.chars().count() + 1 > MAX_FIELD_CHARS {
                self.flush(name, &mut part, &mut text);
                if self.truncated {
                    return;
                }
            }

            if !text.is_empty() {
                text.push('\n');
            }
            text.push_str(&line);
        }

        self.flush(name, &mut part, &mut text);
    }

    fn flush(&mut self, name: &str, part: &mut usize, text: &mut String) {
        // Field sem valor faz o Discord recusar a resposta inteira.
        if text.is_empty() {
            return;
        }

        let field_name = if *part == 1 {
            name.to_owned()
        } else {
            format!("{name} (parte {part})")
        };
        let size = field_name.chars().count() + text.chars().count();

        if self.fields.len() >= MAX_FIELDS || self.total_chars + size > MAX_TOTAL_CHARS {
            self.truncated = true;
            text.clear();
            return;
        }

        self.fields.push((field_name, std::mem::take(text)));
        self.total_chars += size;
        *part += 1;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const CSV: &str = ",,PATENTE ANTIGA,Promoção,Nome do Usuário,Nota\n\
                       ,,Soldado,Cabo,Sr.Miojao,\"linha 1\nlinha 2\"\n\
                       ,,Cabo,Sargento,Outro\n";

    #[test]
    fn normaliza_acento_caixa_e_espacos() {
        assert_eq!(normalize("  Nome   do Usuário "), "NOME DO USUARIO");
        assert_eq!(normalize("Promoção"), "PROMOCAO");
    }

    #[test]
    fn acha_coluna_e_linha_mesmo_com_quebra_dentro_de_aspas() {
        let loaded = load_tab(CSV, "sr.miojao").unwrap();
        assert_eq!(loaded.username_column, 4);
        assert_eq!(loaded.row.as_ref().unwrap()[5], "linha 1\nlinha 2");
    }

    #[test]
    fn usuario_ausente_nao_e_erro_e_aba_sem_cabecalho_e() {
        assert!(load_tab(CSV, "ninguem").unwrap().row.is_none());
        assert!(load_tab("a,b\nc,d\n", "x").is_err());
        assert!(load_tab("", "x").is_err());
    }

    #[test]
    fn promocao_resume_as_patentes_e_nao_as_repete() {
        let lines = describe_promotion(&load_tab(CSV, "Sr.Miojao").unwrap());
        assert_eq!(lines[0], "**Soldado** → **Cabo**");
        assert_eq!(lines.len(), 2);
        assert!(lines[1].starts_with("**Nota**:"));
    }

    #[test]
    fn budget_respeita_limites_e_avisa_que_cortou() {
        let lines: Vec<String> = (0..200)
            .map(|i| format!("{i}: {}", "ç".repeat(100)))
            .collect();
        let mut budget = EmbedBudget::new(0);
        budget.add_section("Aba", &lines);

        assert!(budget.truncated);
        assert!(budget.fields.len() <= MAX_FIELDS);
        assert!(
            budget
                .fields
                .iter()
                .all(|(_, v)| !v.is_empty() && v.chars().count() <= 1024)
        );
        let total: usize = budget
            .fields
            .iter()
            .map(|(n, v)| n.chars().count() + v.chars().count())
            .sum();
        assert!(total <= MAX_TOTAL_CHARS);
    }
}
