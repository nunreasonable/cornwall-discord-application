# Comando de Deployment

## Uso
Use o comando `/deployment` com apenas o código para enviar uma mensagem de deployment automática com embed, role pings e botões.

## Parâmetros
- **codigo** (obrigatório): Código para acesso ao jogo

## Exemplo de Uso
```
/deployment codigo:"JIZTSL"
```

## Funcionalidades
- ✅ Mensagens em português
- ✅ Role pings automáticos (configurados no JSON)
- ✅ Imagem automática (configurada no JSON)
- ✅ Título automático (configurado no JSON)
- ✅ Canal de voz automático (configurado no JSON)
- ✅ Botões interativos
- ✅ Link do jogo configurado no config.json
- ✅ Verificação de permissões
- ✅ Tratamento de erros

## Configurações

### config.json
Adicione as seguintes configurações ao seu `config.json`:

```json
{
    "deploymentGameLink": "https://www.roblox.com/games/12068120918/Napoleonic-Wars",
    "deploymentVoiceChatLink": "https://discord.com/channels/1397973799105855570/1417989019605925978",
    "deploymentAllowedRoleIds": [1397974337457356871],
    "deploymentDefaultRoles": [1417986656774000753],
    "deploymentTitle": "32ND À BATALHA!!!!!",
    "deploymentVoiceChannel": "Bar de Cornwall",
    "deploymentImageUrl": "https://media.discordapp.net/attachments/1417988789267333231/1472332335725805741/Captura_de_tela_2026-02-14_174200.png?ex=69b5c857&is=69b476d7&hm=a3dbc7fd29b24a47443ceac555407d90bfebe9cc8505c0d70c62437f607b277d&=&format=webp&quality=lossless&width=1343&height=848",
    "deploymentQuickLaunchLink": "https://www.roblox.com/games/start?launchData=CODE_HERE&placeId=12068120918",
    "deploymentChannelId": 1417989019605925978
}
```

- **deploymentGameLink**: Link do jogo para os botões (sobrescreve defaultGameLink)
- **deploymentVoiceChatLink**: Link para o canal de voz (opcional)
- **deploymentAllowedRoleIds**: Array de IDs de roles que podem usar o comando (além do enlistPermissionRoleId)
- **deploymentDefaultRoles**: Array de IDs de roles para pingar automaticamente
- **deploymentTitle**: Título padrão da mensagem de deployment
- **deploymentVoiceChannel**: Nome do canal de voz padrão
- **deploymentImageUrl**: URL da imagem para o embed (opcional)
- **deploymentQuickLaunchLink**: Template completo do link de Quick Launch (CODE_HERE será substituído pelo código informado)
- **deploymentChannelId**: ID do canal onde as mensagens de deployment serão enviadas (obrigatório)

### Permissões
Apenas usuários com:
- O cargo configurado em `enlistPermissionRoleId` no config.json, OU
- Qualquer cargo listado em `deploymentAllowedRoleIds`

Podem usar este comando.

### Restrição de Canal
Este comando pode ser executado em qualquer canal, mas a mensagem de deployment será enviada exclusivamente para o canal configurado em `deploymentChannelId`.

## Botões
- **Quick Launch**: Botão primário com link dinâmico usando `deploymentQuickLaunchLink` do config (CODE_HERE substituído pelo código)
- **Napoleonic Wars Game**: Botão secundário para o jogo
- **Entrar no Canal de Voz**: Botão de sucesso para o VC

Observação: O botão Quick Launch agora usa o template completo configurado no JSON, onde CODE_HERE é substituído pelo código informado. O link do jogo também está disponível no campo "Link Rápido" do embed.
