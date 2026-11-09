use std::time::{Duration, Instant};

use poise::{CreateReply, serenity_prelude as serenity};
use serenity::Mentionable;

use crate::{
    Context, Error,
    embeds::{self, truncate},
    enlistment::{self, LogDetails, cooldown_left},
};

const COOLDOWN: Duration = Duration::from_secs(30);
const FORM_TIMEOUT: Duration = Duration::from_secs(600);

/// Respostas fechadas do formulario. O Serenity 0.12 nao tem os radios de modal
/// do Discord, entao elas vem de menus, antes do modal com os campos de texto.
#[derive(Default)]
struct Choices {
    language: Option<String>,
    groups: Option<String>,
    social: Option<String>,
}

impl Choices {
    fn is_complete(&self) -> bool {
        self.language.is_some() && self.groups.is_some() && self.social.is_some()
    }
}

/// Aliste-se usando verificação automática de conta ROBLOX + formulário.
#[poise::command(slash_command, rename = "alistar-se")]
pub async fn alistar_se(ctx: Context<'_>) -> Result<(), Error> {
    let config = &ctx.data().config;

    let Some(guild_id) = ctx.guild_id() else {
        return deny(
            ctx,
            "Comando inválido",
            "Este comando só pode ser executado em um servidor (guild).",
        )
        .await;
    };

    let Some(channel) = config.roblox_enlist_channel_id.filter(|&id| id != 0) else {
        return deny(
            ctx,
            "Configuração inválida",
            "O ID do canal de alistamento ROBLOX não está configurado. Verifique o arquivo config.json (robloxEnlistChannelId).",
        )
        .await;
    };

    if ctx.channel_id().get() != channel {
        return deny(
            ctx,
            "Canal incorreto",
            "Este comando só pode ser utilizado no canal de alistamento.",
        )
        .await;
    }

    if let Some(wait) = claim_attempt(ctx) {
        return deny(
            ctx,
            "Aguarde um momento",
            format!(
                "Você iniciou um alistamento há pouco. Tente de novo em {}s.",
                wait.as_secs() + 1
            ),
        )
        .await;
    }

    let Some(target) = ctx.author_member().await.map(|m| m.into_owned()) else {
        return deny(
            ctx,
            "Comando inválido",
            "Não consegui ler seus dados neste servidor.",
        )
        .await;
    };

    let blocked = target
        .roles
        .iter()
        .any(|role| config.roblox_enlist_blocked_role_ids.contains(&role.get()));
    if blocked {
        return deny(
            ctx,
            "Alistamento negado",
            "Este usuário já está alistado em algum outro regimento, favor redirecionar-se ao canal <#1397974742228533322> para solicitar a transferência.",
        )
        .await;
    }

    let prefix = ctx.id().to_string();
    let handle = ctx.send(form(&prefix)).await?;
    let sctx = ctx.serenity_context();

    let mut choices = Choices::default();
    let response = loop {
        let Some(press) = serenity::collector::ComponentInteractionCollector::new(ctx)
            .filter({
                let prefix = prefix.clone();
                move |press| press.data.custom_id.starts_with(&prefix)
            })
            .timeout(FORM_TIMEOUT)
            .await
        else {
            let _ = handle
                .edit(
                    ctx,
                    CreateReply::default()
                        .content("Formulário expirado. Rode `/alistar-se` de novo.")
                        .components(vec![]),
                )
                .await;
            return Ok(());
        };

        if let serenity::ComponentInteractionDataKind::StringSelect { values } = &press.data.kind {
            let value = values.first().cloned();
            match press.data.custom_id.rsplit(':').next() {
                Some("language") => choices.language = value,
                Some("groups") => choices.groups = value,
                Some("social") => choices.social = value,
                _ => {}
            }
            press
                .create_response(ctx, serenity::CreateInteractionResponse::Acknowledge)
                .await?;
            continue;
        }

        if !choices.is_complete() {
            press
                .create_response(
                    ctx,
                    serenity::CreateInteractionResponse::Message(
                        serenity::CreateInteractionResponseMessage::new()
                            .content("Responda as três perguntas antes de continuar.")
                            .ephemeral(true),
                    ),
                )
                .await?;
            continue;
        }

        let modal = serenity::CreateQuickModal::new("Alistamento — 12° Regiment")
            .timeout(FORM_TIMEOUT)
            .field(
                serenity::CreateInputText::new(
                    serenity::InputTextStyle::Short,
                    "Nome no Roblox",
                    "enlist_roblox",
                )
                .placeholder("Exatamente como aparece. Ex.: RafaOdebrecht")
                .min_length(1)
                .max_length(20),
            )
            .field(
                serenity::CreateInputText::new(
                    serenity::InputTextStyle::Short,
                    "Quem te recrutou?",
                    "enlist_recruiter",
                )
                .placeholder("Opcional — deixe vazio se não souber")
                .max_length(60)
                .required(false),
            );

        match press.quick_modal(sctx, modal).await? {
            Some(response) => break response,
            None => return Ok(()),
        }
    };

    let _ = handle
        .edit(
            ctx,
            CreateReply::default()
                .content("Formulário enviado.")
                .components(vec![]),
        )
        .await;

    let interaction = response.interaction;
    interaction
        .create_response(
            ctx,
            serenity::CreateInteractionResponse::Defer(
                serenity::CreateInteractionResponseMessage::new().ephemeral(true),
            ),
        )
        .await?;

    let reply = |embed: serenity::CreateEmbed| {
        let interaction = interaction.clone();
        async move {
            interaction
                .edit_response(ctx, serenity::EditInteractionResponse::new().embed(embed))
                .await
                .map(|_| ())
        }
    };

    let roblox_name = response.inputs[0].trim().to_owned();
    if roblox_name.is_empty() {
        reply(embeds::error(
            "Nome não informado",
            "O nome no Roblox é obrigatório. Rode `/alistar-se` de novo.",
        ))
        .await?;
        return Ok(());
    }
    let recruiter = response.inputs[1].trim().to_owned();

    let language = if choices.language.as_deref() == Some("br") {
        "Brasileiro 🇧🇷"
    } else {
        "Português 🇵🇹"
    };
    let groups = if choices.groups.as_deref() == Some("sim") {
        "Sim"
    } else {
        "Não"
    };
    let social_role = choices.social.as_deref() == Some("sim");

    let check = match enlistment::verify_roblox(&ctx.data().http, &roblox_name).await {
        Ok(check) => check,
        Err(cause) => {
            reply(embeds::error("Erro ao consultar ROBLOX", cause)).await?;
            return Ok(());
        }
    };

    if !check.is_likely_main() {
        reply(check.alt_embed()).await?;
        return Ok(());
    }

    let applied = enlistment::apply(
        sctx,
        guild_id,
        &target,
        config,
        social_role,
        ctx.channel_id(),
    )
    .await;

    let log = enlistment::log_embed(
        sctx,
        &target,
        &check,
        &applied,
        LogDetails {
            executor: ctx.author().mention().to_string(),
            roblox_name: &roblox_name,
            social_role,
            title: "12° Regiment - Recruit Log (ROBLOX)",
            description: "Registro de alistamento realizado com verificação automática de conta ROBLOX.",
            extra_fields: vec![
                ("Idioma", language.to_owned()),
                ("Pertence a outros grupos?", groups.to_owned()),
                ("Quem recrutou?", recruiter),
            ],
        },
    );

    let mut warnings = applied.warnings;
    warnings.extend(enlistment::announce(sctx, guild_id, config, log, &target).await);

    let mut embed = serenity::CreateEmbed::new()
        .title(if warnings.is_empty() {
            "Alistamento concluido"
        } else {
            "Alistamento concluido com avisos"
        })
        .description("Verificacao ROBLOX aprovada. Bem-vindo ao 12°.")
        .colour(if warnings.is_empty() {
            serenity::Colour::DARK_GREEN
        } else {
            serenity::Colour::ORANGE
        });

    if !warnings.is_empty() {
        embed = embed.field("Avisos", truncate(&warnings.join("\n"), 1024), false);
    }

    reply(embed).await?;
    Ok(())
}

