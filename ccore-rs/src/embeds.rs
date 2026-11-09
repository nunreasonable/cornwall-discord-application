use poise::serenity_prelude as serenity;

pub const ERROR_COLOUR: serenity::Colour = serenity::Colour::new(0xCD5C5C);

pub fn error(title: impl Into<String>, text: impl Into<String>) -> serenity::CreateEmbed {
    serenity::CreateEmbed::new()
        .title(title)
        .description(text)
        .colour(ERROR_COLOUR)
}

/// Corta por caractere, e nao por byte: cortar um acento ao meio daria panic.
pub fn truncate(text: &str, max: usize) -> String {
    if text.chars().count() <= max {
        return text.to_owned();
    }

    let mut cut: String = text.chars().take(max.saturating_sub(1)).collect();
    cut.push('…');
    cut
}

/// Deixa o texto seguro dentro de um bloco ```: a crase tripla fecharia o bloco
/// e o resto viraria markdown solto. O zero-width space separa as crases.
pub fn fence_safe(text: &str) -> String {
    text.replace("```", "`\u{200b}`\u{200b}`")
}
