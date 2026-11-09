//! A escada de promocoes do 12° Regimento. So responde elegibilidade: do
//! Kingsman para cima o criterio tem parte humana, e a promocao continua sendo
//! decisao do comando do regimento.

use super::model::{AuditEntry, key};

pub struct Rank {
    /// Exatamente como aparece no `rank` da auditoria.
    pub name: &'static str,
    /// Nome do emoji no servidor, resolvido na hora.
    pub emoji: &'static str,
    /// None quando nao ha criterio numerico.
    pub required: Option<i64>,
    pub needs_approval: bool,
    pub criteria: &'static str,
}

/// Da patente mais baixa para a mais alta. A ordem E a escada.
pub static RANKS: [Rank; 7] = [
    Rank {
        name: "Fencible",
        emoji: "Fencible",
        required: Some(1),
        needs_approval: false,
        criteria: "Participar de 1 batalha e ficar nela do início ao fim.",
    },
    Rank {
        name: "Regular",
        emoji: "Regular",
        required: Some(3),
        needs_approval: false,
        criteria: "Participar de 3 batalhas e ficar nelas do início ao fim.",
    },
    Rank {
        name: "Private",
        emoji: "Private",
        required: Some(5),
        needs_approval: false,
        criteria: "Participar de 5 eventos por completo.",
    },
    Rank {
        name: "Kingsman",
        emoji: "Kingsman",
        required: Some(10),
        needs_approval: true,
        criteria: "Participar de 10+ eventos e ser um soldado capaz; dado aos melhores recrutas do regimento.",
    },
    Rank {
        name: "Lance Corporal",
        emoji: "LanceCorporal",
        required: Some(20),
        needs_approval: true,
        criteria: "Participar de 20 eventos com atividade consistente e usar o cérebro.",
    },
    Rank {
        name: "Corporal",
        emoji: "Corporal",
        required: Some(35),
        needs_approval: true,
        criteria: "Participar de 35 eventos com atividade consistente e competência ao nível de granadeiro.",
    },
    // Sem numero: esta na escada para aparecer como destino, mas nunca e
    // calculada a partir das batalhas.
    Rank {
        name: "Lance Serjeant",
        emoji: "lance_serjeant",
        required: None,
        needs_approval: true,
        criteria: "Patente inicial de NCO, dada àqueles com contribuições significativas para o regimento.",
    },
];

/// Acima da escada: progressao de NCO/oficial nao e por contagem de batalha.
const ABOVE_LADDER: [&str; 11] = [
    "Serjeant",
    "Colour Serjeant",
    "Quartermaster-Serjeant",
    "Quartermaster Serjeant",
    "Serjeant Major",
    "Ensign",
    "Lieutenant",
    "Captain",
    "Major",
    "Lieutenant Colonel",
    "Colonel",
];

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Status {
    /// No degrau que os numeros justificam.
    UpToDate,
    /// Alcancou um degrau acima, e o criterio e so numerico.
    Eligible,
    /// Alcancou o numero, mas o degrau exige avaliacao humana.
    EligibleNeedsApproval,
    OutsideLadder,
    /// Nao chega nem ao primeiro degrau.
    BelowFirstRank,
}

pub struct Evaluation<'a> {
    pub username: &'a str,
    pub current_name: &'a str,
    pub battles: i64,
    pub status: Status,
    pub current: Option<&'static Rank>,
    /// O degrau MAIS ALTO que as batalhas alcancam, quando e acima do atual.
    pub target: Option<&'static Rank>,
    pub steps: usize,
    /// O proximo degrau ainda nao alcancado, para mostrar o que falta.
    pub next: Option<&'static Rank>,
    pub to_next: i64,
}

impl Evaluation<'_> {
    pub fn is_promotable(&self) -> bool {
        matches!(
            self.status,
            Status::Eligible | Status::EligibleNeedsApproval
        )
    }
}

pub fn find(name: &str) -> Option<&'static Rank> {
    let wanted = key(name.trim());
    RANKS.iter().find(|r| key(r.name) == wanted)
}

fn position(rank: &Rank) -> usize {
    RANKS
        .iter()
        .position(|r| std::ptr::eq(r, rank))
        .unwrap_or_default()
}

/// O alvo e o degrau mais alto alcancado, e nao o seguinte: um Fencible com
/// 40 batalhas esta elegivel a Corporal.
pub fn evaluate(entry: &AuditEntry) -> Evaluation<'_> {
    let name = entry.rank.trim();
    let battles = entry.battles;
    let mut result = Evaluation {
        username: &entry.username,
        current_name: name,
        battles,
        status: Status::UpToDate,
        current: None,
        target: None,
        steps: 0,
        next: None,
        to_next: 0,
    };

    if ABOVE_LADDER.iter().any(|r| key(r) == key(name)) {
        result.status = Status::OutsideLadder;
        return result;
    }

    result.current = find(name);

    // Lance Serjeant e o topo e nao tem numero: nao ha para onde subir.
    if result.current.is_some_and(|r| r.required.is_none()) {
        return result;
    }

    let numeric = || RANKS.iter().filter_map(|r| r.required.map(|n| (r, n)));
    result.next = numeric().find(|(_, n)| battles < *n).map(|(r, _)| r);
    result.to_next = numeric()
        .find(|(_, n)| battles < *n)
        .map_or(0, |(_, n)| (n - battles).max(0));

    // Inclui cargo vazio e "Recruit", que existem no arquivo real.
    let Some((reached, _)) = numeric().rfind(|(_, n)| battles >= *n) else {
        result.status = Status::BelowFirstRank;
        return result;
    };

    let reached_at = position(reached);
    let current_at = result.current.map(position);
    if current_at.is_some_and(|at| reached_at <= at) {
        return result;
    }

    result.status = if reached.needs_approval {
        Status::EligibleNeedsApproval
    } else {
        Status::Eligible
    };
    result.target = Some(reached);
    result.steps = reached_at + 1 - current_at.map_or(0, |at| at + 1);
    result
}

#[cfg(test)]
mod tests {
    use super::*;

    fn eval(rank: &str, battles: i64) -> (Status, Option<&'static str>, usize, i64) {
        let entry = AuditEntry {
            rank: rank.to_owned(),
            battles,
            ..AuditEntry::default()
        };
        let e = evaluate(&entry);
        (e.status, e.target.map(|r| r.name), e.steps, e.to_next)
    }

    #[test]
    fn alvo_e_o_degrau_mais_alto_alcancado() {
        assert_eq!(
            eval("Fencible", 40),
            (Status::EligibleNeedsApproval, Some("Corporal"), 5, 0)
        );
        assert_eq!(
            eval("Recruit", 3),
            (Status::Eligible, Some("Regular"), 2, 2)
        );
        assert_eq!(
            eval("regular", 5),
            (Status::Eligible, Some("Private"), 1, 5)
        );
    }

    #[test]
    fn sem_promocao_mostra_o_que_falta() {
        assert_eq!(eval("Private", 6), (Status::UpToDate, None, 0, 4));
        assert_eq!(eval("Corporal", 3), (Status::UpToDate, None, 0, 2));
        assert_eq!(eval("", 0), (Status::BelowFirstRank, None, 0, 1));
    }

    #[test]
    fn nco_e_oficial_ficam_fora_do_calculo() {
        assert_eq!(eval("Captain", 99).0, Status::OutsideLadder);
        assert_eq!(eval("Lance Serjeant", 99), (Status::UpToDate, None, 0, 0));
    }
}
