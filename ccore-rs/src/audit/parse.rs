use std::collections::HashMap;

use super::model::{key, same_name};
use crate::embeds::truncate;

const MAX_USERNAME: usize = 32;
const MAX_RANK: usize = 40;

pub struct LineFailure {
    pub line: usize,
    pub raw: String,
    pub reason: String,
}

impl LineFailure {
    pub fn describe(&self, width: usize) -> String {
        format!(
            "L{}: `{}` — {}",
            self.line,
            truncate(&self.raw, width),
            self.reason
        )
    }
}

pub struct ParsedLine {
    pub username: String,
    pub kills: i64,
    pub deaths: i64,
    pub assists: i64,
}

#[derive(Default)]
pub struct BattleParse {
    pub entries: Vec<ParsedLine>,
    pub failures: Vec<LineFailure>,
    pub duplicates: Vec<String>,
    pub warnings: Vec<String>,
}

/// \r\n, \n e \r soltos contam como uma quebra so, para o numero da linha bater
/// com o que a pessoa colou.
fn split_lines(block: &str) -> Vec<String> {
    block
        .replace("\r\n", "\n")
        .replace('\r', "\n")
        .split('\n')
        .map(str::to_owned)
        .collect()
}

fn is_number(token: &str) -> bool {
    token.parse::<i32>().is_ok()
}

/// Le o bloco do /audit-add: uma linha "nome k d a n" por jogador, onde n e so
/// formatacao. Le da direita para a esquerda, entao nome com espaco funciona.
pub fn parse_battle(block: &str) -> BattleParse {
    let mut result = BattleParse::default();
    let mut positions: HashMap<String, usize> = HashMap::new();

    for (i, line) in split_lines(block).iter().enumerate() {
        let number = i + 1;
        let raw = line.trim();
        if raw.is_empty() {
            continue;
        }

        let mut fail = |reason: &str| {
            result.failures.push(LineFailure {
                line: number,
                raw: raw.to_owned(),
                reason: reason.to_owned(),
            });
        };

        let tokens: Vec<&str> = raw.split_whitespace().collect();
        let numeric_tail = tokens.iter().rev().take_while(|t| is_number(t)).count();

        let count = if tokens.len() >= 5 && numeric_tail >= 4 {
            4
        } else if tokens.len() >= 4 && numeric_tail >= 3 {
            result.warnings.push(format!(
                "L{number}: sem o campo final `n`, assumido como ausente."
            ));
            3
        } else {
            fail("esperado `nome k d a n` com numeros no final");
            continue;
        };

        let split = tokens.len() - count;
        let name = tokens[..split].join(" ");
        let stats: Vec<i64> = tokens[split..split + 3]
            .iter()
            .map(|t| t.parse().unwrap_or(-1))
            .collect();

        let reason = if name.chars().count() > MAX_USERNAME {
            Some(format!("nome com mais de {MAX_USERNAME} caracteres"))
        } else if name.contains(',') {
            Some("nome nao pode conter virgula".to_owned())
        } else if is_number(&name) {
            Some("nome nao pode ser apenas numeros".to_owned())
        } else if stats.iter().any(|v| *v < 0) {
            Some("valores negativos nao sao aceitos".to_owned())
        } else {
            None
        };
        if let Some(reason) = reason {
            fail(&reason);
            continue;
        }

        if let Some(&p) = positions.get(&key(&name)) {
            let existing = &mut result.entries[p];
            existing.kills += stats[0];
            existing.deaths += stats[1];
            existing.assists += stats[2];
            if !result
                .duplicates
                .iter()
                .any(|d| same_name(d, &existing.username))
            {
                result.duplicates.push(existing.username.clone());
            }
            continue;
        }

        positions.insert(key(&name), result.entries.len());
        result.entries.push(ParsedLine {
            username: name,
            kills: stats[0],
            deaths: stats[1],
            assists: stats[2],
        });
    }

    result
}

pub struct ParsedRank {
    pub username: String,
    pub rank: String,
}

