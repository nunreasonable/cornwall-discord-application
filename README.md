<p align="center">
  <a href="https://ccore.daeese.me/"><img src="https://daeese.me/filearchive/icons/ccore-badge.webp" width="140" alt="Brasão do 12° Regimento de Infantaria &quot;Chaves&quot;" /></a>
</p>

# ccore

Bot Discord para o 12° "Chaves" Regimento de Infantaria com funcionalidades de alistamento, deployments e mensagens.

[Vitrine](https://ccore.daeese.me/) · [Status](https://ccore.daeese.me/status/) · [Painel da staff](https://dashboard.daeese.me/) · [Termos](https://ccore.daeese.me/termsofservice/) · [Privacidade](https://ccore.daeese.me/privacypolicy/)

As páginas seguem o visual do regimento: o brasão do 12° num círculo branco, tema claro "livro de
ordens" (papel cinza, tinta e lacre) e escuro "casaca escarlate" (vinho e dourado), escolhido
pelo sistema ou pelo botão no canto. A vitrine mostra o caminho de um soldado pelo bot, do
alistamento à promoção; o status público mostra se o bot está no ar e os totais do efetivo; o
painel tem a escada de promoção de cada jogador.

Desde novembro de 2026 o ccore roda em **Rust**, em `ccore-rs/` (poise + serenity, tokio e axum).
A versão em C# / .NET que ficou em `ConsoleApp1/` foi a base do port e não roda mais em produção.

> **Status:** o ccore continua no ar, mas daqui pra frente recebe menos atualizações. Ele foi o
> pontapé inicial do [Sollarety](https://apps.daeese.me/sollarety/), que nasceu da mesma
> infraestrutura. Num futuro próximo ele pode virar uma "engine": um modelo próprio de bot / app
> do Discord pra construir qualquer aplicação.

## Comandos do Bot

### Alistamento & Verificação
- **`/enlistuser`** - Alista um usuário com verificação automática ROBLOX
  - Requer: Cargo de permissão configurado
  - Parâmetros: usuário, username ROBLOX
  - Função: Verificação anti-ALT + atribuição de cargos + nickname + log

- **`/alistar-se`** - Auto-alistamento com formulário em pop-up
  - Canal específico requerido
  - As cinco perguntas são respondidas de uma vez num modal; nacionalidade, "pertence a
    outros grupos?" e "cargo social?" são botões de escolha, então não existe resposta inválida
  - **Não usa DM** — quem está com a DM fechada também consegue se alistar
  - Função: formulário completo + verificação anti-ALT no ROBLOX + cargos + apelido + log

### Planilha & Informações
- **`/checkspreadsheetinfo`** - Consulta informações da planilha regimental
  - Parâmetro: username na planilha
  - Função: Busca dados na aba Roster da planilha

### Mensagens & Comunicações
- **`/deployment`** - Envia mensagem de deployment
  - Requer: Cargo de permissão configurado
  - Parâmetro: código do jogo
  - Função: Embed com roles ping + botões de acesso

- **`/dmdeployment`** - Envia DM para cargo/usuário (com código)
  - Requer: Cargo de permissão configurado
  - Parâmetros: cargo/usuário, código, mensagem
  - Limite: 500 membros por execução
  - Se o Discord sinalizar rate limit ou as falhas seguidas passarem do limite, o envio para e
    o resumo sai como **Envio interrompido**, com quantas mensagens foram antes do corte

- **`/dmreminder`** - Envia DM para cargo/usuário (sem código)
  - Requer: Cargo de permissão configurado
  - Parâmetros: cargo/usuário, mensagem
  - Limite: 500 membros por execução
  - Mesmo corte do `/dmdeployment`: se o envio for interrompido, o resumo mostra o parcial

### Sistema de Auditoria
> 📖 **Guia passo a passo para quem vai usar:** [`markdownspam/audit_usage.md`](markdownspam/audit_usage.md)
> — escrito em linguagem simples, sem exigir conhecimento técnico.

Registra kills, deaths, assists, batalhas e cargo de cada integrante, e guarda um histórico
de quem fez cada alteração (`/audit-logs`). Funciona em duas
etapas: primeiro as auditorias entram numa fila (`/audit-add`), depois são publicadas de
uma vez (`/audit-push`). Todos exigem o cargo de permissão do staff.

- **`/audit-add`** - Registra a auditoria de uma batalha
  - Abre uma janela para colar o bloco no formato `nome k d a n`
  - O `n` não tem valor: é a quebra de linha que o jogo escreve. Ele fecha cada jogador e
    começa o próximo, então o bloco pode vir todo numa linha só (`ana 1 2 3 n bia 4 5 6 n`) ou
    um jogador por linha. Quebra de linha de verdade depois de `k d a` também fecha o jogador,
    com aviso de que faltou o `n`; um número no lugar do `n` é recusado
  - Cada execução vale **+1 batalha** para cada jogador listado
  - Função: valida linha a linha, mostra prévia e guarda na fila (não publica)

- **`/audit-push`** - Publica a fila
  - Parâmetros opcionais: `dry_run` (simula sem gravar nem publicar) e `planilha` (padrão
    `true`; com `false` os lotes não vão para a planilha do regimento)
  - Função: consolida os totais, publica na branch de dados e arquiva cada lote por data
  - Lote publicado sem a planilha fica em `localOnlyBatches`, e a sincronização soma ele por
    cima da planilha em vez de perdê-lo
  - Sem lotes na fila, publica as mudanças feitas direto no arquivo (`/audit-import`,
    `/audit-edit`, `/audit-setranks`); se o GitHub já estiver igual, não cria commit
  - Em caso de erro a fila **não** é limpa; republicar não conta em dobro

- **`/audit-check`** - Consulta a auditoria
  - Parâmetros opcionais: `jogador` (com autocomplete), `ordenar`, `privado`
  - Função: tabela paginada do efetivo ou ficha individual com K/D

- **`/audit-edit`** - Corrige, renomeia ou remove um jogador
  - Parâmetros: `jogador`, `escopo` (consolidado ou pendente), `remover`
  - Função: janela pré-preenchida com os valores atuais e resumo antes/depois

- **`/audit-setranks`** - Define os cargos manualmente
  - Função: busca pelo nome, junta até 10 jogadores numa cesta e define os cargos de uma vez
  - O botão **🔍 Buscar** filtra o efetivo; buscar de novo **não** perde quem já está na cesta,
    então dá para juntar gente de partes diferentes da lista sem paginar
  - **Definir cargos** abre um bloco `nome = cargo`, uma linha por jogador, já preenchido com o
    cargo atual; cargo é texto livre e o lado direito vazio remove o cargo
  - Nome que não existe na auditoria é reportado de volta, não ignorado em silêncio

- **`/audit-import`** - Importa o efetivo atual da planilha
  - Parâmetros: `dry_run` (padrão `true`), `sobrescrever` (padrão `false`)
  - Função: semeia o sistema com nome, patente e batalhas da planilha; não altera quem já existe
  - A planilha **não** tem kills/deaths/assists: esses números vêm só do `/audit-add` e a
    importação nunca encosta neles

- **Sincronização com a planilha** - automática, sem comando
  - A cada `audit.sheetSyncMinutes` (padrão 5; `0` desliga) o bot baixa a aba da auditoria e
    deixa o `audit.json` igual a ela: entra quem é novo, sai quem sumiu, e patente, batalhas e
    K/D/A passam a ser os da planilha. É o que o dashboard e o `/audit-check` mostram
  - Desde que o `/audit-push` manda os lotes para a planilha, ela é a fonte da verdade. Por isso
    uma correção feita só no bot (`/audit-edit`, `/audit-setranks` ou o dashboard) volta ao
    valor da planilha na sincronização seguinte: corrija na planilha
  - A fila pendente não é tocada. Se a planilha vier vazia ou com menos da metade do efetivo,
    a sincronização é ignorada e vira aviso no log
  - Mudanças entram no `/audit-logs` como `IMPORT`, feitas por `planilha`

- **`/audit-logs`** - Mostra quem mexeu na auditoria e o que foi feito
  - Parâmetros opcionais: `usuario`, `acao`, `privado` (padrão `true`)
  - Função: histórico paginado, do mais recente para o mais antigo, com data, autor e detalhe
    de cada `/audit-add`, `/audit-edit`, `/audit-setranks`, `/audit-import` e `/audit-push`

- **`/promocoes`** - Mostra quem está elegível a promoção
  - Parâmetros opcionais: `jogador` (com autocomplete), `privado`
  - Função: aplica a escada de promoções do regimento sobre a auditoria e lista quem já pode
    subir, ou a ficha de um jogador só

### Sistema de Repost
- **`/messages`** - Verifica status do armazenamento de mensagens
  - Função: Estatísticas e progresso do sistema

- **`/repost`** - Republica mensagem aleatória armazenada
  - Requer: Mínimo de mensagens armazenadas
  - Cooldown: 2 horas após uso manual

### Utilitários
- **`/ping`** - Testa latência do bot
  - Função: Retorna tempo de resposta em ms

- **`/logs`** - Mostra as últimas linhas de log do bot
  - Requer: o mesmo cargo de staff dos comandos de auditoria
  - Parâmetros opcionais: `quantidade` (1–100, padrão 25), `nivel`
    (todos/erro/aviso/info), `filtro` (texto), `privado` (padrão `true`)
  - Função: histórico paginado, do mais recente para o mais antigo

  As linhas vêm de um **buffer em memória** (`src/log_buffer.rs`), uma camada do `tracing`
  que recebe cada evento de log além da saída normal. Sem ele, a única forma de ler essa saída
  seria ter acesso à máquina e rodar `journalctl --user -u ccore-bot`.

  A camada **não substitui** o stdout, ela duplica: o journald continua recebendo tudo, e a
  interface de terminal continua funcionando. O buffer guarda as últimas 1000 linhas e
  **zera a cada reinício do bot** — para histórico que sobrevive a restart, o journald
  continua sendo a fonte.

- **`/dashboardlink`** - Gera um código de login único para o painel
  - Requer: algum nível no `dashboard_auth.json`
  - Função: responde só para quem pediu, com o código que entra em
    [dashboard.daeese.me](https://dashboard.daeese.me/)

### Comandos de Administração
- **`-osinfo`** - Mostra informações do sistema, do processo, das linguagens do bot e da conexão
  com o Discord
  - Requer: cargo de staff (expõe dados da máquina)
  - Comando de prefixo (`-` por padrão, campo `prefix` do config), não de barra

- **`vsfdliliane`** - Comando especial restrito
  - Requer: ID de usuário específico
  - Função: Respostas personalizadas

---

## Configuração

O bot utiliza o arquivo `ccore-rs/config/config.jsonc` para configurações de:
- IDs de canais e cargos
- URLs de planilhas e links
- Permissões de comandos
- Mensagens personalizadas
- Lista de termos bloqueados e resposta automática do blacklist
- Bloco `audit`: destino no GitHub e mapeamento das colunas da planilha usadas pelo
  `/audit-import`. O campo `githubToken` pode ficar **vazio** — nesse caso o bot reaproveita
  o login do `gh` CLI da máquina (`gh auth login`), sem precisar criar nenhum token novo.
  A ordem de busca da credencial é: `githubToken` → `GH_TOKEN`/`GITHUB_TOKEN` → `gh auth token`
- `notifyUserIds` / `dmAlertCooldownMinutes`: quem recebe DM quando alguém cai na blacklist,
  e a janela de silêncio entre esses alertas (infrações dentro da janela viram uma DM única
  de resumo, para o bot não ser sinalizado como spam)

### Blacklist de mensagens

A lista é **curta de propósito**. Hoje são três termos:

```jsonc
"blacklistedTerms": ["traveco", "trany", "estuprei"],
```

Ela já teve 17, e a maioria era fala corriqueira do servidor — `desgraçado`, `filho da puta`,
`vai se fuder`. Regra que todo mundo ignora não modera nada: ensina a ignorar o bot. O critério
agora é **slur contra grupo** e **confissão de violência sexual**, e mais nada. O resto é
trabalho de moderador humano.

Dois cortes que valem explicação, porque parecem perdas e não são:

- **`assediar`, `assediado`, `estuprar`, `estuprado`, `estrup`** eram o vocabulário que a
  **vítima** usa para relatar. Com eles na lista, quem escrevesse "ele me assediou" recebia
  *"não vou hesitar em te punir"*. O bot punia quem denuncia. `estuprei` ficou por ser primeira
  pessoa — confissão, ou piada com ela, não relato.
- **`nazi`, `nazista`, `fascista`** e diminutivos são rótulo político, não slur. "isso é
  fascista" é crítica corrente, e apologia de verdade não cabe em casar palavra solta.

#### O match exige palavra inteira

`src/blacklist.rs` só aceita o termo quando ele aparece como palavra inteira (sem letra, número
ou `_` colado nas pontas), sobre uma forma normalizada (minúscula, sem acento).

Antes era `Contains` cru, e substring pega palavra de dentro de palavra: com `fascista` na
lista, quem escrevesse **`antifascista`** — o oposto do que a regra quer punir — levava a
punição. O mesmo valia para `antinazista`. A normalização fecha o outro buraco: o
`OrdinalIgnoreCase` antigo não mexia em acento, então `desgracado` sem cedilha passava direto
por `desgraçado`.

**Ao adicionar um termo, escreva a palavra completa.** Fragmento como `estrup`, que existia
para pegar `estrupador`, não funciona mais.

Isso não torna a lista à prova de quem *quer* burlar — separar com ponto, trocar letra por
número e por aí vai continua passando. A blacklist é lombada, não cerca.

> A lista viva fica no `config.jsonc`, que **não é versionado**. Se o bot roda em mais de uma
> máquina, cada uma tem a sua cópia e o corte precisa ser repetido à mão em todas.

## Interface de terminal

Além dos comandos do Discord, o bot aceita comandos em texto
(`src/terminal.rs`): `>channel ID` escolhe um canal, texto solto vira uma mensagem
nesse canal, `>help` lista tudo e `>exit` encerra a sessão.

Em produção o bot roda como serviço do systemd (`ccore-bot.service`), e serviço do systemd
recebe o stdin ligado em `/dev/null`. Por isso a interface **nunca** subia na máquina de
verdade: o bot logava `stdin nao interativo; interface de terminal desativada` e pronto.

Agora ela tem dois caminhos, com o mesmo interpretador atrás dos dois:

- **stdin**, quando o processo tem terminal de verdade (`cargo run` na mão);
- **socket Unix**, sempre — inclusive sob o systemd.

Para abrir uma sessão no bot que já está rodando, basta rodar o próprio binário em modo
cliente:

```bash
ccore-rs/target/release/ccore --terminal
```

O socket fica em `$XDG_RUNTIME_DIR/ccore-bot/terminal.sock` (modo `0600`, dentro de um
diretório `0700`), então só o dono do processo consegue falar com ele — nada é exposto na
rede. `CCORE_TERMINAL_SOCKET` troca esse caminho, se precisar. Várias sessões podem ficar
abertas ao mesmo tempo: cada uma tem o seu próprio canal atual, e o `>exit` fecha só a
sessão de quem digitou, nunca o bot.

## Dashboard e API HTTP

O bot expõe uma API em `http://127.0.0.1:5056` (`src/dashboard/`, com axum), consumida
pelo painel em [dashboard.daeese.me](https://dashboard.daeese.me/) e pela página pública de
status em [ccore.daeese.me/status](https://ccore.daeese.me/status/). Dá para entrar com o
Discord (OAuth2, escopo `identify`) ou com o código do `/dashboardlink`, que continua valendo.
O login com o Discord liga quando o `dashboard_auth.json` tem `discordClientSecret` (o client
secret do aplicativo do bot) e `discordRedirectUri` (a URL do painel, cadastrada em OAuth2 >
Redirects no portal). O painel manda a pessoa ao Discord, recebe o `code` de volta e o entrega
em `POST /api/auth/discord`; o secret nunca sai do bot. Os níveis vêm de `dashboard_auth.json`
(ver "Onde fica a fronteira"). `level2UserIds` ainda dá nível 2 por ID, mas só para quem tem o
12º Staff.

| Método | Rota | Nível | O que faz |
|---|---|---|---|
| GET | `/api/health` | público | ping da API |
| GET | `/api/status` | público | uptime, latência, servidores, membros, versão |
| GET | `/api/status?detail=host` | 2 | acrescenta SO, disco, carga e memória |
| GET | `/api/logs` | 2 | buffer de logs (`take`, `level`, `q`) |
| GET | `/api/audit` | 1 | log de auditoria do painel (quem usou o dashboard e o quê fez) |
| GET | `/api/audit/roster` | 1 | efetivo consolidado + fila pendente |
| GET | `/api/audit/history` | 1 | histórico da auditoria (o mesmo do `/audit-logs`) |
| POST | `/api/audit/entry` | **1 / 3** | edita e renomeia com 1; **remover** exige 3 |
| POST | `/api/audit/ranks` | 1 | define patentes |
| POST | `/api/audit/push` | 2 | publica no GitHub (aceita `dryRun`) |
| GET | `/api/promotions` | 1 | elegibilidade pela escada de promoções |
| POST | `/api/deployment` | 1 | envia a mensagem de deployment |
| POST | `/api/dm` | 2 | inicia DM em massa, devolve `jobId` |
| GET | `/api/dm/status` | 2 | progresso do job (`?jobId=`) |
| POST | `/api/enlist` | 2 | alista com verificação ROBLOX |
| GET | `/api/guild/options` | 1 | canais para o menu de envio (nível 1 só vê os liberados) e, do nível 2 em diante, os cargos, marcando os que `/api/roles/*` aceita |
| GET | `/api/auth/discord` | público | se o login com o Discord está ligado, e o client id e a URL de retorno |
| POST | `/api/auth/discord` | público | troca o `code` do Discord por uma sessão do painel |
| POST | `/api/messages/send` | 1 | manda o bot escrever num canal |
| POST | `/api/roles/add`, `/api/roles/remove` | 1 | gerencia cargos abaixo da patente de quem pede |
| POST | `/api/punishments/timeout` | 2 | aplica timeout |
| POST | `/api/punishments/remove-from-regiment` | 3 | remove os cargos do regimento |

### Onde fica a fronteira

Só entra quem tem o cargo **12º Staff** (`staffRoleIds`). Fora isso, o nível vem da patente no
Discord da BA, e o painel mostra a patente ao lado do nome ("Captain - Superior"):

**Nível 1, NCO** (Serjeant, Colour Serjeant e Quarter-Master Serjeant) faz a administração do
dia a dia: mandar mensagem por canal, consultar o efetivo, definir patente, corrigir números de
um jogador e anunciar deployment. Também dá e tira cargo, com a mesma regra de todos os níveis:
só cargos abaixo da própria patente, e só em quem tem patente menor. Cargo que dá acesso ao
painel (patentes de Serjeant para cima e o 12º Staff) nunca passa pelo painel.

**Nível 2, Oficial** (Serjeant Major, Ensign, Colour Ensign e Lieutenant: oficiais
comissionados) soma timeout, alistar via ROBLOX, DM em massa (até 500), publicar a auditoria,
os logs do bot e `status?detail=host`.

**Nível 3, Superior** (Captain e acima) tem tudo: soma remover do regimento e apagar registro
da auditoria.

**Nível 4, Developer**, vem só por ID (`level4UserIds`) e não depende de cargo.

Os níveis vivem em dois lugares e **precisam concordar**: `src/dashboard/gate.rs` é o portão
real, e o `applyUser` do painel apenas apaga os botões. Divergir faz a interface mentir —
botão aceso que devolve 403, ou apagado que funcionaria.

A lógica administrativa é compartilhada entre os comandos de barra e a API: os módulos
`deployment.rs`, `dm.rs`, `enlistment.rs`, `status.rs` e `audit/publish.rs` existem justamente
para que `/deployment` e `POST /api/deployment` (e assim por diante) produzam o mesmo resultado
em vez de duas cópias que divergem no primeiro ajuste.

**A DM em massa é um job, não um request.** Cada destinatário custa ~4,2s entre o limitador
global (3s, em `dm.rs`) e o espaçamento de 1,2s, então o teto de 500 pessoas leva cerca de
35 minutos. `POST /api/dm` responde `202` com um `jobId` e o envio segue em segundo plano; o
progresso vem de `GET /api/dm/status`. Os jobs vivem em memória e somem no reinício.

### `tunnelSecret`: como o bot sabe que a requisição veio mesmo pelo tunnel

O `dashboard_auth.json` tem um campo `tunnelSecret`. O Worker `daese-api-proxy` manda esse
valor no header `X-Ccore-Tunnel` em toda requisição que encaminha, e o bot só confia no
`CF-Connecting-IP` quando ele bate.

Sem isso o limite de 10 tentativas de login por IP era contornável: quem alcançasse
`127.0.0.1:5056` direto — outro processo na máquina, um segundo tunnel — mandava um
`CF-Connecting-IP` diferente por requisição e tinha tentativas ilimitadas; e, ao contrário,
dava para queimar de propósito as 10 tentativas do IP de outra pessoa.

Para trocar o segredo, os dois lados têm que mudar juntos.

Os comandos abaixo passam o segredo por um **arquivo temporário** em vez de uma variável de
shell. Isso não é firula: `NOVO=$(...)` é sintaxe de bash e **não funciona em fish**, onde a
linha vira um erro e `$NOVO` expande para nada. Uma rotação feita assim em fish já gravou a
string literal `$NOVO` no `tunnelSecret` e mandou lixo para o Worker — e, como campo errado
não derruba nada (ver o fim desta seção), ninguém percebeu. Um arquivo `600` é entendido
igual por bash, fish, zsh e sh.

**1. Gerar o segredo** (sem `\n` no fim — o que o Worker guarda vai literal no header):

```
python3 -c 'import os, secrets
fd = os.open("/tmp/ccore-tunnel-secret", os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
os.write(fd, secrets.token_urlsafe(32).encode())
os.close(fd)'
```

**2. Mandar para o Worker:**

```
cd nunreasonable.github.io/cloudflare/api-proxy-worker
npx wrangler secret put TUNNEL_SECRET < /tmp/ccore-tunnel-secret
```

**3. Gravar no `dashboard_auth.json`.** O comando troca **só** o literal do campo
`tunnelSecret` e reescreve o arquivo por truncamento, então o resto do JSON fica byte a byte
como estava e o modo `0600` (e o inode) é preservado — o que um `mv` de arquivo temporário
por cima perderia:

```
python3 -c 'import json, pathlib
alvo = pathlib.Path("ccore-rs/config/dashboard_auth.json")
novo = pathlib.Path("/tmp/ccore-tunnel-secret").read_text().strip()
txt = alvo.read_text()
chave = json.dumps("tunnelSecret")
antigo = json.dumps(json.loads(txt)["tunnelSecret"])
i = txt.index(antigo, txt.index(chave) + len(chave))
saida = txt[:i] + json.dumps(novo) + txt[i + len(antigo):]
assert json.loads(saida)["tunnelSecret"] == novo
alvo.write_text(saida)'
```

**4. Reiniciar e apagar o temporário:**

```
systemctl --user restart ccore-bot
rm /tmp/ccore-tunnel-secret
```

#### Conferir que os dois lados combinam

Desde 29/09/2026 basta o health pela borda:

```
curl -s -o /dev/null -w "borda %{http_code}\n" https://daeese.me/api/health
```

- **`200`** → os dois lados combinam.
- **`403`** com `Acesso direto à API recusado.` → os segredos **não** batem.

Isso vale porque o bot passou a **recusar** toda requisição que chega pela Cloudflare (tem
`CF-Ray`/`CF-Connecting-IP`) sem o `X-Ccore-Tunnel` certo — é a trava contra quem chama
`api.daeese.me` direto e pula o Worker. A consequência para a rotação é que erro de segredo
deixou de ser silencioso: antes ele só degradava o rate limit para um balde global; agora
derruba a API inteira pela borda até os dois lados baterem. Por isso a conferência continua
não sendo opcional — mas agora ela aparece.

Campo vazio **não** é erro: o bot avisa no log, o limite de login volta a ser global, o
orçamento geral por cliente fica desligado (seria um balde só para o mundo inteiro) e a trava de
acesso direto deixa de valer. É o modo de desenvolvimento, não o de produção.

### Limites da API

Todos por cliente (`ClientKey`: o `CF-Connecting-IP` quando o segredo bate, com IPv6 agrupado
no /64), e todos com `Retry-After` na resposta:

| Limite | Valor | Resposta |
|---|---|---|
| Qualquer requisição | 240 por minuto | 429 |
| POST por rota (por sessão, ou por IP sem sessão) | 30 por minuto | 429 |
| `/api/dm`, `/api/audit/push`, `/api/deployment` | 3 por minuto | 429 |
| Login | 10 tentativas a cada 5 minutos | 429 |
| Requisições em voo | 64, espera de até 10 s por vaga | 503 |
| Corpo | 64 KB | 413 |
| Tempo para o corpo chegar inteiro | 10 s | 408 |

Na frente disto ainda há o rate limit do Worker `daese-api-proxy` (120 por minuto por IP) e a
regra de rate limit do WAF da zona — ver a seção 16 do `INFRA.md` do `nunreasonable.github.io`.

`config.jsonc` e `dashboard_auth.json` **não são versionados** (contêm token e IDs do
servidor). Para montar um ambiente novo, copie os modelos e preencha:

```bash
cp ccore-rs/config/config.example.jsonc ccore-rs/config/config.jsonc
cp ConsoleApp1/config/dashboard_auth.example.json ccore-rs/config/dashboard_auth.json
```

O caminho `config/config.jsonc` é lido em relação ao diretório de trabalho, então o bot deve
ser executado de dentro de `ccore-rs/`. O `dashboard_auth.json` tem o mesmo formato da versão
em C#, por isso o modelo continua em `ConsoleApp1/config/`.

Observação: o bot agora usa o intent `MessageContent` para ler o texto das mensagens e detectar termos bloqueados. Esse intent também precisa estar habilitado no painel do aplicativo do Discord.

## Portal do Discord: os dois campos de URL ficam vazios

O painel do aplicativo oferece **Interactions Endpoint URL** e **Linked Roles
Verification URL**. Nenhum dos dois está preenchido, e o primeiro não deve ser.

**Interactions Endpoint URL — deixar vazio.** A documentação do Discord descreve
o Gateway e o endpoint HTTP como formas *mutuamente exclusivas* de receber
interação: preencher o campo faz o bot **parar de receber interações pelo
Gateway**. Isso quebraria todo comando que espera respostas pelo Gateway (os
collectors do serenity) — hoje `/audit-setranks`, `/audit-add`,
`/audit-edit` e `/robloxenlist`, além da paginação. Migrar exigiria
reimplementar à mão a verificação de assinatura Ed25519, o roteamento de
comandos e um armazenamento de estado para os fluxos de várias etapas, sem
ganho: o bot roda continuamente atrás do tunnel e o Gateway entrega tudo.

**Linked Roles Verification URL — opcional, e aditiva.** Essa não desliga nada.
Serve para exigir uma verificação feita pelo bot (por exemplo, conta do ROBLOX
confirmada) como requisito de um cargo do servidor. A infraestrutura HTTP
necessária já existe — ver a seção do dashboard acima — mas nada foi
implementado ainda.

## Colocar para rodar como serviço

A unit é versionada no repositório (`ccore-bot.service`, na raiz). Até 19/09/2026 ela existia
**só** na máquina, e a configuração de produção não tinha cópia em lugar nenhum.

```bash
cd ccore-rs && nice -n 10 cargo build --release && cd ..
cp ccore-bot.service ~/.config/systemd/user/
systemctl --user daemon-reload
systemctl --user enable --now ccore-bot
journalctl --user -u ccore-bot -f
```

**É serviço de usuário, não de sistema, de propósito.** O SELinux está `Enforcing` e o binário
mora em `/home` (contexto `user_home_t`); um serviço de sistema o executaria como `init_t`, uma
transição que a política padrão do Fedora bloqueia. Em troca, o `Linger` precisa estar ligado
para o bot sobreviver ao logout:

```bash
sudo loginctl enable-linger $USER
```

**O `--release` não é detalhe.** A unit aponta para `ccore-rs/target/release/ccore`; o build de
debug roda sem otimização. O ccore resolve `config/` e `data/` por caminho relativo cru, preso
ao `WorkingDirectory` (`ccore-rs/`) — o diretório de estado não se move junto com o binário.
O estado fica em `ccore-rs/config/` e `ccore-rs/data/`. (No
CommunityBot é o contrário: lá os caminhos saem de `AppContext.BaseDirectory`, e trocar Debug por
Release, ou o TFM, sem mover o `data/` faz o bot subir com o estado zerado.)

### Sobre `network-online.target`

A unit **não** usa `After=network-online.target`. Esse target só existe no systemd de
**sistema** — no gerenciador de usuário ele não existe, e `Wants=` para unidade inexistente não
é erro, é ignorado calado. As duas diretivas ficaram ali sem ordenar nada, e o sintoma era o bot
tomar `Resource temporarily unavailable (discord.com:443)` em todo boot, recuperando só no retry
de 7s da biblioteca da época (DisCatSharp, na versão em C#).

No lugar entrou um gate de DNS de verdade:

```ini
ExecStartPre=-/usr/bin/timeout 60 /usr/bin/bash -c 'until getent ahostsv4 discord.com >/dev/null 2>&1; do sleep 1; done'
```

O prefixo `-` é deliberado: se os 60s estourarem, o bot sobe assim mesmo e o retry volta a ser a
rede de segurança. O gate melhora o caso comum sem inventar um jeito novo de o bot não subir.

### Atenção ao desenvolver

O serviço ocupa a porta **5056**. Rodar `cargo run` à mão em paralelo falha com
`Address already in use` — pare o serviço antes
(`systemctl --user stop ccore-bot`).

## Requisitos

- Rust estável com edição 2024 (Rust 1.85 ou mais novo); no Fedora, `sudo dnf install rust cargo`
  ou o `rustup`. As dependências estão no `ccore-rs/Cargo.toml` e no `Cargo.lock`.
- Configuração adequada no `config.jsonc`
- Permissões do bot no servidor Discord

Para rodar os testes: `cd ccore-rs && cargo test`.

A versão antiga em `ConsoleApp1/` pedia .NET 10 e DisCatSharp; ela fica no repositório só como
referência.
