use std::{
    collections::VecDeque,
    fmt::{Debug, Write},
    sync::{Arc, Mutex, MutexGuard},
};

use chrono::{DateTime, Local};
use tracing::{
    Event, Level, Subscriber,
    field::{Field, Visit},
};
use tracing_subscriber::layer::{Context, Layer};

const CAPACITY: usize = 1000;
const MAX_LINE_CHARS: usize = 500;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Severity {
    Info,
    Aviso,
    Erro,
}

impl Severity {
    pub fn name(self) -> &'static str {
        match self {
            Self::Info => "INFO",
            Self::Aviso => "AVISO",
            Self::Erro => "ERRO",
        }
    }
}

#[derive(Clone, Debug)]
pub struct LogLine {
    pub at: DateTime<Local>,
    pub severity: Severity,
    pub text: String,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Counts {
    pub info: usize,
    pub aviso: usize,
    pub erro: usize,
}

#[derive(Default)]
struct Inner {
    lines: VecDeque<LogLine>,
    total_seen: u64,
}

/// As ultimas linhas de log do processo, em memoria. O stdout continua
/// recebendo tudo; isto so permite ler o historico pelo Discord.
#[derive(Default)]
pub struct LogBuffer {
    inner: Mutex<Inner>,
}

impl LogBuffer {
    /// Um panic com o lock na mao "envenena" o Mutex. Log nao pode derrubar
    /// quem esta logando, entao seguimos com o conteudo que estiver la.
    fn lock(&self) -> MutexGuard<'_, Inner> {
        self.inner.lock().unwrap_or_else(|e| e.into_inner())
    }

    pub fn append(&self, severity: Severity, text: &str) {
        let text = text.trim_end();
        if text.is_empty() {
            return;
        }

        let line = LogLine {
            at: Local::now(),
            severity,
            text: truncate(text, MAX_LINE_CHARS),
        };

        let mut inner = self.lock();
        if inner.lines.len() >= CAPACITY {
            inner.lines.pop_front();
        }
        inner.lines.push_back(line);
        inner.total_seen += 1;
    }

    /// Da linha mais recente para a mais antiga.
    pub fn snapshot(
        &self,
        take: usize,
        severity: Option<Severity>,
        contains: Option<&str>,
    ) -> Vec<LogLine> {
        let needle = contains.map(str::to_lowercase);

        self.lock()
            .lines
            .iter()
            .rev()
            .filter(|line| severity.is_none_or(|s| line.severity == s))
            .filter(|line| {
                needle
                    .as_deref()
                    .is_none_or(|n| line.text.to_lowercase().contains(n))
            })
            .take(take)
            .cloned()
            .collect()
    }

    pub fn counts(&self) -> Counts {
        let mut counts = Counts::default();
        for line in &self.lock().lines {
            match line.severity {
                Severity::Info => counts.info += 1,
                Severity::Aviso => counts.aviso += 1,
                Severity::Erro => counts.erro += 1,
            }
        }
        counts
    }

    pub fn total_seen(&self) -> u64 {
        self.lock().total_seen
    }
}

/// Corta por caractere, e nao por byte: cortar um acento ao meio daria panic.
pub fn truncate(text: &str, max: usize) -> String {
    if text.chars().count() <= max {
        return text.to_owned();
    }

    let mut cut: String = text.chars().take(max.saturating_sub(1)).collect();
    cut.push('…');
    cut
}

/// Faz cada evento do `tracing` cair tambem no buffer. No C# isto era um tee
/// do console com palavras-chave para adivinhar o nivel; aqui o nivel e real.
pub struct BufferLayer {
    buffer: Arc<LogBuffer>,
}

impl BufferLayer {
    pub fn new(buffer: Arc<LogBuffer>) -> Self {
        Self { buffer }
    }
}

impl<S: Subscriber> Layer<S> for BufferLayer {
    fn on_event(&self, event: &Event<'_>, _ctx: Context<'_, S>) {
        let metadata = event.metadata();
        let severity = match *metadata.level() {
            Level::ERROR => Severity::Erro,
            Level::WARN => Severity::Aviso,
            _ => Severity::Info,
        };

        let mut visitor = FieldText::default();
        event.record(&mut visitor);

        // O que vem de dependencia leva a origem, como as tags [gw] do C#.
        let target = metadata.target();
        if target == "ccore" || target.starts_with("ccore::") {
            self.buffer.append(severity, &visitor.0);
        } else {
            self.buffer
                .append(severity, &format!("[{target}] {}", visitor.0));
        }
    }
}

#[derive(Default)]
struct FieldText(String);

impl Visit for FieldText {
    fn record_debug(&mut self, field: &Field, value: &dyn Debug) {
        let _ = if field.name() == "message" {
            write!(self.0, "{value:?}")
        } else {
            write!(self.0, " {}={value:?}", field.name())
        };
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn descarta_a_linha_mais_antiga_quando_enche() {
        let buffer = LogBuffer::default();
        for i in 0..CAPACITY + 1 {
            buffer.append(Severity::Info, &format!("linha {i}"));
        }

        let lines = buffer.snapshot(CAPACITY + 10, None, None);
        assert_eq!(lines.len(), CAPACITY);
        assert_eq!(lines[0].text, format!("linha {CAPACITY}"));
        assert_eq!(lines[CAPACITY - 1].text, "linha 1");
        assert_eq!(buffer.total_seen(), CAPACITY as u64 + 1);
    }

    #[test]
    fn filtra_por_nivel_e_texto_sem_diferenciar_caixa() {
        let buffer = LogBuffer::default();
        buffer.append(Severity::Info, "tudo CERTO");
        buffer.append(Severity::Erro, "deu ruim");
        buffer.append(Severity::Erro, "certo que falhou");

        let lines = buffer.snapshot(10, Some(Severity::Erro), Some("certo"));
        assert_eq!(lines.len(), 1);
        assert_eq!(lines[0].text, "certo que falhou");
        assert_eq!(
            buffer.counts(),
            Counts {
                info: 1,
                aviso: 0,
                erro: 2
            }
        );
    }

    #[test]
    fn corta_sem_quebrar_acento_e_ignora_linha_vazia() {
        assert_eq!(truncate("ação", 3), "aç…");
        assert_eq!(truncate("ação", 4), "ação");

        let buffer = LogBuffer::default();
        buffer.append(Severity::Info, "  \n");
        assert_eq!(buffer.total_seen(), 0);
    }
}
