# Guia da Auditoria — passo a passo

Este guia é para quem vai **usar** a auditoria no dia a dia. Não é preciso entender nada
de programação: é só digitar comandos no Discord e conferir o que o bot responde.

---

## O que esse sistema faz

Antes, os números de cada batalha eram anotados na mão na planilha. Agora o bot cuida disso:

1. Depois de cada batalha, você **cola a auditoria** no Discord.
2. O bot guarda numa **fila de espera** (nada é publicado ainda).
3. Quando quiser, você manda o bot **publicar** tudo de uma vez.

O bot soma sozinho os kills, deaths e assists de cada pessoa e conta quantas batalhas
cada um participou.

> **Só o staff pode usar.** São os mesmos cargos que já liberam o `/enlistuser`.
> Se você não tiver o cargo, o bot avisa e não faz nada.

---

## Um aviso que evita 90% das dúvidas

Existem **duas etapas separadas**, e isso é de propósito:

| Etapa | Comando | O que acontece |
|---|---|---|
| 1. Guardar | `/audit-add` | Fica na **fila**. Ainda dá para corrigir. |
| 2. Publicar | `/audit-push` | Vira **oficial** e é somado ao total de todos. |

Ou seja: você pode adicionar 5 batalhas ao longo da semana e publicar todas juntas no fim.
Enquanto não publicar, **nada foi somado ainda** — e nada foi perdido também.

---

## Passo a passo: registrar uma batalha

### 1. Digite `/audit-add`

Vai abrir uma janelinha com uma caixa de texto grande.

### 2. Cole a auditoria na caixa

Uma linha por pessoa, assim:

```
RafaOdebrecht 12 3 5 0
DecafOdebrecht 8 1 2 0
BrithishOdebrecht 15 4 7 0
```

O que é cada número, na ordem:

```
RafaOdebrecht   12      3        5         0
     ↑           ↑      ↑        ↑         ↑
   nome        kills  deaths  assists   ignorado
```

- **nome** — o nome do **Roblox** da pessoa (não o do Discord)
- **kills**, **deaths**, **assists** — os números da batalha
- **o último número** é só formatação. O bot lê e joga fora. Pode deixar como vier.

Nomes com espaço funcionam normalmente (`Rafa Odebrecht 12 3 5 0`). O bot entende que os
últimos números são as estatísticas e que todo o resto é o nome.

### 3. Confira a prévia

Ao enviar, o bot mostra um resumo com:

- a tabela do que ele entendeu
- **quem é novo** (nunca apareceu antes) e **quem já existia**
- **linhas que ele não entendeu**, com o número da linha e o motivo

> ⚠️ **Se o resumo vier laranja em vez de verde**, alguma linha teve problema.
> Leia a parte "Linhas não reconhecidas", corrija e rode `/audit-add` de novo só com
> as linhas que ficaram de fora.

### 4. Pronto

Cada pessoa nessa auditoria ganhou **+1 batalha**. Se alguém aparecer em três auditorias
diferentes, ganha 3 batalhas — uma por auditoria.

Repita esse processo para cada batalha. Pode fazer quantas quiser antes de publicar.

---

## Passo a passo: publicar

Quando estiver na hora de tornar oficial:

### 1. Faça um teste primeiro (opcional, mas recomendado)

```
/audit-push dry_run:true
```

Isso **não publica nada**. Só mostra o que aconteceria: quantas auditorias serão somadas,
quantas pessoas mudam, quantas batalhas entram. Serve para conferir antes.

### 2. Publique de verdade

```
/audit-push
```

O bot soma tudo, publica e mostra um resumo com links. A fila fica vazia de novo.

> 💡 **Sem fila também vale.** `/audit-push` publica igualmente as mudanças feitas direto no
> arquivo por `/audit-import`, `/audit-edit` e `/audit-setranks` — é assim que elas chegam ao
> GitHub. Se não houver nada na fila **e** o GitHub já estiver igual ao arquivo local, o bot
> responde "Nada a publicar" e não cria commit nenhum.

### Se der erro

Não entre em pânico — **a fila não é apagada quando dá erro**. O bot inclusive avisa isso
na mensagem. É só resolver o problema e rodar `/audit-push` outra vez. Publicar duas vezes
por engano também não é problema: o bot reconhece o que já foi publicado e **não conta em
dobro**.

---

## Consultar a auditoria

```
/audit-check
```

Mostra a tabela com todo mundo: nome, kills, deaths, assists, batalhas e cargo.

Opções úteis (todas opcionais):

- **`jogador`** — mostra só uma pessoa, com o K/D dela. Ao digitar, o bot sugere os nomes
  que já existem, então não precisa acertar a digitação sozinho.
- **`ordenar`** — por Nome, Batalhas, Kills ou K/D.
- **`privado`** — marque `true` para só você ver a resposta.

Se a lista for longa, aparecem botões de navegação para passar as páginas.

---

## Corrigir um erro

```
/audit-edit jogador:NomeDaPessoa
```

Abre uma janela já preenchida com os números atuais. É só trocar o que estiver errado e
enviar. O bot mostra um "antes e depois" para você conferir.

Também dá para:

