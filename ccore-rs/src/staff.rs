use poise::serenity_prelude as serenity;

use crate::{Context, embeds};

/// None quando pode usar; senao, o embed de recusa. O portao de staff e ter o
/// cargo em `enlistPermissionRoleId`.
pub async fn check(ctx: Context<'_>) -> Option<serenity::CreateEmbed> {
    let Some(member) = ctx.author_member().await else {
        return Some(embeds::error(
            "Comando indisponível",
            "Este comando só pode ser usado dentro do servidor.",
        ));
    };

    let Some(role_id) = ctx.data().config.enlist_permission_role_id else {
        return Some(embeds::error(
            "Configuração ausente",
            "`enlistPermissionRoleId` não está definido no config.jsonc.",
        ));
    };

    if member.roles.contains(&serenity::RoleId::new(role_id)) {
        return None;
    }

    Some(embeds::error(
        "Permissão negada",
        format!("Você precisa do cargo <@&{role_id}> para usar este comando."),
    ))
}