/// Sem isto, repetir o comando em laco martelaria a API do Roblox.
fn claim_attempt(ctx: Context<'_>) -> Option<Duration> {
    let now = Instant::now();
    let user = ctx.author().id.get();
    let mut attempts = ctx
        .data()
        .enlist_attempts
        .lock()
        .unwrap_or_else(|e| e.into_inner());

    if let Some(wait) = attempts
        .get(&user)
        .and_then(|&last| cooldown_left(now - last, COOLDOWN))
    {
        return Some(wait);
    }

    attempts.retain(|_, &mut last| now - last < COOLDOWN);
    attempts.insert(user, now);
    None
}

fn form(prefix: &str) -> CreateReply {
    let select = |name: &str, placeholder: &str, options: &[(&str, &str)]| {
        let options = options
            .iter()
            .map(|(label, value)| serenity::CreateSelectMenuOption::new(*label, *value))
            .collect();

        serenity::CreateActionRow::SelectMenu(
            serenity::CreateSelectMenu::new(
                format!("{prefix}:{name}"),
                serenity::CreateSelectMenuKind::String { options },
            )
            .placeholder(placeholder),
        )
    };

    let yes_no = [("Sim", "sim"), ("Não", "nao")];

    CreateReply::default()
        .content("**Alistamento — 12° Regiment**\nResponda abaixo e clique em Continuar.")
        .ephemeral(true)
        .components(vec![
            select(
                "language",
                "Nacionalidade",
                &[("Português 🇵🇹", "pt"), ("Brasileiro 🇧🇷", "br")],
            ),
            select("groups", "Pertence a outros grupos ou regimentos?", &yes_no),
            select("social", "Quer o cargo social?", &yes_no),
            serenity::CreateActionRow::Buttons(vec![
                serenity::CreateButton::new(format!("{prefix}:next"))
                    .label("Continuar")
                    .style(serenity::ButtonStyle::Primary),
            ]),
        ])
}

async fn deny(ctx: Context<'_>, title: &str, text: impl Into<String>) -> Result<(), Error> {
    ctx.send(
        CreateReply::default()
            .embed(embeds::error(title, text))
            .ephemeral(true),
    )
    .await?;
    Ok(())
}
