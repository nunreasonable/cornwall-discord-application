use std::{
    net::{IpAddr, Ipv4Addr, Ipv6Addr, SocketAddr},
    time::Duration,
};

use reqwest::{
    Url,
    dns::{Addrs, Name, Resolve, Resolving},
    redirect,
};

const LONG_TIMEOUT: Duration = Duration::from_secs(60);

pub fn client() -> Result<reqwest::Client, reqwest::Error> {
    reqwest::Client::builder()
        .user_agent("ccore-bot")
        .dns_resolver(std::sync::Arc::new(SafeResolver))
        .redirect(redirect::Policy::custom(|attempt| {
            if attempt.previous().len() >= 10 {
                attempt.error("redirecionamentos demais")
            } else if is_blocked_url(attempt.url()) {
                attempt.error("redirecionamento para endereco interno")
            } else {
                attempt.follow()
            }
        }))
        .connect_timeout(Duration::from_secs(5))
        .timeout(Duration::from_secs(20))
        .build()
}

/// Para downloads que nao cabem nos 20s do cliente, como o CSV da planilha.
pub async fn fetch_text(client: &reqwest::Client, url: &str) -> Result<String, String> {
    let parsed = Url::parse(url).map_err(|e| format!("URL inválida: {e}"))?;
    if is_blocked_url(&parsed) {
        return Err("A URL aponta para um endereço interno.".to_owned());
    }

    let fetch = async {
        client
            .get(parsed)
            .timeout(LONG_TIMEOUT)
            .send()
            .await?
            .error_for_status()?
            .text()
            .await
    };

    // without_url: a mensagem vai para o Discord, e a URL da planilha nao.
    fetch.await.map_err(|e| e.without_url().to_string())
}

/// O DNS dos hosts do Google anuncia IPv6 que esta rede nao roteia: IPv4 vem
/// primeiro. E nada de endereco interno, para um redirect ou config adulterado
/// nao levar o bot a 127.0.0.1 ou ao metadata de nuvem.
struct SafeResolver;

impl Resolve for SafeResolver {
    fn resolve(&self, name: Name) -> Resolving {
        Box::pin(async move {
            let found = tokio::net::lookup_host((name.as_str(), 0)).await?;
            let mut allowed: Vec<SocketAddr> =
                found.filter(|addr| !is_blocked(addr.ip())).collect();

            if allowed.is_empty() {
                return Err(format!("{} nao tem endereco permitido", name.as_str()).into());
            }

            allowed.sort_by_key(SocketAddr::is_ipv6);
            let addrs: Addrs = Box::new(allowed.into_iter());
            Ok(addrs)
        })
    }
}

/// O resolver nao ve URL com IP literal, entao esse caso e conferido aqui.
fn is_blocked_url(url: &Url) -> bool {
    match url.host() {
        Some(url::Host::Ipv4(ip)) => is_blocked(IpAddr::V4(ip)),
        Some(url::Host::Ipv6(ip)) => is_blocked(IpAddr::V6(ip)),
        _ => false,
    }
}

fn is_blocked(ip: IpAddr) -> bool {
    match ip {
        IpAddr::V4(ip) => is_blocked_v4(ip),
        IpAddr::V6(ip) => is_blocked_v6(ip),
    }
}

fn is_blocked_v4(ip: Ipv4Addr) -> bool {
    let [a, b, ..] = ip.octets();
    let cgnat = a == 100 && (64..=127).contains(&b);

    ip.is_loopback()
        || ip.is_private()
        || ip.is_link_local()
        || ip.is_unspecified()
        || ip.is_broadcast()
        || cgnat
}

fn is_blocked_v6(ip: Ipv6Addr) -> bool {
    if let Some(mapped) = ip.to_ipv4_mapped() {
        return is_blocked_v4(mapped);
    }

    let site_local = (ip.segments()[0] & 0xffc0) == 0xfec0;

    ip.is_loopback()
        || ip.is_unspecified()
        || ip.is_unique_local()
        || ip.is_unicast_link_local()
        || site_local
}

#[cfg(test)]
mod tests {
    use super::*;

    fn blocked(text: &str) -> bool {
        is_blocked(text.parse().unwrap())
    }

    #[test]
    fn recusa_enderecos_internos() {
        for ip in [
            "127.0.0.1",
            "10.1.2.3",
            "172.16.0.1",
            "192.168.0.1",
            "169.254.169.254",
            "100.64.0.1",
            "0.0.0.0",
            "::1",
            "fc00::1",
            "fe80::1",
            "::ffff:192.168.0.1",
        ] {
            assert!(blocked(ip), "{ip} deveria ser recusado");
        }
    }

    #[test]
    fn aceita_enderecos_publicos() {
        for ip in ["8.8.8.8", "172.32.0.1", "2606:4700::1111"] {
            assert!(!blocked(ip), "{ip} deveria passar");
        }
    }

    #[test]
    fn recusa_url_com_ip_literal_interno() {
        assert!(is_blocked_url(
            &Url::parse("http://169.254.169.254/x").unwrap()
        ));
        assert!(!is_blocked_url(
            &Url::parse("https://docs.google.com/x").unwrap()
        ));
    }
}
