mod audit;
mod checkspreadsheetinfo;
mod dashboardlink;
mod deployment;
mod enlistuser;
mod logs;
pub mod massdm;
mod messages;
mod ping;
mod robloxenlist;
mod utility;

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
        utility::osinfo(),
        utility::vsfdliliane(),
        dashboardlink::dashboardlink(),
    ];
    commands.extend(audit::all());
    commands
}
