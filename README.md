# Cornwall Discord Bot

Bot Discord para o 12° "The Cornwall" Regiment of Foot com funcionalidades de alistamento, deployments e mensagens.

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

- **`/dmreminder`** - Envia DM para cargo/usuário (sem código)
  - Requer: Cargo de permissão configurado
  - Parâmetros: cargo/usuário, mensagem
  - Limite: 500 membros por execução

### Sistema de Auditoria
> 📖 **Guia passo a passo para quem vai usar:** [`markdownspam/audit_usage.md`](markdownspam/audit_usage.md)
> — escrito em linguagem simples, sem exigir conhecimento técnico.

Registra kills, deaths, assists, batalhas e cargo de cada integrante, e guarda um histórico
de quem fez cada alteração (`/audit-logs`). Funciona em duas
etapas: primeiro as auditorias entram numa fila (`/audit-add`), depois são publicadas de
uma vez (`/audit-push`). Todos exigem o cargo de permissão do staff.

- **`/audit-add`** - Registra a auditoria de uma batalha
  - Abre uma janela para colar o bloco, uma linha por jogador: `nome k d a n`
  - O campo `n` é lido e descartado (é só formatação)
  - Cada execução vale **+1 batalha** para cada jogador listado
  - Função: valida linha a linha, mostra prévia e guarda na fila (não publica)

- **`/audit-push`** - Publica a fila
  - Parâmetro opcional: `dry_run` (simula sem gravar nem publicar)
  - Função: consolida os totais, publica na branch de dados e arquiva cada lote por data
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

- **`/audit-logs`** - Mostra quem mexeu na auditoria e o que foi feito
  - Parâmetros opcionais: `usuario`, `acao`, `privado` (padrão `true`)
  - Função: histórico paginado, do mais recente para o mais antigo, com data, autor e detalhe
    de cada `/audit-add`, `/audit-edit`, `/audit-setranks`, `/audit-import` e `/audit-push`

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

  As linhas vêm de um **buffer em memória** (`Services/BotLogBuffer.cs`), alimentado por um
  tee instalado sobre o `Console.Out` (`Services/ConsoleTee.cs`). O bot não usa biblioteca de
  log: escreve tudo com `Console.WriteLine`, e antes disso a única forma de ler essa saída era
  ter acesso à máquina e rodar `journalctl --user -u ccore-bot`.

  O tee **não substitui** o stdout, ele duplica: o journald continua recebendo tudo, e a
  interface de terminal continua funcionando. O buffer guarda as últimas 1000 linhas e
  **zera a cada reinício do bot** — para histórico que sobrevive a restart, o journald
  continua sendo a fonte.

### Comandos de Administração
- **`vsfdliliane`** - Comando especial restrito
  - Requer: ID de usuário específico
  - Função: Respostas personalizadas

---

## Configuração

O bot utiliza o arquivo `ConsoleApp1/config/config.jsonc` para configurações de:
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

## Interface de terminal

Além dos comandos do Discord, o bot aceita comandos em texto
(`ConsoleApp1/terminalshenanigans.cs`): `>channel ID` escolhe um canal, texto solto vira uma mensagem
nesse canal, `>help` lista tudo e `>exit` encerra a sessão.

Em produção o bot roda como serviço do systemd (`ccore-bot.service`), e serviço do systemd
recebe o stdin ligado em `/dev/null`. Por isso a interface **nunca** subia na máquina de
verdade: o bot logava `stdin nao interativo; interface de terminal desativada` e pronto.

Agora ela tem dois caminhos, com o mesmo interpretador atrás dos dois:

- **stdin**, quando o processo tem terminal de verdade (`dotnet run` na mão);
- **socket Unix**, sempre — inclusive sob o systemd.

Para abrir uma sessão no bot que já está rodando, basta rodar o próprio binário em modo
cliente:

```bash
ConsoleApp1/bin/Debug/net9.0/ConsoleApp1 --terminal
```

O socket fica em `$XDG_RUNTIME_DIR/ccore-bot/terminal.sock` (modo `0600`, dentro de um
diretório `0700`), então só o dono do processo consegue falar com ele — nada é exposto na
rede. `CCORE_TERMINAL_SOCKET` troca esse caminho, se precisar. Várias sessões podem ficar
abertas ao mesmo tempo: cada uma tem o seu próprio canal atual, e o `>exit` fecha só a
sessão de quem digitou, nunca o bot.

## Dashboard e API HTTP

