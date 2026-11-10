use poise::{CreateReply, serenity_prelude as serenity};
use serenity::Mentionable;

use crate::{
    Context, Error, embeds,
    enlistment::{self, LogDetails},
    staff,
};

/// Alista um usuário (verificação ROBLOX automática + cargos + nickname + log).
#[poise::command(slash_command)]
pub async fn enlistuser(
    ctx: Context<'_>,
    #[description = "Usuário a ser alistado"] user: serenity::User,
    #[description = "Username do ROBLOX do usuário"] roblox_username: String,
    #[description = "Adicionar cargo social?"] socialrole: Option<bool>,
) -> Result<(), Error> {
    ctx.defer().await?;

    let config = &ctx.data().config;
    let social_role = socialrole.unwrap_or(false);

    let Some(guild_id) = ctx.guild_id() else {
        return fail(
            ctx,
            "Comando inválido",
            "Este comando só pode ser executado em um servidor (guild).",
        )
        .await;
    };

    if let Some(denied) = staff::require_enlist_role(ctx).await {
        ctx.send(CreateReply::default().embed(denied)).await?;
        return Ok(());
    }

    let Ok(target) = guild_id.member(ctx, user.id).await else {
        return fail(
            ctx,
            "Usuário não encontrado",
            "O usuário fornecido não é membro deste servidor.",
        )
        .await;
    };

    let roblox_name = roblox_username.trim();
    let check = match enlistment::verify_roblox(&ctx.data().http, roblox_name).await {
        Ok(check) => check,
        Err(cause) => return fail(ctx, "Erro ao consultar ROBLOX", cause).await,
    };

    if !check.is_likely_main() {
        ctx.send(CreateReply::default().embed(check.alt_embed()))
            .await?;
        return Ok(());
    }

    let sctx = ctx.serenity_context();
    let applied = enlistment::apply(
        sctx,
        guild_id,
        &target,
        config,
        social_role,
        Some(ctx.channel_id()),
    )
    .await;

    let log = enlistment::log_embed(
        sctx,
        &target,
        &check,
        &applied,
        LogDetails {
            executor: ctx.author().mention().to_string(),
            roblox_name,
            social_role,
            title: "12° Regiment - Recruit Log",
            description: "Registro de alistamento realizado com sucesso.",
            extra_fields: Vec::new(),
        },
    );

    let announce_warnings = enlistment::announce(sctx, guild_id, config, log, &target).await;

    // Quem alista precisa ver o que faltou mesmo tendo dado certo no geral.
    for warning in applied.warnings.iter().chain(&announce_warnings) {
        ctx.channel_id()
            .say(ctx, format!("⚠️ **Aviso**: {warning}"))
            .await?;
    }

    let description = if check.badges.is_some() {
        "Bem vindo ao 12°!"
    } else {
        "Bem vindo ao 12°! (Badges indisponíveis no momento.)"
    };

    let bot_avatar = sctx.cache.current_user().face();
    let embed = serenity::CreateEmbed::new()
        .title("12° - Usuário alistado com sucesso")
        .description(description)
        .colour(serenity::Colour::DARK_GREEN)
        .thumbnail(target.face())
        .footer(
            serenity::CreateEmbedFooter::new("Confirmação de alistamento ROBLOX")
                .icon_url(bot_avatar),
        )
        .timestamp(serenity::Timestamp::now());

    ctx.send(CreateReply::default().embed(embed)).await?;
    Ok(())
}

async fn fail(ctx: Context<'_>, title: &str, text: impl Into<String>) -> Result<(), Error> {
    ctx.send(CreateReply::default().embed(embeds::error(title, text)))
        .await?;
    Ok(())
}
