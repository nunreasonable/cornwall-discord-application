use std::collections::HashSet;

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

    let channel = match resolve_channel(ctx, guild_id).await {
        Ok(channel) => channel,
        Err(cause) => return fail(ctx, "Configuração inválida", &cause).await,
    };

    let (roles, resenha) = {
        let Some(guild) = ctx.guild() else {
            return fail(ctx, "Erro", "O servidor não está no cache do bot.").await;
        };
        let roles: HashSet<u64> = guild.roles.keys().map(|id| id.get()).collect();
        let resenha = guild
            .emojis
            .values()
            .find(|e| e.name.eq_ignore_ascii_case("Resenha"))
            .map(ToString::to_string);
        (roles, resenha)
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

/// Confere o canal e as permissoes do bot antes de tentar: troca um erro
/// generico da API por qual permissao falta.
async fn resolve_channel(
    ctx: Context<'_>,
    guild_id: serenity::GuildId,
) -> Result<serenity::GuildChannel, String> {
    let Some(id) = ctx
        .data()
        .config
        .deployment_channel_id
        .filter(|&id| id != 0)
    else {
        return Err(
            "O ID do canal para envio de deployments não está configurado (deploymentChannelId)."
                .to_owned(),
        );
    };

    let not_found = || {
        format!("O canal configurado para deployments não foi encontrado neste servidor. ID: {id}")
    };
    let channel = serenity::ChannelId::new(id)
        .to_channel(ctx)
        .await
        .ok()
        .and_then(|c| c.guild())
        .filter(|c| c.guild_id == guild_id)
        .ok_or_else(not_found)?;

    // A referencia do cache segura um lock: o id sai dela antes do await.
    let bot_id = ctx.cache().current_user().id;
    let bot = guild_id.member(ctx, bot_id).await.map_err(|_| {
        "Não foi possível obter as informações do membro do bot no servidor.".to_owned()
    })?;

    let permissions = ctx
        .guild()
        .map(|guild| guild.user_permissions_in(&channel, &bot))
        .ok_or("O servidor não está no cache do bot.")?;

    if !permissions.send_messages() {
        return Err(format!(
            "O bot não tem permissão para enviar mensagens no canal {}.",
            channel.name
        ));
    }
    if !permissions.embed_links() {
        return Err(format!(
            "O bot não tem permissão para enviar embeds no canal {}.",
            channel.name
        ));
    }
    Ok(channel)
}

async fn fail(ctx: Context<'_>, title: &str, text: &str) -> Result<(), Error> {
    ctx.send(CreateReply::default().embed(embeds::error(title, text)))
        .await?;
    Ok(())
}