- **renomear** alguém (é só mudar o campo do nome)
- **remover** alguém, com `remover:true` — nesse caso o bot pede confirmação antes
- escolher onde corrigir com **`escopo`**: `Auditoria consolidada` (o total oficial, padrão)
  ou `Lotes pendentes` (algo que ainda está na fila)

> Se você tentar renomear alguém para um nome que já existe, o bot **recusa** em vez de
> juntar as duas pessoas em silêncio. Isso é proposital.

---

## Definir os cargos

```
/audit-setranks
```

O bot abre um painel com busca. São três passos:

**1. Ache a pessoa.** Clique em **🔍 Buscar** e digite parte do nome — `oda` já traz todos os
Odebrecht. Deixar a busca vazia lista o efetivo em ordem alfabética.

**2. Junte quem você quer mudar.** Selecione no menu; os escolhidos vão para a **cesta**, que
aparece no painel com o cargo atual de cada um. Cabem **até 10 pessoas**.

> 💡 **Buscar de novo não perde a cesta.** Selecione quatro numa busca, procure outro nome e
> selecione mais seis: os dez continuam lá. É por isso que não existe mais seta de página.

**3. Escreva os cargos.** Clique em **Definir cargos**. Abre uma janela com uma linha por
pessoa, já preenchida com o cargo atual — é só editar o que vem depois do `=`:

```
DecafOdebrecht = Lieutenant Colonel
RafaOdebrecht = Serjeant Major
chebobful =
```

- O cargo é **texto livre** — escreva como preferir (`C4. Serjeant Major`, `Sargento`, etc.)
- **Deixar vazio depois do `=` remove** o cargo daquela pessoa
- Se você errar um nome, o bot avisa qual não existe em vez de ignorar em silêncio

Clique em **Concluir** quando terminar. **Limpar seleção** esvazia a cesta sem alterar nada.

A sessão expira depois de 3 minutos parada. É só rodar o comando de novo.

---

## Primeira vez: trazer os dados da planilha

Isso só precisa ser feito **uma vez**, para o sistema começar já com todo o histórico.

```
/audit-import dry_run:true
```

Mostra o que seria importado sem gravar nada. Deve aparecer **110 jogadores**.

```
/audit-import dry_run:false
```

Agora sim, grava — no arquivo local. Depois rode `/audit-push` para publicar isso no GitHub
(mesmo sem nenhuma batalha na fila).

> ✅ **A importação não zera nada.** Ela traz nome, patente e batalhas da planilha
> exatamente como estão.
>
> ℹ️ **A planilha não tem kills, deaths e assists.** Esses números são acumulados apenas
> pelo `/audit-add`, e a importação nunca encosta neles — nem com `sobrescrever:true`.
>
> ✅ **Rodar de novo por engano não faz mal.** Quem já está no sistema não é alterado —
> o bot só avisa se encontrar batalhas diferentes.

---

## Ver quem fez o quê

```
/audit-logs
```

Mostra o histórico da auditoria, do mais recente para o mais antigo: data, quem executou e o
que foi feito em cada `/audit-add`, `/audit-edit`, `/audit-setranks`, `/audit-import` e
`/audit-push`.

Filtros opcionais:

- `usuario` — só as ações de uma pessoa
- `acao` — só um tipo (registro de batalha, edição, remoção, cargos, importação, publicação)
- `privado` — vem ligado por padrão; desligue para mostrar a resposta no canal

> O histórico fica no arquivo local do bot e guarda as **2000** ações mais recentes.
> Ele é independente do `audit.json`: publicar no GitHub não apaga nem sobrescreve o histórico.

---

## Perguntas frequentes

**Coloquei o nome do Discord em vez do nome do Roblox. E agora?**
Use `/audit-edit` para corrigir o nome, ou remova a entrada errada com `remover:true`.

**Escrevi maiúscula/minúscula diferente. Vai duplicar a pessoa?**
Não. `RafaOdebrecht` e `rafaodebrecht` são tratados como a mesma pessoa.

**Coloquei a mesma pessoa duas vezes na mesma auditoria.**
O bot soma os números das duas linhas e conta **uma** batalha só. Ele avisa isso no resumo.

**Publiquei sem querer.**
Sem problema — use `/audit-edit` para acertar os números de quem foi afetado.

**Esqueci se já publiquei.**
Rode `/audit-check`. Se ainda houver algo na fila, ele avisa no topo da resposta.

**O comando não aparece quando eu digito `/audit`.**
Provavelmente você não tem o cargo de staff, ou o bot está offline. Confira com quem
administra o bot.

**A janela do `/audit-add` sumiu antes de eu colar.**
Ela expira depois de 10 minutos. É só rodar o comando de novo — nada foi perdido.

---

## Resumo dos comandos

| Comando | Para quê |
|---|---|
| `/audit-add` | Registrar a auditoria de uma batalha (vai para a fila) |
| `/audit-push` | Publicar a fila e qualquer mudança feita no arquivo |
| `/audit-check` | Ver a auditoria, completa ou de uma pessoa |
| `/audit-edit` | Corrigir, renomear ou remover alguém |
| `/audit-setranks` | Definir o cargo de cada jogador |
| `/audit-import` | Trazer os dados da planilha (só na primeira vez) |
| `/audit-logs` | Ver quem mexeu na auditoria e o que foi feito |
