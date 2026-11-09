use poise::serenity_prelude as serenity;

use super::{reply, store_error};
use crate::{
    Context, Error,
    audit::{
        embeds::{Draft, field_value},
        import,
        model::merge_import,
        store::Action,
    },
    embeds, staff,
};

/// Importa a auditoria atual da planilha para o arquivo local
#[poise::command(slash_command, rename = "audit-import")]
pub async fn audit_import(
    ctx: Context<'_>,
    // Padrao true de proposito: isto semeia o arquivo com o historico inteiro
    // do regimento, entao gravar precisa ser explicito.
    #[description = "Só mostra o que seria importado (padrão)"] dry_run: Option<bool>,
    #[description = "Sobrescreve os dados de quem já existe pelos da planilha (perigoso)"]
    sobrescrever: Option<bool>,
) -> Result<(), Error> {
    ctx.defer_ephemeral().await?;
    let dry_run = dry_run.unwrap_or(true);
    let overwrite = sobrescrever.unwrap_or(false);

    if let Some(denied) = staff::check(ctx).await {
        return reply(ctx, denied, true).await;
    }

    let Some(config) = ctx.data().config.audit.as_ref() else {
        let embed = embeds::error(
            "Configuração ausente",
            "A seção `audit` não existe no config.jsonc.",
        );
        return reply(ctx, embed, true).await;
    };

    let csv = match import::fetch(&ctx.data().http, config).await {
        Ok(csv) => csv,
        Err(cause) => return reply(ctx, embeds::error("Falha na importação", cause), true).await,
    };

    let report = if dry_run {
        // Simula numa copia: nada e gravado.
        ctx.data()
            .audit
            .read_audit()
            .await
            .map(|mut audit| merge_import(&mut audit, &csv.rows, overwrite, csv.has_kda))
    } else {
        ctx.data()
            .audit
            .update(|audit, _| merge_import(audit, &csv.rows, overwrite, csv.has_kda))
            .await
    };
    let mut report = match report {
        Ok(report) => report,
        Err(cause) => return reply(ctx, store_error(cause), true).await,
    };
    report.skipped_rows += csv.skipped_rows;

    // Dry run nao muda nada, entao nao entra no historico.
    if !dry_run {
        let suffix = if overwrite { " (com sobrescrita)" } else { "" };
        ctx.data()
            .audit
            .record(
                ctx.author(),
                Action::Import,
                &format!(
                    "Importou a planilha: {} adicionado(s), {} sobrescrito(s), {} linha(s) ignorada(s){suffix}.",
                    report.added.len(),
                    report.overwritten,
                    report.skipped_rows
                ),
            )
            .await;
    }

    let (title, description, colour) = if dry_run {
        (
            "Prévia da importação (dry run)",
            "Nada foi gravado. Rode com `dry_run: false` para aplicar.",
            serenity::Colour::BLURPLE,
        )
    } else {
        (
            "Importação concluída",
            "O arquivo local foi atualizado. Publique com `/audit-push`.",
            serenity::Colour::DARK_GREEN,
        )
    };

    let mut embed = Draft::new(title, description, colour)
        .field("Linhas lidas da planilha", csv.rows.len().to_string(), true)
        .field("Adicionados", report.added.len().to_string(), true)
        .field("Linhas ignoradas", report.skipped_rows.to_string(), true);

    if !report.added.is_empty() {
        embed = embed.field(
            "Novos jogadores",
            field_value(report.added.iter().take(20)),
            false,
        );
    }
    if overwrite {
        embed = embed.field("Sobrescritos", report.overwritten.to_string(), true);
    } else if !report.conflicts.is_empty() {
        embed = embed.field(
            format!(
                "Já existentes com números diferentes ({})",
                report.conflicts.len()
            ),
            field_value(report.conflicts.iter().take(15)),
            false,
        );
    }

    let note = if csv.has_kda {
        "A importação traz nome, **cargo**, **batalhas** e K/D/A da planilha. Jogadores que já existem no arquivo não são \
         alterados: o `audit.json` passa a ser a fonte da verdade e já soma a planilha **mais** tudo que foi consolidado \
         depois, então sobrescrever faria os totais voltarem atrás."
    } else {
        "A planilha atual traz apenas **nome**, **patente** e **batalhas** — kills, deaths e assists não existem lá e são \
         acumulados só pelo `/audit-add`, então a importação não toca neles. Jogadores que já existem no arquivo não são \
         alterados: o `audit.json` é a fonte da verdade e já inclui tudo que foi consolidado depois da planilha."
    };
    embed = embed.field("Observação", note, false);

    reply(ctx, embed.build(), true).await
}
