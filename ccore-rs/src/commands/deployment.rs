use poise::{CreateReply, serenity_prelude as serenity};
use serenity::Mentionable;

use crate::{Context, Error, deployment, embeds};

/// Envia uma mensagem de deployment com embed, roles ping e botões.
#[poise::command(slash_command)]
pub async fn deployment(
    ctx: Context<'_>,
    #[description = "Código para acesso ao jogo"] codigo: String,
) -> Result<(), Error> {
    ctx.defer().await?;
    let config = &ctx.data().config;

    let Some(guild_id) = ctx.guild_id() else {
        return fail(
            ctx,
            "Erro",
            "Este comando só pode ser usado em servidores (guilds).",
        )
        .await;
    };

    // Portao proprio, e nao o cargo de staff dos outros comandos.
    let allowed = match ctx.author_member().await {
        Some(member) => member
            .roles
            .iter()
            .any(|r| config.deployment_allowed_role_ids.contains(&r.get())),
        None => false,
    };
    if !allowed {
        return fail(
            ctx,
            "Permissão negada",
            "Você não possui um cargo com permissão para usar este comando. Verifique os deploymentAllowedRoleIds no config.json.",
        )
        .await;
    }

    let channel = match deployment::channel(ctx.serenity_context(), guild_id, config).await {
        Ok(channel) => channel,
        Err(cause) => return fail(ctx, "Configuração inválida", &cause).await,
    };

    let Some((roles, resenha)) = deployment::guild_assets(ctx.cache(), guild_id) else {
        return fail(ctx, "Erro", "O servidor não está no cache do bot.").await;
    };

    let built = deployment::build(config, &codigo, &roles, resenha.as_deref());

    if let Err(cause) = channel.id.send_message(ctx, built.message).await {
        return fail(
            ctx,
            "❌ Erro ao enviar mensagem",
            &format!("Ocorreu um erro ao enviar a mensagem: {cause}"),
        )
        .await;
    }

    let embed = serenity::CreateEmbed::new()
        .title("✅ Mensagem de deployment enviada!")
        .description(format!(
            "Título: {}\nCódigo: {codigo}\nCanal de voz: {}\nRoles pingados: {}\nCanal: {}",
            built.title,
            built.voice,
            built.roles_pinged,
            channel.mention()
        ))
        .colour(serenity::Colour::DARK_GREEN)
        .timestamp(serenity::Timestamp::now());
    ctx.send(CreateReply::default().embed(embed)).await?;
    Ok(())
}

async fn fail(ctx: Context<'_>, title: &str, text: &str) -> Result<(), Error> {
    ctx.send(CreateReply::default().embed(embeds::error(title, text)))
        .await?;
    Ok(())
}
