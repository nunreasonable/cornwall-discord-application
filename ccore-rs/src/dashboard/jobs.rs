//! DM em massa pelo dashboard. Cada destinatario custa ~4,2s (limitador mais
//! espacamento), entao 500 levam ~35 minutos: o envio roda em segundo plano e
//! o painel acompanha pelo jobId.

use std::{
    collections::HashMap,
    sync::{
        Arc, Mutex, MutexGuard,
        atomic::{AtomicBool, Ordering},
    },
};

use chrono::{DateTime, Utc};
use poise::serenity_prelude as serenity;
use serde::Serialize;
use tracing::info;

use super::gate::job_id;
use crate::dm::{self, DmLimiter, SendResult};

/// /api/dm permite 3 disparos por minuto, de 500 cada: sem teto, horas de DM
/// se empilhavam atras do limitador sem forma de parar.
pub const MAX_CONCURRENT: usize = 2;
const RETENTION: chrono::Duration = chrono::Duration::hours(1);
const RECENT: usize = 10;

const SENDING: &str = "enviando";

/// O retrato que vai para a API. Nomes de campo do DmJobSnapshot do C#.
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Snapshot {
    pub job_id: String,
    pub state: &'static str,
    pub target_name: String,
    pub total: usize,
    pub sent: usize,
    pub failed: usize,
    pub started_at_utc: DateTime<Utc>,
    pub finished_at_utc: Option<DateTime<Utc>>,
    pub failure_samples: Vec<String>,
    pub error: Option<String>,
}

pub struct Job {
    cancel: AtomicBool,
    snapshot: Mutex<Snapshot>,
}

fn lock<T>(m: &Mutex<T>) -> MutexGuard<'_, T> {
    m.lock().unwrap_or_else(|e| e.into_inner())
}

impl Job {
    pub fn snapshot(&self) -> Snapshot {
        lock(&self.snapshot).clone()
    }

    fn progress(&self, sent: usize, failed: usize) {
        let mut s = lock(&self.snapshot);
        s.sent = sent;
        s.failed = failed;
    }

    fn finish(&self, result: SendResult) {
        let mut s = lock(&self.snapshot);
        s.sent = result.sent;
        s.failed = result.failed;
        s.failure_samples = result.samples;
        s.finished_at_utc = Some(Utc::now());
        if result.cancelled {
            info!("dm: job {} cancelado pelo operador", s.job_id);
            s.state = "cancelado";
            s.error = Some("Cancelado pelo operador.".to_owned());
        } else if let Some(reason) = result.aborted {
            info!("dm: job {} abortado: {reason}", s.job_id);
            s.state = "abortado";
            s.error = Some(reason);
        } else {
            s.state = "concluido";
        }
    }
}

#[derive(Default)]
pub struct Jobs {
    map: Mutex<HashMap<String, Arc<Job>>>,
}

impl Jobs {
    fn prune(map: &mut HashMap<String, Arc<Job>>) {
        let now = Utc::now();
        map.retain(|_, job| {
            lock(&job.snapshot)
                .finished_at_utc
                .is_none_or(|at| now - at < RETENTION)
        });
    }

    /// Reserva a vaga. Err e a frase pronta quando ja ha envios demais.
    pub fn register(&self, target: &str, total: usize) -> Result<Arc<Job>, String> {
        let mut map = lock(&self.map);
        Self::prune(&mut map);
        let active = map
            .values()
            .filter(|j| lock(&j.snapshot).state == SENDING)
            .count();
        if active >= MAX_CONCURRENT {
            return Err(format!(
                "Já há {active} envio(s) em andamento (limite {MAX_CONCURRENT}). Aguarde ou cancele antes de iniciar outro."
            ));
        }

        let job = Arc::new(Job {
            cancel: AtomicBool::new(false),
            snapshot: Mutex::new(Snapshot {
                job_id: job_id(),
                state: SENDING,
                target_name: target.to_owned(),
                total,
                sent: 0,
                failed: 0,
                started_at_utc: Utc::now(),
                finished_at_utc: None,
                failure_samples: Vec::new(),
                error: None,
            }),
        });
        map.insert(job.snapshot().job_id, job.clone());
        Ok(job)
    }

