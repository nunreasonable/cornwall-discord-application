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
using Newtonsoft.Json.Linq;

namespace CornwallUtilities.commands
{
    internal class RobloxEnlist : ApplicationCommandsModule
    {
        [SlashCommand("robloxenlist", "Aliste-se usando verificação automática de conta ROBLOX + formulário.")]
        public async Task RobloxEnlistCommand(InteractionContext ctx)
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

            // Canal específico de alistamento
            if (!config.robloxEnlistChannelId.HasValue || config.robloxEnlistChannelId.Value == 0)
            {
                var missingConfig = new DiscordEmbedBuilder()
                    .WithTitle("Configuração inválida")
                    .WithDescription("O ID do canal de alistamento ROBLOX não está configurado. Verifique o arquivo config.json (robloxEnlistChannelId).")
                    .WithColor(DiscordColor.IndianRed);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(missingConfig));
                return;
            }

            if (ctx.Channel.Id != config.robloxEnlistChannelId.Value)
            {
                var wrongChannel = new DiscordEmbedBuilder()
                    .WithTitle("Canal incorreto")
                    .WithDescription("Este comando só pode ser utilizado no canal de alistamento.")
                    .WithColor(DiscordColor.IndianRed);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(wrongChannel));
                return;
            }

            DiscordMember targetMember = ctx.Member!;

            // Verifica se o usuário já possui algum dos cargos bloqueados
            if (config.robloxEnlistBlockedRoleIds != null && config.robloxEnlistBlockedRoleIds.Length > 0)
            {
                var hasBlockedRole = targetMember.Roles.Any(r => config.robloxEnlistBlockedRoleIds.Contains(r.Id));
                if (hasBlockedRole)
                {
                    var blockedEmbed = new DiscordEmbedBuilder()
                        .WithTitle("Alistamento negado")
                        .WithDescription("Este usuário já está alistado em algum outro regimento, favor redirecionar-se ao canal #transfer-office para solicitar a transferência.")
                        .WithColor(DiscordColor.IndianRed);

                    await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(blockedEmbed));
                    return;
                }
            }

            // Pede para o próprio usuário responder o formulário no formato especificado
            var questionsEmbed = new DiscordEmbedBuilder()
                .WithTitle("Formulário de Alistamento - 32nd Regiment")
                .WithDescription(
                    $"{ctx.User.Mention}, responda **nesta mensagem** seguindo exatamente o formato abaixo:\n\n" +
                    "Nome no Roblox:\n" +
                    "Português 🇵🇹 / Brasileiro 🇧🇷 ?: \n" +
                    "Pendendo aos grupos?: S/N\n" +
                    "Quem te recrutou?:")
                .WithColor(DiscordColor.Blurple);

            await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(questionsEmbed));

            var interactivity = ctx.Client.GetInteractivity();
            var response = await interactivity.WaitForMessageAsync(
                m => m.Author.Id == ctx.User.Id && m.Channel.Id == ctx.Channel.Id,
                TimeSpan.FromMinutes(5));

            if (response.TimedOut)
            {
                var timeoutForm = new DiscordEmbedBuilder()
                    .WithTitle("Tempo esgotado")
                    .WithDescription("Nenhuma resposta ao formulário foi recebida a tempo. Execute o comando novamente quando estiver pronto.")
                    .WithColor(DiscordColor.IndianRed);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(timeoutForm));
                return;
            }

            var formContent = response.Result.Content ?? string.Empty;
            var lines = formContent.Split('\n');

            string robloxName = string.Empty;
            string languageAnswer = string.Empty;
            string groupsAnswer = string.Empty;
            string recruiterAnswer = string.Empty;

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                    continue;

                if (line.StartsWith("Nome no Roblox", StringComparison.OrdinalIgnoreCase))
                {
                    var idx = line.IndexOf(':');
                    if (idx >= 0 && idx < line.Length - 1)
                        robloxName = line[(idx + 1)..].Trim();
                }
                else if (line.StartsWith("Português", StringComparison.OrdinalIgnoreCase))
                {
                    var idx = line.IndexOf(':');
                    if (idx >= 0 && idx < line.Length - 1)
                        languageAnswer = line[(idx + 1)..].Trim();
                }
                else if (line.StartsWith("Pendendo aos grupos", StringComparison.OrdinalIgnoreCase))
                {
                    var idx = line.IndexOf(':');
                    if (idx >= 0 && idx < line.Length - 1)
                        groupsAnswer = line[(idx + 1)..].Trim();
                }
                else if (line.StartsWith("Quem te recrutou", StringComparison.OrdinalIgnoreCase))
                {
                    var idx = line.IndexOf(':');
                    if (idx >= 0 && idx < line.Length - 1)
                        recruiterAnswer = line[(idx + 1)..].Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(robloxName))
            {
                var invalidForm = new DiscordEmbedBuilder()
                    .WithTitle("Formulário inválido")
                    .WithDescription("Não foi possível encontrar o campo **\"Nome no Roblox:\"** na sua resposta. Execute o comando novamente e siga exatamente o formato solicitado.")
                    .WithColor(DiscordColor.IndianRed);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(invalidForm));
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
                        var dataArrayLookup = usernameJson["data"] as JArray;
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
                    .AddField("Idade da conta", $"{accountAge.Days} dias", true)
                    .AddField("Amigos", friendsCount.ToString(), true)
                    .AddField("Badges (bônus)", badgeCount.ToString(), true);

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

                    if (targetMember.Roles.Any(r => r.Id == roleId))
                        continue;

                    try
                    {
                        await targetMember.GrantRoleAsync(role, "Alistamento via robloxenlist");
                        addedRoles.Add(role);
                    }
                    catch
                    {
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
                }
            }

            // Log no canal de logs (reutiliza enlistLogChannelId)
            if (config.enlistLogChannelId.HasValue)
            {
                try
                {
                    var logChannel = await ctx.Client.GetChannelAsync(config.enlistLogChannelId.Value);
                    if (logChannel != null && logChannel.GuildId == ctx.Guild.Id)
                    {
                        var logEmbed = new DiscordEmbedBuilder()
                            .WithTitle("32nd Regiment - Recruit Log (ROBLOX)")
                            .WithDescription("Registro de alistamento realizado com verificação automática de conta ROBLOX.")
                            .WithColor(DiscordColor.Blurple)
                            .WithThumbnail(targetMember.GetAvatarUrl(ImageFormat.Auto))
                            .WithFooter("Recruit log gerado por CornwallBot", ctx.Client.CurrentUser.AvatarUrl)
                            .WithTimestamp(DateTimeOffset.UtcNow)
                            .AddField("Executor / Alistado", ctx.User.Mention, true)
                            .AddField("Nome no ROBLOX", robloxName, true)
                            .AddField("ROBLOX ID", robloxUserId.ToString(), true)
                            .AddField("Idioma", string.IsNullOrWhiteSpace(languageAnswer) ? "N/A" : languageAnswer, true)
                            .AddField("Pendendo aos grupos?", string.IsNullOrWhiteSpace(groupsAnswer) ? "N/A" : groupsAnswer, true)
                            .AddField("Quem recrutou?", string.IsNullOrWhiteSpace(recruiterAnswer) ? "N/A" : recruiterAnswer, true)
                            .AddField("Idade da conta (dias)", accountAge.Days.ToString(), true)
                            .AddField("Amigos", friendsCount.ToString(), true)
                            .AddField("Badges", badgeCount.ToString(), true)
                            .AddField("Cargos adicionados", addedRoles.Count > 0 ? string.Join(", ", addedRoles.Select(r => r.Mention)) : "Nenhum", false)
                            .AddField("Verificação de alt", "Automática (aprovado)", true);

                        await logChannel.SendMessageAsync(new DiscordMessageBuilder().AddEmbed(logEmbed));
                    }
                }
                catch
                {
                }
            }

            var successEmbed = new DiscordEmbedBuilder()
                .WithTitle("32nd Regiment - Alistamento bem-sucedido (ROBLOX)")
                .WithDescription($"Você foi alistado com sucesso após passar na verificação automática da sua conta ROBLOX.")
                .WithColor(DiscordColor.Green)
                .WithThumbnail(targetMember.GetAvatarUrl(ImageFormat.Auto))
                .WithFooter("Confirmação de alistamento ROBLOX", ctx.Client.CurrentUser.AvatarUrl)
                .WithTimestamp(DateTimeOffset.UtcNow)
                .AddField("Nome no ROBLOX", robloxName, true)
                .AddField("Cargos adicionados", addedRoles.Count > 0 ? string.Join(", ", addedRoles.Select(r => r.Name)) : "Nenhum", true)
                .AddField("ROBLOX - idade da conta (dias)", accountAge.Days.ToString(), true)
                .AddField("ROBLOX - amigos", friendsCount.ToString(), true)
                .AddField("ROBLOX - badges", badgeCount.ToString(), true);

            await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(successEmbed));
        }
    }
}

