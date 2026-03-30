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
    internal class RobloxEnlist : ApplicationCommandsModule
    {
        [SlashCommand("alistar-se", "Aliste-se usando verificação automática de conta ROBLOX + formulário.")]
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
                        .WithDescription("Este usuário já está alistado em algum outro regimento, favor redirecionar-se ao canal <#1397974742228533322> para solicitar a transferência.")
                        .WithColor(DiscordColor.IndianRed);

                    await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(blockedEmbed));
                    return;
                }
            }

            // Cria um formulário interativo com botões
            var formEmbed = new DiscordEmbedBuilder()
                .WithTitle("Formulário de Alistamento - 32nd Regiment")
                .WithDescription($"{ctx.User.Mention}, por favor, preencha o formulário respondendo às perguntas abaixo:")
                .WithColor(DiscordColor.Blurple);

            var formMessage = await ctx.EditResponseAsync(new DiscordWebhookBuilder()
                .AddEmbed(formEmbed));

            var interactivity = ctx.Client.GetInteractivity();

            // Pergunta 1: Nome no Roblox - usando modal com TextInputComponent
            var nameEmbed = new DiscordEmbedBuilder()
                .WithTitle("1️⃣ Nome no Roblox")
                .WithDescription("Por favor, clique no botão abaixo para abrir o formulário de digitação:")
                .WithColor(DiscordColor.Blurple);

            var nameButton = new DiscordButtonComponent(ButtonStyle.Primary, "roblox_name_modal", "📝 Digitar Nome");

            var nameMessage = await ctx.FollowUpAsync(new DiscordFollowupMessageBuilder()
                .AddEmbed(nameEmbed)
                .AddComponents(nameButton));

            var nameButtonResult = await interactivity.WaitForButtonAsync(
                nameMessage,
                TimeSpan.FromMinutes(1)); // Reduzi o timeout para evitar expiração

            if (nameButtonResult.TimedOut)
            {
                var timeoutName = new DiscordEmbedBuilder()
                    .WithTitle("Tempo esgotado")
                    .WithDescription("Nome no Roblox não fornecido a tempo. Execute o comando novamente quando estiver pronto.")
                    .WithColor(DiscordColor.IndianRed);

                await ctx.FollowUpAsync(new DiscordFollowupMessageBuilder().AddEmbed(timeoutName));
                return;
            }

            // Como DisCatSharp não suporta TextInputComponent nativamente, vamos usar uma abordagem alternativa
            // Criamos uma resposta ephemeral que funciona como um "prompt" dentro do embed
            try
            {
                await nameButtonResult.Result.Interaction.CreateResponseAsync(InteractionResponseType.DeferredMessageUpdate);
                // Após defer, enviamos o prompt
                await ctx.FollowUpAsync(new DiscordFollowupMessageBuilder()
                    .WithContent("Por favor, digite seu nome de usuário no Roblox:"));
            }
            catch (DisCatSharp.Exceptions.NotFoundException)
            {
                // Se a interação expirou, tentamos usar follow-up
                await ctx.FollowUpAsync(new DiscordFollowupMessageBuilder()
                    .WithContent("Por favor, digite seu nome de usuário no Roblox:"));
            }
            catch (DisCatSharp.Exceptions.BadRequestException)
            {
                // Se houver bad request, tentamos abordagem mais simples
                await ctx.FollowUpAsync(new DiscordFollowupMessageBuilder()
                    .WithContent("Por favor, digite seu nome de usuário no Roblox:"));
            }

            var nameResponse = await interactivity.WaitForMessageAsync(
                m => m.Author.Id == ctx.User.Id && m.Channel.Id == ctx.Channel.Id,
                TimeSpan.FromMinutes(2));

            if (nameResponse.TimedOut || string.IsNullOrWhiteSpace(nameResponse.Result.Content))
            {
                var timeoutName = new DiscordEmbedBuilder()
                    .WithTitle("Tempo esgotado")
                    .WithDescription("Nome no Roblox não fornecido a tempo. Execute o comando novamente quando estiver pronto.")
                    .WithColor(DiscordColor.IndianRed);

                await ctx.FollowUpAsync(new DiscordFollowupMessageBuilder().AddEmbed(timeoutName));
                return;
            }

            string robloxName = nameResponse.Result.Content.Trim();

            // Pergunta 2: Nacionalidade
            var languageEmbed = new DiscordEmbedBuilder()
                .WithTitle("2️⃣ Nacionalidade")
                .WithDescription("Selecione sua nacionalidade clicando nos botões abaixo:")
                .WithColor(DiscordColor.Blurple);

            var portuguesButton = new DiscordButtonComponent(ButtonStyle.Secondary, "lang_pt", "🇵🇹 Português");
            var brasileiroButton = new DiscordButtonComponent(ButtonStyle.Secondary, "lang_br", "🇧🇷 Brasileiro");

            var langMessage = await ctx.FollowUpAsync(new DiscordFollowupMessageBuilder()
                .AddEmbed(languageEmbed)
                .AddComponents(portuguesButton, brasileiroButton));

            var langResult = await interactivity.WaitForButtonAsync(
                langMessage,
                TimeSpan.FromMinutes(2));

            string languageAnswer = string.Empty;
            if (langResult.TimedOut)
            {
                var timeoutLang = new DiscordEmbedBuilder()
                    .WithTitle("Tempo esgotado")
                    .WithDescription("Nacionalidade não selecionada a tempo. Execute o comando novamente quando estiver pronto.")
                    .WithColor(DiscordColor.IndianRed);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(timeoutLang));
                return;
            }

            languageAnswer = langResult.Result.Id == "lang_pt" ? "Português 🇵🇹" : "Brasileiro 🇧🇷";
            await langResult.Result.Interaction.CreateResponseAsync(InteractionResponseType.DeferredMessageUpdate);

            // Pergunta 3: Pendendo aos grupos
            var groupsEmbed = new DiscordEmbedBuilder()
                .WithTitle("3️⃣ Pendendo aos grupos?")
                .WithDescription("Você está pendendo a outros grupos?")
                .WithColor(DiscordColor.Blurple);

            var simButton = new DiscordButtonComponent(ButtonStyle.Success, "groups_sim", "✅ Sim");
            var naoButton = new DiscordButtonComponent(ButtonStyle.Danger, "groups_nao", "❌ Não");

            var groupsMessage = await ctx.FollowUpAsync(new DiscordFollowupMessageBuilder()
                .AddEmbed(groupsEmbed)
                .AddComponents(simButton, naoButton));

            var groupsResult = await interactivity.WaitForButtonAsync(
                groupsMessage,
                TimeSpan.FromMinutes(2));

            string groupsAnswer = string.Empty;
            if (groupsResult.TimedOut)
            {
                var timeoutGroups = new DiscordEmbedBuilder()
                    .WithTitle("Tempo esgotado")
                    .WithDescription("Resposta sobre grupos não fornecida a tempo. Execute o comando novamente quando estiver pronto.")
                    .WithColor(DiscordColor.IndianRed);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(timeoutGroups));
                return;
            }

            groupsAnswer = groupsResult.Result.Id == "groups_sim" ? "S" : "N";
            await groupsResult.Result.Interaction.CreateResponseAsync(InteractionResponseType.DeferredMessageUpdate);

            // Pergunta 4: Quem recrutou (opcional)
            var recruiterEmbed = new DiscordEmbedBuilder()
                .WithTitle("4️⃣ Quem te recrutou?")
                .WithDescription("Digite o nome de quem te recrutou (ou digite 'não sei' para pular):")
                .WithColor(DiscordColor.Blurple);

            await ctx.FollowUpAsync(new DiscordFollowupMessageBuilder().AddEmbed(recruiterEmbed));

            var recruiterResponse = await interactivity.WaitForMessageAsync(
                m => m.Author.Id == ctx.User.Id && m.Channel.Id == ctx.Channel.Id,
                TimeSpan.FromMinutes(2));

            string recruiterAnswer = string.Empty;
            if (!recruiterResponse.TimedOut && !string.IsNullOrWhiteSpace(recruiterResponse.Result.Content))
            {
                recruiterAnswer = recruiterResponse.Result.Content.Trim();
            }

            // Confirmação final
            var confirmEmbed = new DiscordEmbedBuilder()
                .WithTitle("✅ Formulário Completo")
                .WithDescription("Obrigado! Seu formulário foi preenchido. Processando suas informações...")
                .WithColor(DiscordColor.Green)
                .AddField(new DiscordEmbedField("Nome no Roblox", robloxName))
                .AddField(new DiscordEmbedField("Nacionalidade", languageAnswer))
                .AddField(new DiscordEmbedField("Pendendo aos grupos?", groupsAnswer))
                .AddField(new DiscordEmbedField("Quem recrutou?", string.IsNullOrWhiteSpace(recruiterAnswer) ? "Não informado" : recruiterAnswer));

            await ctx.FollowUpAsync(new DiscordFollowupMessageBuilder().AddEmbed(confirmEmbed));

            
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
                            .WithThumbnail(targetMember.GetAvatarUrl(MediaFormat.Auto))
                            .WithFooter("Recruit log gerado por CornwallBot", ctx.Client.CurrentUser.AvatarUrl)
                            .WithTimestamp(DateTimeOffset.UtcNow)
                            .AddField(new DiscordEmbedField("Executor / Alistado", ctx.User.Mention, true))
                            .AddField(new DiscordEmbedField("Nome no ROBLOX", robloxName, true))
                            .AddField(new DiscordEmbedField("ROBLOX ID", robloxUserId.ToString(), true))
                            .AddField(new DiscordEmbedField("Idioma", string.IsNullOrWhiteSpace(languageAnswer) ? "N/A" : languageAnswer, true))
                            .AddField(new DiscordEmbedField("Pendendo aos grupos?", string.IsNullOrWhiteSpace(groupsAnswer) ? "N/A" : groupsAnswer, true))
                            .AddField(new DiscordEmbedField("Quem recrutou?", string.IsNullOrWhiteSpace(recruiterAnswer) ? "N/A" : recruiterAnswer, true))
                            .AddField(new DiscordEmbedField("Idade da conta (dias)", accountAge.Days.ToString(), true))
                            .AddField(new DiscordEmbedField("Amigos", friendsCount.ToString(), true))
                            .AddField(new DiscordEmbedField("Badges", badgeCount.ToString(), true))
                            .AddField(new DiscordEmbedField("Cargos adicionados", addedRoles.Count > 0 ? string.Join(", ", addedRoles.Select(r => r.Mention)) : "Nenhum", false))
                            .AddField(new DiscordEmbedField("Verificação de alt", "Automática (aprovado)", true));

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

