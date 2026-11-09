/// Le o CSV exportado de uma planilha do Google. Campos entre aspas podem ter
/// quebra de linha; `flexible` porque as linhas nao tem todas o mesmo numero de
/// colunas. Linhas em branco sao puladas.
pub fn parse_csv(text: &str) -> Result<Vec<Vec<String>>, String> {
    csv::ReaderBuilder::new()
        .has_headers(false)
        .flexible(true)
        .from_reader(text.as_bytes())
        .records()
        .map(|record| {
            record
                .map(|r| r.iter().map(str::to_owned).collect())
                .map_err(|e| format!("CSV inválido: {e}"))
        })
        .collect()
}