O bot expõe uma API em `http://127.0.0.1:5056` (`Services/DashboardHttpService.cs`), consumida
pelo painel em [dashboard.daeese.me](https://dashboard.daeese.me/) e pela página pública de
status em [ccore.daeese.me/status](https://ccore.daeese.me/status/). O login é o código de
`/dashboardlink`; os níveis 1–4 vêm de `dashboard_auth.json`.

| Método | Rota | Nível | O que faz |
|---|---|---|---|
| GET | `/api/health` | público | ping da API |
| GET | `/api/status` | público | uptime, latência, servidores, membros, versão |
| GET | `/api/status?detail=host` | 2 | acrescenta SO, disco, carga e memória |
| GET | `/api/logs` | 2 | buffer de logs (`take`, `level`, `q`) |
| GET | `/api/audit/roster` | 1 | efetivo consolidado + fila pendente |
| GET | `/api/audit/history` | 1 | histórico da auditoria (o mesmo do `/audit-logs`) |
| POST | `/api/audit/entry` | **1 / 2** | edita e renomeia com 1; **remover** exige 2 |
| POST | `/api/audit/ranks` | 1 | define patentes |
| POST | `/api/audit/push` | 2 | publica no GitHub (aceita `dryRun`) |
| GET | `/api/promotions` | 1 | elegibilidade pela escada de promoções |
| POST | `/api/deployment` | 1 | envia a mensagem de deployment |
| POST | `/api/dm` | 2 | inicia DM em massa, devolve `jobId` |
| GET | `/api/dm/status` | 2 | progresso do job (`?jobId=`) |
| POST | `/api/enlist` | 2 | alista com verificação ROBLOX |
| POST | `/api/messages/send` | 1 | manda o bot escrever num canal |
| POST | `/api/roles/add`, `/api/roles/remove` | 2 | gerencia cargos |
| POST | `/api/punishments/timeout` | 2 | aplica timeout |
| POST | `/api/punishments/remove-from-regiment` | 2 | remove os cargos do regimento |

### Onde fica a fronteira

**Nível 1 (NCO/Officer)** faz a administração do dia a dia: mandar mensagem por canal,
consultar o efetivo, definir patente, corrigir números de um jogador e anunciar deployment.
Nada nesse conjunto remove alguém nem fala com serviço de terceiro.

**Nível 2 (Regimental Command)** concentra três famílias:

- **remove gente** — expulsar do regimento, timeout, tirar cargo, apagar registro da auditoria;
- **fala com API externa** — publicar no GitHub, alistar via ROBLOX;
- **age em volume ou expõe o interior** — DM em massa (até 500), logs do bot e
  `status?detail=host`, que carregam id de usuário e caminho da máquina.

Conceder cargo fica no 2 junto com remover, e não no 1, porque dar cargo pode escalar
privilégio.

Os níveis vivem em dois lugares e **precisam concordar**: `DashboardHttpService` é o portão
real, e o `applyUser` do painel apenas apaga os botões. Divergir faz a interface mentir —
botão aceso que devolve 403, ou apagado que funcionaria.

A lógica administrativa é compartilhada entre os comandos de barra e a API: `DeploymentBuilder`,
`MassDmService`, `EnlistmentService`, `BotStatusSnapshot` e `AuditPublisher` existem justamente
para que `/deployment` e `POST /api/deployment` (e assim por diante) produzam o mesmo resultado
em vez de duas cópias que divergem no primeiro ajuste.

**A DM em massa é um job, não um request.** Cada destinatário custa ~4,2s entre o limitador
global (`DmRateLimiter`, 3s) e o espaçamento de 1,2s, então o teto de 500 pessoas leva cerca de
35 minutos. `POST /api/dm` responde `202` com um `jobId` e o envio segue em segundo plano; o
progresso vem de `GET /api/dm/status`. Os jobs vivem em memória e somem no reinício.

`config.jsonc` e `dashboard_auth.json` **não são versionados** (contêm token e IDs do
servidor). Para montar um ambiente novo, copie os modelos e preencha:

```bash
cp ConsoleApp1/config/config.example.jsonc ConsoleApp1/config/config.jsonc
cp ConsoleApp1/config/dashboard_auth.example.json ConsoleApp1/config/dashboard_auth.json
```

O caminho `config/config.jsonc` é lido em relação ao diretório de trabalho, então o bot deve
ser executado de dentro de `ConsoleApp1/`.

Observação: o bot agora usa o intent `MessageContent` para ler o texto das mensagens e detectar termos bloqueados. Esse intent também precisa estar habilitado no painel do aplicativo do Discord.

## Portal do Discord: os dois campos de URL ficam vazios

O painel do aplicativo oferece **Interactions Endpoint URL** e **Linked Roles
Verification URL**. Nenhum dos dois está preenchido, e o primeiro não deve ser.

**Interactions Endpoint URL — deixar vazio.** A documentação do Discord descreve
o Gateway e o endpoint HTTP como formas *mutuamente exclusivas* de receber
interação: preencher o campo faz o bot **parar de receber interações pelo
Gateway**. Isso quebraria todo comando que depende da extensão `Interactivity`,
que escuta eventos do Gateway — hoje `/audit-setranks`, `/audit-add`,
`/audit-edit` e `/robloxenlist`, além da paginação. Migrar exigiria
reimplementar à mão a verificação de assinatura Ed25519, o roteamento de
comandos e um armazenamento de estado para os fluxos de várias etapas, sem
ganho: o bot roda continuamente atrás do tunnel e o Gateway entrega tudo.

**Linked Roles Verification URL — opcional, e aditiva.** Essa não desliga nada.
Serve para exigir uma verificação feita pelo bot (por exemplo, conta do ROBLOX
confirmada) como requisito de um cargo do servidor. A infraestrutura HTTP
necessária já existe — ver a seção do dashboard acima — mas nada foi
implementado ainda.

## Requisitos

- .NET 9.0 (o projeto tem `TargetFramework` `net9.0`; `global.json` fixa o SDK em 9.0.x)
- DisCatSharp
- Configuração adequada no `config.jsonc`
- Permissões do bot no servidor Discord

## Troubleshooting de Build/Restore

Se `dotnet restore` ou `dotnet build` falhar com `NU1301` e timeout para `https://api.nuget.org/v3/index.json`, execute com IPv6 desativado:

```bash
DOTNET_SYSTEM_NET_DISABLEIPV6=1 dotnet restore ConsoleApp1/ConsoleApp1.csproj
DOTNET_SYSTEM_NET_DISABLEIPV6=1 dotnet build ConsoleApp1/ConsoleApp1.csproj
```

Observação: as tasks do VS Code em `.vscode/tasks.json` já foram configuradas para usar essa variável automaticamente.
