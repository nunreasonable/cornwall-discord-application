//! Auditoria de batalhas do regimento: cada /audit-add vira um lote pendente,
//! e o /audit-push consolida os lotes no audit.json e publica no GitHub. A
//! planilha e a fonte da verdade, e o sync traz as mudancas dela de volta.

pub mod embeds;
pub mod github;
pub mod import;
pub mod ladder;
pub mod model;
pub mod parse;
pub mod publish;
pub mod sheet;
pub mod store;
pub mod sync;
