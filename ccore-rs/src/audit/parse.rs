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

/// O `n` que o jogo usa como quebra de linha. Nao tem valor: so fecha o
/// jogador.
fn is_literal_break(token: &str) -> bool {
    matches!(token, "n" | "N" | "\\n" | "/n")
}

/// Le o bloco do /audit-add: `nome k d a n` por jogador. O `n` e a quebra de
/// linha do jogo: fecha o jogador e comeca o proximo, entao o bloco pode vir
/// todo numa linha so (`ana 1 2 3 n bia 4 5 6 n`) ou um por linha. Quebra de
/// linha de verdade depois de `k d a` tambem fecha, com aviso de que faltou o
/// `n`.
pub fn parse_battle(block: &str) -> BattleParse {
    let mut result = BattleParse::default();
    let mut positions: HashMap<String, usize> = HashMap::new();

    // Um jogador em montagem: nome, numeros e a linha onde comecou.
    let mut name: Vec<String> = Vec::new();
    let mut nums: Vec<String> = Vec::new();
    let mut start = 0usize;

    let mut emit = |name: &mut Vec<String>,
                    nums: &mut Vec<String>,
                    start: usize,
                    closed: bool,
                    result: &mut BattleParse| {
        let raw = name
            .iter()
            .chain(nums.iter())
            .cloned()
            .collect::<Vec<_>>()
            .join(" ");
        let joined = name.join(" ");
        let stats: Vec<i64> = nums
            .iter()
            .take(3)
            .map(|t| t.parse().unwrap_or(-1))
            .collect();
        name.clear();
        nums.clear();
        if !closed {
            result.warnings.push(format!(
                "L{start}: sem o campo final `n`, assumido como ausente."
            ));
        }
        let reason = if joined.is_empty() {
            Some("falta o nome antes dos numeros".to_owned())
        } else if joined.chars().count() > MAX_USERNAME {
            Some(format!("nome com mais de {MAX_USERNAME} caracteres"))
        } else if joined.contains(',') {
            Some("nome nao pode conter virgula".to_owned())
        } else if stats.len() < 3 {
            Some("esperado `nome k d a n` com numeros no final".to_owned())
        } else if stats.iter().any(|v| *v < 0) {
            Some("valores negativos nao sao aceitos".to_owned())
        } else {
            None
        };
        if let Some(reason) = reason {
            result.failures.push(LineFailure {
                line: start,
                raw,
                reason,
            });
            return;
        }
        if let Some(&p) = positions.get(&key(&joined)) {
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
            return;
        }
        positions.insert(key(&joined), result.entries.len());
        result.entries.push(ParsedLine {
            username: joined,
            kills: stats[0],
            deaths: stats[1],
            assists: stats[2],
        });
    };

    // Jogador incompleto (sem k d a): vira falha com o que tinha.
    let fail_partial =
        |name: &mut Vec<String>, nums: &mut Vec<String>, start: usize, result: &mut BattleParse| {
            let raw = name
                .iter()
                .chain(nums.iter())
                .cloned()
                .collect::<Vec<_>>()
                .join(" ");
            name.clear();
            nums.clear();
            result.failures.push(LineFailure {
                line: start,
                raw,
                reason: "esperado `nome k d a n` com numeros no final".to_owned(),
            });
        };

    for (i, line) in split_lines(block).iter().enumerate() {
        let number = i + 1;
        for token in line.split_whitespace() {
            if name.is_empty() && nums.is_empty() {
                start = number;
            }
            if is_literal_break(token) {
                // O `n` fecha o jogador. Solto (sem nada antes), nao conta.
                if nums.len() == 3 {
                    emit(&mut name, &mut nums, start, true, &mut result);
                } else if !name.is_empty() || !nums.is_empty() {
                    fail_partial(&mut name, &mut nums, start, &mut result);
                }
            } else if is_number(token) {
                nums.push(token.to_owned());
                // Um quarto numero: k d a ja vieram e o `n` nao.
                if nums.len() == 4 {
                    fail_partial(&mut name, &mut nums, start, &mut result);
                }
            } else {
                // Palavra depois de numeros: o jogador anterior acabou.
                if nums.len() == 3 {
                    emit(&mut name, &mut nums, start, false, &mut result);
                    start = number;
                } else if !nums.is_empty() {
                    fail_partial(&mut name, &mut nums, start, &mut result);
                    start = number;
                }
                name.push(token.to_owned());
            }
        }
        // Fim da linha: fecha quem ja tem k d a; o resto nao continua na
        // linha de baixo.
        if nums.len() == 3 {
            emit(&mut name, &mut nums, start, false, &mut result);
        } else if !name.is_empty() || !nums.is_empty() {
            fail_partial(&mut name, &mut nums, start, &mut result);
        }
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
        let parsed = parse_battle("Rafa Odebrecht 12 3 5 n\r\nAna 1 2 3");
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
        let parsed = parse_battle("Ana 1 1 1 n\nana 2 2 2 n\n\n12 3 4 n\nBia 1 -1 0 n\nsó texto");
        assert_eq!(parsed.entries.len(), 1);
        assert_eq!(parsed.entries[0].kills, 3);
        assert_eq!(parsed.duplicates, vec!["Ana"]);
        let lines: Vec<usize> = parsed.failures.iter().map(|f| f.line).collect();
        assert_eq!(lines, vec![4, 5, 6]);
    }

    #[test]
    fn o_n_separa_jogadores_na_mesma_linha() {
        let parsed = parse_battle("ana 1 2 3 n bia 4 5 6 n caio 7 8 9 n");
        let names: Vec<&str> = parsed.entries.iter().map(|e| e.username.as_str()).collect();
        assert_eq!(names, ["ana", "bia", "caio"]);
        assert_eq!(parsed.entries[2].assists, 9);
        assert!(parsed.failures.is_empty());
        assert!(parsed.warnings.is_empty());
    }

    #[test]
    fn numero_no_lugar_do_n_e_recusado() {
        let parsed = parse_battle("ana 1 2 3 0 bia 4 5 6 n");
        assert_eq!(parsed.entries.len(), 1);
        assert_eq!(parsed.entries[0].username, "bia");
        assert_eq!(parsed.failures.len(), 1);
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
