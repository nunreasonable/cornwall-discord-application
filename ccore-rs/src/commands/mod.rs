mod audit;
mod checkspreadsheetinfo;
mod deployment;
mod enlistuser;
mod logs;
mod massdm;
mod messages;
mod ping;
mod robloxenlist;

use crate::{Data, Error};

/// Um lugar so para a lista: comando fora daqui nao existe para o Discord.
pub fn all() -> Vec<poise::Command<Data, Error>> {
    let mut commands = vec![
        ping::ping(),
        logs::logs(),
        checkspreadsheetinfo::checkspreadsheetinfo(),
        enlistuser::enlistuser(),
        robloxenlist::alistar_se(),
        massdm::dmreminder(),
        massdm::dmdeployment(),
        deployment::deployment(),
        messages::repost(),
        messages::messages(),
    ];
    commands.extend(audit::all());
    commands
}
