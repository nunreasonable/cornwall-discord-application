using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using CornwallUtilities.config;
using DisCatSharp;
using DisCatSharp.Entities;
using DisCatSharp.Interactivity.Extensions;
using DisCatSharp.ApplicationCommands;
using DisCatSharp.Enums;
using DisCatSharp.ApplicationCommands.Context;
using DisCatSharp.ApplicationCommands.Attributes;
using Newtonsoft.Json.Linq;

namespace CornwallUtilities.commands
{
    internal class EnlistUser : ApplicationCommandsModule
    {
        [SlashCommand("enlistuser", "Alista um usuário (verificação ROBLOX automática + cargos + nickname + log).")]
        public async Task EnlistUserCommand(
            InteractionContext ctx,
            [Option("user", "Usuário a ser alistado")] DiscordUser user,
            [Option("roblox_username", "Username do ROBLOX do usuário")] string robloxUsername,
            [Option("socialrole", "Adicionar cargo social?")] bool socialRole = false)
        {

            await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);

            var config = new JSONReader();
            await config.ReadJSON();

            if (ctx.Guild is null)
            {
                var guildEmbed = new DiscordEmbedBuilder()
                    .WithTitle("Comando inválido")
                    .WithDescription("Este comando só pode ser executado em um servidor (guild).")
                    .WithColor(DiscordColor.IndianRed);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(guildEmbed));
                return;
            }