    /// Os destinatarios ja vem resolvidos: alvo invalido falha no request, e
    /// nao vira um job que so quebra depois.
    pub fn start(
        &self,
        ctx: serenity::Context,
        limiter: Arc<DmLimiter>,
        recipients: dm::Recipients,
        message: serenity::CreateMessage,
    ) -> Result<Snapshot, String> {
        let job = self.register(&recipients.name, recipients.members.len())?;
        let snapshot = job.snapshot();
        tokio::spawn(async move {
            let result = dm::send_tracked(
                &ctx,
                &limiter,
                &recipients.members,
                &message,
                &job.cancel,
                |sent, failed| job.progress(sent, failed),
            )
            .await;
            job.finish(result);
        });
        Ok(snapshot)
    }

    /// False quando o job nao existe ou ja terminou.
    pub fn cancel(&self, id: &str) -> bool {
        let Some(job) = lock(&self.map).get(id).cloned() else {
            return false;
        };
        if lock(&job.snapshot).finished_at_utc.is_some() {
            return false;
        }
        job.cancel.store(true, Ordering::Relaxed);
        true
    }

    pub fn get(&self, id: &str) -> Option<Snapshot> {
        let mut map = lock(&self.map);
        Self::prune(&mut map);
        map.get(id).map(|j| j.snapshot())
    }

    /// Do mais novo ao mais velho.
    pub fn recent(&self) -> Vec<Snapshot> {
        let mut map = lock(&self.map);
        Self::prune(&mut map);
        let mut jobs: Vec<Snapshot> = map.values().map(|j| j.snapshot()).collect();
        jobs.sort_by_key(|j| std::cmp::Reverse(j.started_at_utc));
        jobs.truncate(RECENT);
        jobs
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn done(jobs: &Jobs, id: &str, ago: chrono::Duration) {
        let map = lock(&jobs.map);
        let mut s = lock(&map[id].snapshot);
        s.state = "concluido";
        s.finished_at_utc = Some(Utc::now() - ago);
    }

    #[test]
    fn no_maximo_dois_enviando() {
        let jobs = Jobs::default();
        let a = jobs.register("A", 3).unwrap().snapshot().job_id;
        jobs.register("B", 3).unwrap();
        let refused = jobs.register("C", 3).err().unwrap();
        assert!(refused.contains("Já há 2 envio(s) em andamento (limite 2)"));

        done(&jobs, &a, chrono::Duration::zero());
        assert!(jobs.register("C", 3).is_ok());
        assert_eq!(jobs.recent().len(), 3);
    }

    #[test]
    fn cancelar_so_vale_para_job_em_andamento() {
        let jobs = Jobs::default();
        let id = jobs.register("A", 1).unwrap().snapshot().job_id;
        assert!(!jobs.cancel("nao-existe"));
        assert!(jobs.cancel(&id));
        done(&jobs, &id, chrono::Duration::zero());
        assert!(!jobs.cancel(&id));
    }

    #[test]
    fn job_terminado_ha_mais_de_uma_hora_some() {
        let jobs = Jobs::default();
        let id = jobs.register("A", 1).unwrap().snapshot().job_id;
        done(&jobs, &id, chrono::Duration::minutes(61));
        assert!(jobs.get(&id).is_none());
    }

    #[test]
    fn retrato_tem_os_campos_do_csharp() {
        let jobs = Jobs::default();
        let job = jobs.register("Cargo", 2).unwrap();
        job.progress(1, 1);
        let json = serde_json::to_value(job.snapshot()).unwrap();
        let keys: Vec<&str> = json
            .as_object()
            .unwrap()
            .keys()
            .map(String::as_str)
            .collect();
        for key in [
            "jobId",
            "state",
            "targetName",
            "total",
            "sent",
            "failed",
            "startedAtUtc",
            "finishedAtUtc",
            "failureSamples",
            "error",
        ] {
            assert!(keys.contains(&key), "{key}");
        }
        assert_eq!(json["state"], "enviando");
        assert_eq!(json["sent"], 1);
        assert!(json["finishedAtUtc"].is_null() && json["error"].is_null());

        job.finish(SendResult {
            total: 2,
            sent: 1,
            failed: 1,
            samples: vec!["x (y)".to_owned()],
            aborted: None,
            cancelled: true,
        });
        let json = serde_json::to_value(job.snapshot()).unwrap();
        assert_eq!(json["state"], "cancelado");
        assert_eq!(json["error"], "Cancelado pelo operador.");
    }
}
