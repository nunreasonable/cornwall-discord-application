mod checkspreadsheetinfo;
mod enlistuser;
mod logs;
mod ping;
mod robloxenlist;

use crate::{Data, Error};

/// Um lugar so para a lista: comando fora daqui nao existe para o Discord.
pub fn all() -> Vec<poise::Command<Data, Error>> {
    vec![
        ping::ping(),
        logs::logs(),
        checkspreadsheetinfo::checkspreadsheetinfo(),
        enlistuser::enlistuser(),
        robloxenlist::alistar_se(),
    ]
}