            if (!config.enlistPermissionRoleId.HasValue)
            {
                var missingConfig = new DiscordEmbedBuilder()
                    .WithTitle("Configuração inválida")
                    .WithDescription("O ID do cargo com permissão para usar este comando não está configurado. Verifique o arquivo config.json (enlistPermissionRoleId).")
                    .WithColor(DiscordColor.IndianRed);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(missingConfig));
                return;
            }

            var requiredRoleId = config.enlistPermissionRoleId.Value;
            var hasPermission = ctx.Member?.Roles.Any(r => r.Id == requiredRoleId) ?? false;
            if (!hasPermission)
            {
                var permEmbed = new DiscordEmbedBuilder()
                    .WithTitle("Permissão negada")
                    .WithDescription("Você não possui o cargo necessário para usar este comando.")
                    .WithColor(DiscordColor.IndianRed);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(permEmbed));
                return;
            }

            DiscordMember? targetMember;
            try
            {
                targetMember = await ctx.Guild.GetMemberAsync(user.Id);
            }
            catch
            {
                var notFoundEmbed = new DiscordEmbedBuilder()
                    .WithTitle("Usuário não encontrado")
                    .WithDescription("O usuário fornecido não é membro deste servidor.")
                    .WithColor(DiscordColor.IndianRed);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(notFoundEmbed));
                return;
            }

            var robloxName = robloxUsername?.Trim() ?? string.Empty;

            // Debug: mostrar o username recebido
            Console.WriteLine($"[DEBUG] Username recebido: '{robloxUsername}'");
            Console.WriteLine($"[DEBUG] Username após trim: '{robloxName}'");
            Console.WriteLine($"[DEBUG] Username length: {robloxName.Length}");

            if (string.IsNullOrWhiteSpace(robloxName))
            {
                var invalidInput = new DiscordEmbedBuilder()
                    .WithTitle("Username inválido")
                    .WithDescription("O username fornecido está vazio. Execute o comando novamente.")
                    .WithColor(DiscordColor.IndianRed);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(invalidInput));
                return;
            }

            int badgeCount = 0;
            int friendsCount;
            TimeSpan accountAge;
            long robloxUserId;
            bool badgesAvailable = true;

            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromSeconds(20);

                try
                {
                    // Resolve username -> userId
                    var lookupPayload = new JObject
                    {
                        ["usernames"] = new JArray(robloxName),
                        ["excludeBannedUsers"] = true
                    };

                    using (var content = new StringContent(lookupPayload.ToString(), Encoding.UTF8, "application/json"))
                    {
                        var usernameResponse = await http.PostAsync("https://users.roblox.com/v1/usernames/users", content);
                        if (!usernameResponse.IsSuccessStatusCode)
                        {
                            var errLookup = new DiscordEmbedBuilder()
                                .WithTitle("Erro ao consultar ROBLOX")
                                .WithDescription("Não foi possível encontrar uma conta ROBLOX com esse nome. Verifique se o nome foi digitado corretamente.")
                                .WithColor(DiscordColor.IndianRed);

                            await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(errLookup));
                            return;
                        }

                        var usernameJson = JObject.Parse(await usernameResponse.Content.ReadAsStringAsync());
                        Console.WriteLine($"[DEBUG] ROBLOX API Response: {usernameJson.ToString()}");
                        var dataArrayLookup = usernameJson["data"] as JArray;
                        Console.WriteLine($"[DEBUG] Data array count: {dataArrayLookup?.Count ?? 0}");
                        if (dataArrayLookup == null || dataArrayLookup.Count == 0)
                        {
                            var notFound = new DiscordEmbedBuilder()
                                .WithTitle("Conta ROBLOX não encontrada")
                                .WithDescription("Nenhuma conta ROBLOX foi encontrada com o nome informado.")
                                .WithColor(DiscordColor.IndianRed);

                            await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(notFound));
                            return;
                        }

                        robloxUserId = (long?)dataArrayLookup[0]?["id"] ?? 0;
                        if (robloxUserId <= 0)
                        {
                            var invalidLookup = new DiscordEmbedBuilder()
                                .WithTitle("Conta ROBLOX inválida")
                                .WithDescription("Não foi possível determinar o ID da conta ROBLOX a partir do nome informado.")
                                .WithColor(DiscordColor.IndianRed);

                            await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(invalidLookup));
                            return;
                        }
                    }

                    // Dados básicos (inclui data de criação)
                    var userInfoResponse = await http.GetAsync($"https://users.roblox.com/v1/users/{robloxUserId}");
                    if (!userInfoResponse.IsSuccessStatusCode)
                    {
                        var errEmbed = new DiscordEmbedBuilder()
                            .WithTitle("Erro ao consultar ROBLOX")
                            .WithDescription("Não foi possível obter as informações da conta ROBLOX.")
                            .WithColor(DiscordColor.IndianRed);

                        await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(errEmbed));
                        return;
                    }

                    var userInfoJson = JObject.Parse(await userInfoResponse.Content.ReadAsStringAsync());
                    var createdToken = userInfoJson["created"];
                    var createdStr = createdToken?.Value<string>()?.Trim();
                    if (string.IsNullOrWhiteSpace(createdStr) ||
                        !DateTimeOffset.TryParse(createdStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var createdAt))
                    {
                        var errEmbed = new DiscordEmbedBuilder()
                            .WithTitle("Erro ao ler data de criação")
                            .WithDescription("Não foi possível determinar a data de criação da conta ROBLOX.")
                            .WithColor(DiscordColor.IndianRed);

                        await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(errEmbed));
                        return;
                    }

                    accountAge = DateTimeOffset.UtcNow - createdAt;

                    // Contagem de amigos
                    var friendsResponse = await http.GetAsync($"https://friends.roblox.com/v1/users/{robloxUserId}/friends/count");
                    if (!friendsResponse.IsSuccessStatusCode)
                    {
                        var errEmbed = new DiscordEmbedBuilder()
                            .WithTitle("Erro ao consultar amigos ROBLOX")
                            .WithDescription("Não foi possível obter a quantidade de amigos da conta ROBLOX.")
                            .WithColor(DiscordColor.IndianRed);

                        await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(errEmbed));
                        return;
                    }

                    var friendsJson = JObject.Parse(await friendsResponse.Content.ReadAsStringAsync());
                    friendsCount = (int?)friendsJson["count"] ?? 0;

                    // Badges (opcionais)
                    try
                    {
                        var badgesResponse = await http.GetAsync($"https://badges.roblox.com/v1/users/{robloxUserId}/badges?limit=100&sortOrder=Asc");
                        if (!badgesResponse.IsSuccessStatusCode)
                        {
                            badgesAvailable = false;
                            Console.WriteLine($"[WARN] Falha ao consultar badges ROBLOX. Status: {badgesResponse.StatusCode}");
                        }
                        else
                        {
                            var badgesJson = JObject.Parse(await badgesResponse.Content.ReadAsStringAsync());
                            var dataArray = badgesJson["data"] as JArray;
                            if (dataArray is null)
                            {
                                badgesAvailable = false;
                                Console.WriteLine("[WARN] Resposta de badges ROBLOX sem campo 'data'.");
                            }
                            else
                            {
                                badgeCount = dataArray.Count;
                            }
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        badgesAvailable = false;
                        Console.WriteLine("[WARN] Tempo excedido ao consultar badges ROBLOX.");
                    }
                    catch (Exception ex)
                    {
                        badgesAvailable = false;
                        Console.WriteLine($"[WARN] Falha ao consultar badges ROBLOX: {ex.Message}");
                    }
                }
                catch (TaskCanceledException)
                {
                    var timeoutEmbed = new DiscordEmbedBuilder()
                        .WithTitle("Tempo excedido")
                        .WithDescription("A consulta à API do ROBLOX demorou demais. Tente novamente em instantes.")
                        .WithColor(DiscordColor.IndianRed);

                    await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(timeoutEmbed));
                    return;
                }
                catch (Exception ex)
                {
                    var err = ex.Message ?? string.Empty;
                    if (err.Length > 150)
                        err = err[..147] + "...";

                    var genericEmbed = new DiscordEmbedBuilder()
                        .WithTitle("Erro ao consultar ROBLOX")
                        .WithDescription($"Ocorreu um erro inesperado ao consultar a conta ROBLOX: `{err}`")
                        .WithColor(DiscordColor.IndianRed);

                    await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(genericEmbed));
                    return;
                }
            }

            // Critérios principais para NÃO ser alt
            var minAccountAge = TimeSpan.FromDays(90); // > 3 meses
            const int minFriends = 1;
            const int minBadgesForBonus = 50; // usado apenas como bônus, não bloqueia

            var passesAge = accountAge >= minAccountAge;
            var passesFriends = friendsCount >= minFriends;
            var hasBadgeBonus = badgeCount >= minBadgesForBonus;
            var badgesDisplay = badgesAvailable ? badgeCount.ToString() : "Indisponível";

            // A decisão de ALT usa apenas idade da conta + amigos.
            // Badges contam apenas como informação/bônus, não bloqueiam o alistamento.
            var isLikelyMain = passesAge && passesFriends;

            if (!isLikelyMain)
            {
                var deniedEmbed = new DiscordEmbedBuilder()
                    .WithTitle("Alistamento negado - Conta provavelmente ALT")
                    .WithDescription("A conta ROBLOX fornecida não atende aos critérios mínimos de confiabilidade.")
                    .WithColor(DiscordColor.IndianRed)
                    .AddField(new DiscordEmbedField("Idade da conta", $"{accountAge.Days} dias", true))
                    .AddField(new DiscordEmbedField("Amigos", friendsCount.ToString(), true))
                    .AddField(new DiscordEmbedField("Badges (bônus)", badgesDisplay, true));

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(deniedEmbed));
                return;
            }

            // Adiciona cargos ao membro (ignorando cargos que ele já possui)
            var addedRoles = new List<DiscordRole>();
            if (config.enlistTargetRoleIds != null && config.enlistTargetRoleIds.Length > 0)
            {
                Console.WriteLine($"[DEBUG] Tentando adicionar {config.enlistTargetRoleIds.Length} cargos ao usuário {targetMember.Username}");
                
                // Verifica se o bot tem permissão para gerenciar cargos
                var botMember = await ctx.Guild.GetMemberAsync(ctx.Client.CurrentUser.Id);
                var botCanManageRoles = botMember?.PermissionsIn(ctx.Channel).HasPermission(Permissions.ManageRoles) ?? false;
                Console.WriteLine($"[DEBUG] Bot pode gerenciar cargos: {botCanManageRoles}");
                
                if (!botCanManageRoles)
                {
                    Console.WriteLine("[ERROR] Bot não tem permissão para gerenciar cargos!");
                    await ctx.Channel.SendMessageAsync("⚠️ **Aviso**: O bot não tem permissão para gerenciar cargos. Verifique as permissões do bot.");
                }
                
                foreach (var roleId in config.enlistTargetRoleIds)
                {
                    Console.WriteLine($"[DEBUG] Processando cargo ID: {roleId}");
                    
                    if (!ctx.Guild.Roles.TryGetValue(roleId, out var role))
                    {
                        Console.WriteLine($"[ERROR] Cargo ID {roleId} não encontrado no servidor");
                        continue;
                    }

                    Console.WriteLine($"[DEBUG] Cargo encontrado: {role.Name} (ID: {role.Id})");

                    // Ignora cargos que o membro já possui
                    if (targetMember.Roles.Any(r => r.Id == roleId))
                    {
                        Console.WriteLine($"[DEBUG] Usuário já possui o cargo {role.Name}");
                        continue;
                    }

                    // Verifica hierarquia de cargos - o bot só pode atribuir cargos abaixo do seu cargo mais alto
                    var botHighestRole = botMember?.Roles.OrderByDescending(r => r.Position).FirstOrDefault();
                    if (botHighestRole is not null && role.Position >= botHighestRole.Position)
                    {
                        Console.WriteLine($"[ERROR] Não é possível atribuir o cargo {role.Name} - posição ({role.Position}) é igual ou superior ao cargo mais alto do bot ({botHighestRole.Name} - posição {botHighestRole.Position})");
                        continue;
                    }

                    try
                    {
                        Console.WriteLine($"[DEBUG] Tentando adicionar cargo {role.Name} ao usuário {targetMember.Username}");
                        await targetMember.GrantRoleAsync(role, "Alistamento via comando");
                        addedRoles.Add(role);
                        Console.WriteLine($"[SUCCESS] Cargo {role.Name} adicionado com sucesso");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ERROR] Falha ao adicionar cargo {role.Name}: {ex.Message}");
                        // Continue tentando outros cargos mesmo que um falhe
                    }
                }
                
                Console.WriteLine($"[DEBUG] Total de cargos adicionados: {addedRoles.Count}/{config.enlistTargetRoleIds.Length}");
            }
            else
            {
                Console.WriteLine("[DEBUG] Nenhum cargo configurado para alistamento (enlistTargetRoleIds está vazio)");
            }

            if (socialRole)
            {
                if (!config.enlistSocialRoleId.HasValue || config.enlistSocialRoleId.Value == 0)
                {
                    Console.WriteLine("[ERROR] enlistSocialRoleId não está configurado no config.jsonc");
                    await ctx.Channel.SendMessageAsync("⚠️ **Aviso**: O cargo social não está configurado. Verifique o `enlistSocialRoleId` no config.jsonc.");
                }
                else if (!ctx.Guild.Roles.TryGetValue(config.enlistSocialRoleId.Value, out var socialRoleEntity))
                {
                    Console.WriteLine($"[ERROR] Cargo social ID {config.enlistSocialRoleId.Value} não encontrado no servidor");
                    await ctx.Channel.SendMessageAsync("⚠️ **Aviso**: O cargo social configurado não foi encontrado no servidor. Verifique o `enlistSocialRoleId` no config.jsonc.");
                }
                else if (targetMember.Roles.Any(r => r.Id == socialRoleEntity.Id))
                {
                    Console.WriteLine("[DEBUG] Usuário já possui o cargo social");
                }
                else
                {
                    var botMember = await ctx.Guild.GetMemberAsync(ctx.Client.CurrentUser.Id);
                    var botCanManageRoles = botMember?.PermissionsIn(ctx.Channel).HasPermission(Permissions.ManageRoles) ?? false;
                    if (!botCanManageRoles)
                    {
                        Console.WriteLine("[ERROR] Bot não tem permissão para gerenciar cargos (cargo social)");
                        await ctx.Channel.SendMessageAsync("⚠️ **Aviso**: O bot não tem permissão para gerenciar cargos. Verifique as permissões do bot.");
                    }
                    else
                    {
                        var botHighestRole = botMember?.Roles.OrderByDescending(r => r.Position).FirstOrDefault();
                        if (botHighestRole is not null && socialRoleEntity.Position >= botHighestRole.Position)
                        {
                            Console.WriteLine($"[ERROR] Não é possível atribuir o cargo social {socialRoleEntity.Name} - posição é igual ou superior ao cargo mais alto do bot");
                            await ctx.Channel.SendMessageAsync("⚠️ **Aviso**: Não foi possível atribuir o cargo social devido à hierarquia de cargos do bot.");
                        }
                        else
                        {
                            try
                            {
                                await targetMember.GrantRoleAsync(socialRoleEntity, "Cargo social via comando");
                                addedRoles.Add(socialRoleEntity);
                                Console.WriteLine("[SUCCESS] Cargo social adicionado com sucesso");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[ERROR] Falha ao adicionar cargo social: {ex.Message}");
                                await ctx.Channel.SendMessageAsync($"⚠️ **Aviso**: Falha ao adicionar o cargo social. Erro: {ex.Message}");
                            }
                        }
                    }
                }
            }

            // Atualiza nickname adicionando o prefixo [12°] se ainda não existir
            var currentNick = targetMember.Nickname ?? targetMember.Username;
            const string prefix = "[12°]";
            Console.WriteLine($"[DEBUG] Nickname atual: '{currentNick}'");
            Console.WriteLine($"[DEBUG] Verificando se já tem prefixo '{prefix}'");
            
            if (!currentNick.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var newNick = $"{prefix} {currentNick}";
                Console.WriteLine($"[DEBUG] Novo nickname será: '{newNick}'");
                
                // Verifica se o bot tem permissão para gerenciar nicknames
                var botMember = await ctx.Guild.GetMemberAsync(ctx.Client.CurrentUser.Id);
                var botCanManageNicknames = botMember?.PermissionsIn(ctx.Channel).HasPermission(Permissions.ManageNicknames) ?? false;
                Console.WriteLine($"[DEBUG] Bot pode gerenciar nicknames: {botCanManageNicknames}");
                
                if (!botCanManageNicknames)
                {
                    Console.WriteLine("[ERROR] Bot não tem permissão para gerenciar nicknames!");
                    await ctx.Channel.SendMessageAsync("⚠️ **Aviso**: O bot não tem permissão para gerenciar apelidos. Verifique as permissões do bot.");
                }
                else
                {
                    try
                    {
                        Console.WriteLine($"[DEBUG] Tentando alterar nickname de '{currentNick}' para '{newNick}'");
                        await targetMember.ModifyAsync(m => m.Nickname = newNick);
                        Console.WriteLine("[SUCCESS] Nickname alterado com sucesso");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ERROR] Falha ao alterar nickname: {ex.Message}");
                        await ctx.Channel.SendMessageAsync($"⚠️ **Aviso**: Não foi possível alterar o nickname. Erro: {ex.Message}");
                    }
                }
            }
            else
            {
                Console.WriteLine("[DEBUG] Usuário já possui o prefixo [12°] no nickname");
            }

            // Envia log para canal específico (busca o canal na API do Discord para não depender do cache)
            if (config.enlistLogChannelId.HasValue)
            {
                try
                {
                    var logChannel = await ctx.Client.GetChannelAsync(config.enlistLogChannelId.Value);
                    if (logChannel is not null && logChannel.GuildId == ctx.Guild.Id)
                    {
                        var logEmbed = new DiscordEmbedBuilder()
                            .WithTitle("12° Regiment - Recruit Log")
                            .WithDescription("Registro de alistamento realizado com sucesso.")
                            .WithColor(DiscordColor.Blurple)
                            .WithThumbnail(targetMember.GetAvatarUrl(MediaFormat.Auto))
                            .WithFooter("Recruit log gerado por CornwallBot", ctx.Client.CurrentUser.AvatarUrl)
                            .WithTimestamp(DateTimeOffset.UtcNow)
                            .AddField(new DiscordEmbedField("Executor", ctx.User.Mention, true))
                            .AddField(new DiscordEmbedField("Alistado", user.Mention, true))
                            .AddField(new DiscordEmbedField("Nome no ROBLOX", robloxName, true))
                            .AddField(new DiscordEmbedField("ROBLOX ID", robloxUserId.ToString(), true))
                            .AddField(new DiscordEmbedField("Idade da conta (dias)", accountAge.Days.ToString(), true))
                            .AddField(new DiscordEmbedField("Amigos", friendsCount.ToString(), true))
                            .AddField(new DiscordEmbedField("Badges", badgesDisplay, true))
                            .AddField(new DiscordEmbedField("Cargos adicionados", addedRoles.Count > 0 ? string.Join(", ", addedRoles.Select(r => r.Mention)) : "Nenhum", false))
                            .AddField(new DiscordEmbedField("Cargo social?", socialRole ? "Sim" : "Não", true))
                            .AddField(new DiscordEmbedField("Verificação de alt", "Automática (aprovado)", true));

                        await logChannel.SendMessageAsync(new DiscordMessageBuilder().AddEmbed(logEmbed));
                    }
                    else if (logChannel is null)
                    {
                        await ctx.Channel.SendMessageAsync("Não consegui encontrar o canal de logs (ID inválido ou canal de outro servidor?). Verifique o `enlistLogChannelId` no config.json.");
                    }
                    else
                    {
                        await ctx.Channel.SendMessageAsync("O canal de logs configurado pertence a outro servidor. Verifique o `enlistLogChannelId` no config.json.");
                    }
                }
                catch (Exception ex)
                {
                    var err = ex.Message ?? "";
                    if (err.Length > 150) err = err[..147] + "...";
                    await ctx.Channel.SendMessageAsync($"Erro ao enviar log no canal de logs: **{err}**. Verifique se o ID do canal está correto e se o bot tem permissão **Ver canal** e **Enviar mensagens** nesse canal.");
                }
            }

            if (config.enlistWelcomeChannelId.HasValue && config.enlistWelcomeChannelId.Value != 0)
            {
                try
                {
                    var welcomeChannel = await ctx.Client.GetChannelAsync(config.enlistWelcomeChannelId.Value);
                    if (welcomeChannel is not null && welcomeChannel.GuildId == ctx.Guild.Id)
                    {
                        await welcomeChannel.SendMessageAsync($"Bem-vindo ao 12°, {targetMember.Mention}!");
                    }
                    else if (welcomeChannel is null)
                    {
                        await ctx.Channel.SendMessageAsync("Não consegui encontrar o canal de boas-vindas (ID inválido ou canal de outro servidor?). Verifique o `enlistWelcomeChannelId` no config.json.");
                    }
                    else
                    {
                        await ctx.Channel.SendMessageAsync("O canal de boas-vindas configurado pertence a outro servidor. Verifique o `enlistWelcomeChannelId` no config.json.");
                    }
                }
                catch (Exception ex)
                {
                    var err = ex.Message ?? "";
                    if (err.Length > 150) err = err[..147] + "...";
                    await ctx.Channel.SendMessageAsync($"Erro ao enviar boas-vindas no canal geral: **{err}**. Verifique se o ID do canal está correto e se o bot tem permissão **Ver canal** e **Enviar mensagens** nesse canal.");
                }
            }

            var successDescription = badgesAvailable
                ? "Bem vindo ao 12°!"
                : "Bem vindo ao 12°! (Badges indisponíveis no momento.)";

            var successEmbed = new DiscordEmbedBuilder()
                .WithTitle("12° - Usuário alistado com sucesso")
                .WithDescription(successDescription)
                .WithColor(DiscordColor.Green)
                .WithThumbnail(targetMember.GetAvatarUrl(MediaFormat.Auto))
                .WithFooter("Confirmação de alistamento ROBLOX", ctx.Client.CurrentUser.AvatarUrl)
                .WithTimestamp(DateTimeOffset.UtcNow);

            await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(successEmbed));
        }
    }
}
