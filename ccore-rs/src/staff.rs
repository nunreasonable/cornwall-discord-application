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

/// O portao dos comandos de alistamento e de DM, com as frases deles.
pub async fn require_enlist_role(ctx: Context<'_>) -> Option<serenity::CreateEmbed> {
    let Some(required) = ctx.data().config.enlist_permission_role_id else {
        return Some(embeds::error(
            "Configuração inválida",
            "O ID do cargo com permissão para usar este comando não está configurado. Verifique o arquivo config.json (enlistPermissionRoleId).",
        ));
    };

    let allowed = ctx
        .author_member()
        .await
        .is_some_and(|m| m.roles.contains(&serenity::RoleId::new(required)));

    (!allowed).then(|| {
        embeds::error(
            "Permissão negada",
            "Você não possui o cargo necessário para usar este comando.",
        )
    })
}
