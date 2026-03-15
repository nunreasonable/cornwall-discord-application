using System;
using System.Collections.Generic;
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
            [Option("roblox_username", "Username do ROBLOX do usuário")] string robloxUsername)
        {
            await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);

            var config = new JSONReader();
            await config.ReadJSON();

            if (ctx.Guild == null)
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

            int badgeCount;
            int friendsCount;
            TimeSpan accountAge;
            long robloxUserId;

            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromSeconds(10);

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
                    var createdStr = (string?)userInfoJson["created"];
                    if (createdStr == null || !DateTime.TryParse(createdStr, out var createdAt))
                    {
                        var errEmbed = new DiscordEmbedBuilder()
                            .WithTitle("Erro ao ler data de criação")
                            .WithDescription("Não foi possível determinar a data de criação da conta ROBLOX.")
                            .WithColor(DiscordColor.IndianRed);

                        await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(errEmbed));
                        return;
                    }

                    accountAge = DateTime.UtcNow - createdAt.ToUniversalTime();

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

                    // Badges
                    var badgesResponse = await http.GetAsync($"https://badges.roblox.com/v1/users/{robloxUserId}/badges?limit=100&sortOrder=Asc");
                    if (!badgesResponse.IsSuccessStatusCode)
                    {
                        var errEmbed = new DiscordEmbedBuilder()
                            .WithTitle("Erro ao consultar badges ROBLOX")
                            .WithDescription("Não foi possível obter as badges da conta ROBLOX.")
                            .WithColor(DiscordColor.IndianRed);

                        await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(errEmbed));
                        return;
                    }

                    var badgesJson = JObject.Parse(await badgesResponse.Content.ReadAsStringAsync());
                    var dataArray = badgesJson["data"] as JArray;
                    badgeCount = dataArray?.Count ?? 0;
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
                    .AddField(new DiscordEmbedField("Badges (bônus)", badgeCount.ToString(), true));

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(deniedEmbed));
                return;
            }

            // Adiciona cargos ao membro (ignorando cargos que ele já possui)
            var addedRoles = new List<DiscordRole>();
            if (config.enlistTargetRoleIds != null && config.enlistTargetRoleIds.Length > 0)
            {
                foreach (var roleId in config.enlistTargetRoleIds)
                {
                    if (!ctx.Guild.Roles.TryGetValue(roleId, out var role))
                        continue;

                    // Ignora cargos que o membro já possui
                    if (targetMember.Roles.Any(r => r.Id == roleId))
                        continue;

                    try
                    {
                        await targetMember.GrantRoleAsync(role, "Alistamento via comando");
                        addedRoles.Add(role);
                    }
                    catch
                    {
                        // Ignore failures for specific roles to continue with others
                    }
                }
            }

            // Atualiza nickname adicionando o prefixo [32nd] se ainda não existir
            var currentNick = targetMember.Nickname ?? targetMember.Username;
            const string prefix = "[32nd]";
            if (!currentNick.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var newNick = $"{prefix} {currentNick}";
                try
                {
                    await targetMember.ModifyAsync(m => m.Nickname = newNick);
                }
                catch
                {
                    // Caso não seja possível alterar o nickname, ignore.
                }
            }

            // Envia log para canal específico (busca o canal na API do Discord para não depender do cache)
            if (config.enlistLogChannelId.HasValue)
            {
                try
                {
                    var logChannel = await ctx.Client.GetChannelAsync(config.enlistLogChannelId.Value);
                    if (logChannel != null && logChannel.GuildId == ctx.Guild.Id)
                    {
                        var logEmbed = new DiscordEmbedBuilder()
                            .WithTitle("32nd Regiment - Recruit Log")
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
                            .AddField(new DiscordEmbedField("Badges", badgeCount.ToString(), true))
                            .AddField(new DiscordEmbedField("Cargos adicionados", addedRoles.Count > 0 ? string.Join(", ", addedRoles.Select(r => r.Mention)) : "Nenhum", false))
                            .AddField(new DiscordEmbedField("Verificação de alt", "Automática (aprovado)", true));

                        await logChannel.SendMessageAsync(new DiscordMessageBuilder().AddEmbed(logEmbed));
                    }
                    else if (logChannel == null)
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

            var successEmbed = new DiscordEmbedBuilder()
                .WithTitle("32nd Regiment - Alistamento bem-sucedido (ROBLOX)")
                .WithDescription($"O usuário **{user.Username}** foi alistado com sucesso após passar na verificação automática da sua conta ROBLOX.")
                .WithColor(DiscordColor.Green)
                .WithThumbnail(targetMember.GetAvatarUrl(MediaFormat.Auto))
                .WithFooter("Confirmação de alistamento ROBLOX", ctx.Client.CurrentUser.AvatarUrl)
                .WithTimestamp(DateTimeOffset.UtcNow)
                .AddField(new DiscordEmbedField("Nome no ROBLOX", robloxName, true))
                .AddField(new DiscordEmbedField("Cargos adicionados", addedRoles.Count > 0 ? string.Join(", ", addedRoles.Select(r => r.Name)) : "Nenhum", true))
                .AddField(new DiscordEmbedField("ROBLOX - idade da conta (dias)", accountAge.Days.ToString(), true))
                .AddField(new DiscordEmbedField("ROBLOX - amigos", friendsCount.ToString(), true))
                .AddField(new DiscordEmbedField("ROBLOX - badges", badgeCount.ToString(), true));

            await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(successEmbed));
        }
    }
}
