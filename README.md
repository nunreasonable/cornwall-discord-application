# Cornwall Discord Bot

Bot Discord para o 12° "The Cornwall" Regiment of Foot com funcionalidades de alistamento, deployments e mensagens.

## Comandos do Bot

### Alistamento & Verificação
- **`/enlistuser`** - Alista um usuário com verificação automática ROBLOX
  - Requer: Cargo de permissão configurado
  - Parâmetros: usuário, username ROBLOX
  - Função: Verificação anti-ALT + atribuição de cargos + nickname + log

- **`/alistar-se`** - Auto-alistamento com formulário interativo
  - Canal específico requerido
  - Função: Formulário completo + verificação ROBLOX automática

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

Registra kills, deaths, assists, batalhas e cargo de cada integrante. Funciona em duas
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
  - Em caso de erro a fila **não** é limpa; republicar não conta em dobro

- **`/audit-check`** - Consulta a auditoria
  - Parâmetros opcionais: `jogador` (com autocomplete), `ordenar`, `privado`
  - Função: tabela paginada do efetivo ou ficha individual com K/D

- **`/audit-edit`** - Corrige, renomeia ou remove um jogador
  - Parâmetros: `jogador`, `escopo` (consolidado ou pendente), `remover`
  - Função: janela pré-preenchida com os valores atuais e resumo antes/depois

- **`/audit-setranks`** - Define os cargos manualmente
  - Função: lista paginada, até 5 jogadores por vez; cargo é texto livre e campo em branco remove

- **`/audit-import`** - Importa a auditoria atual da planilha
  - Parâmetros: `dry_run` (padrão `true`), `sobrescrever` (padrão `false`)
  - Função: semeia o sistema preservando nome, cargo, batalhas e K/D/A; não altera quem já existe

### Sistema de Repost
- **`/messages`** - Verifica status do armazenamento de mensagens
  - Função: Estatísticas e progresso do sistema

- **`/repost`** - Republica mensagem aleatória armazenada
  - Requer: Mínimo de mensagens armazenadas
  - Cooldown: 2 horas após uso manual

### Utilitários
- **`/ping`** - Testa latência do bot
  - Função: Retorna tempo de resposta em ms

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

`config.jsonc` e `dashboard_auth.json` **não são versionados** (contêm token e IDs do
servidor). Para montar um ambiente novo, copie os modelos e preencha:

```bash
cp ConsoleApp1/config/config.example.jsonc ConsoleApp1/config/config.jsonc
cp ConsoleApp1/config/dashboard_auth.example.json ConsoleApp1/config/dashboard_auth.json
```

O caminho `config/config.jsonc` é lido em relação ao diretório de trabalho, então o bot deve
ser executado de dentro de `ConsoleApp1/`.

Observação: o bot agora usa o intent `MessageContent` para ler o texto das mensagens e detectar termos bloqueados. Esse intent também precisa estar habilitado no painel do aplicativo do Discord.

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
