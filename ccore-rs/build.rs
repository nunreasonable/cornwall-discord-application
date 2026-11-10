//! Conta as linhas de cada linguagem do bot para o -osinfo, e anota a versao
//! do compilador. Roda so na compilacao: o binario nao le o codigo-fonte.

use std::{fs, path::Path, process::Command};

fn lines_in(dir: &Path, ext: &str) -> usize {
    let Ok(entries) = fs::read_dir(dir) else {
        return 0;
    };
    entries
        .flatten()
        .map(|entry| {
            let path = entry.path();
            if path.is_dir() {
                lines_in(&path, ext)
            } else if path.extension().is_some_and(|e| e == ext) {
                fs::read_to_string(&path).map_or(0, |text| text.lines().count())
            } else {
                0
            }
        })
        .sum()
}

fn main() {
    // O script de espera do systemd faz parte do bot que roda.
    let shell = Path::new("../wait-for-discord.sh");
    println!("cargo:rerun-if-changed=src");
    println!("cargo:rerun-if-changed={}", shell.display());

    let rust = lines_in(Path::new("src"), "rs");
    let shell = fs::read_to_string(shell).map_or(0, |text| text.lines().count());

    let rustc = std::env::var("RUSTC").unwrap_or_else(|_| "rustc".to_owned());
    let version = Command::new(rustc)
        .arg("-V")
        .output()
        .ok()
        .and_then(|out| String::from_utf8(out.stdout).ok())
        .map_or_else(
            || "rustc (versão desconhecida)".to_owned(),
            |v| v.trim().to_owned(),
        );

    println!("cargo:rustc-env=CCORE_RUST_LINES={rust}");
    println!("cargo:rustc-env=CCORE_SHELL_LINES={shell}");
    println!("cargo:rustc-env=CCORE_RUSTC={version}");
}
