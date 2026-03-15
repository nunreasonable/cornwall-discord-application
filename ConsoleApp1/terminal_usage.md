# Como usar a Interface Terminal

## O que foi corrigido

O problema original era que o código criava uma segunda instância do DiscordClient, causando conflitos com a instância principal do bot.

## Como funciona agora

1. **Integração com o bot principal**: A interface terminal agora usa a mesma instância do DiscordClient que o bot principal
2. **Inicialização automática**: A interface é ativada automaticamente quando o bot conecta
3. **Comandos disponíveis**:
   - `>channel ID` - Define o canal alvo (substitua ID pelo ID numérico do canal)
   - `>say mensagem` - Envia uma mensagem para o canal definido
   - `>help` - Mostra a lista de comandos
   - `>exit` - Sai da interface terminal

## Exemplo de uso

1. Inicie o bot normalmente
2. Quando aparecer "Interface terminal ativada", use os comandos:
   ```
   >channel 123456789012345678
   >say Olá, este é um teste do terminal!
   ```

## Como obter o ID do canal

1. No Discord, clique com o botão direito no canal desejado
2. Selecione "Copiar ID do Canal" (precisa ter o modo desenvolvedor ativado nas configurações do Discord)

## Importante

- A interface não encerra o bot, apenas sai do modo terminal
- O bot continua funcionando normalmente com todos os comandos slash
- A interface roda em background e não interfere nas outras funcionalidades