#[derive(Default)]
pub struct RankParse {
    pub entries: Vec<ParsedRank>,
    pub failures: Vec<LineFailure>,
    /// Nome repetido: vale a ultima linha.
    pub duplicates: Vec<String>,
}

/// Le o bloco do /audit-setranks: uma linha "nome = cargo". O separador e o
/// primeiro "=", e cargo vazio remove o cargo.
pub fn parse_ranks(block: &str) -> RankParse {
    let mut result = RankParse::default();
    let mut positions: HashMap<String, usize> = HashMap::new();

    for (i, line) in split_lines(block).iter().enumerate() {
        let raw = line.trim();
        if raw.is_empty() {
            continue;
        }

        let mut fail = |reason: String| {
            result.failures.push(LineFailure {
                line: i + 1,
                raw: raw.to_owned(),
                reason,
            });
        };

        let Some((name, rank)) = raw.split_once('=') else {
            fail("Falta o \"=\" separando o nome do cargo.".to_owned());
            continue;
        };
        let (name, rank) = (name.trim(), rank.trim());

        if name.is_empty() {
            fail("Nome do jogador vazio antes do \"=\".".to_owned());
            continue;
        }
        if name.chars().count() > MAX_USERNAME {
            fail(format!("Nome passa de {MAX_USERNAME} caracteres."));
            continue;
        }
        if rank.chars().count() > MAX_RANK {
            fail(format!("Cargo passa de {MAX_RANK} caracteres."));
            continue;
        }

        if let Some(&p) = positions.get(&key(name)) {
            result.entries[p].rank = rank.to_owned();
            result.duplicates.push(name.to_owned());
            continue;
        }

        positions.insert(key(name), result.entries.len());
        result.entries.push(ParsedRank {
            username: name.to_owned(),
            rank: rank.to_owned(),
        });
    }

    result
}

/// O bloco que vai pre-preenchido no modal, com o cargo atual de cada um.
pub fn build_rank_block<'a>(players: impl IntoIterator<Item = (&'a str, &'a str)>) -> String {
    players
        .into_iter()
        .map(|(name, rank)| format!("{name} = {rank}").trim_end().to_owned())
        .collect::<Vec<_>>()
        .join("\n")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn le_nome_com_espaco_e_descarta_o_n() {
        let parsed = parse_battle("Rafa Odebrecht 12 3 5 0\r\nAna 1 2 3");
        assert_eq!(parsed.entries[0].username, "Rafa Odebrecht");
        assert_eq!(parsed.entries[0].assists, 5);
        assert_eq!(parsed.entries[1].kills, 1);
        assert_eq!(
            parsed.warnings,
            vec!["L2: sem o campo final `n`, assumido como ausente."]
        );
    }

    #[test]
    fn soma_repetido_e_recusa_linha_ruim() {
        let parsed = parse_battle("Ana 1 1 1 0\nana 2 2 2 0\n\n12 3 4 5\nBia 1 -1 0 0\nsó texto");
        assert_eq!(parsed.entries.len(), 1);
        assert_eq!(parsed.entries[0].kills, 3);
        assert_eq!(parsed.duplicates, vec!["Ana"]);
        let lines: Vec<usize> = parsed.failures.iter().map(|f| f.line).collect();
        assert_eq!(lines, vec![4, 5, 6]);
    }

    #[test]
    fn cargo_usa_o_primeiro_igual_e_ultima_linha_vence() {
        let parsed = parse_ranks("Ana = Lance Serjeant\nBia = a=b\nana = \nsem igual");
        assert_eq!(parsed.entries.len(), 2);
        assert_eq!(parsed.entries[0].rank, "");
        assert_eq!(parsed.entries[1].rank, "a=b");
        assert_eq!(parsed.duplicates, vec!["ana"]);
        assert_eq!(parsed.failures[0].line, 4);
    }

    #[test]
    fn bloco_pre_preenchido_nao_deixa_espaco_sobrando() {
        assert_eq!(
            build_rank_block([("Ana", "Private"), ("Bia", "")]),
            "Ana = Private\nBia ="
        );
    }
}
