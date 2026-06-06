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
    internal class RobloxEnlist : ApplicationCommandsModule
    {
        [SlashCommand("alistar-se", "Aliste-se usando verificação automática de conta ROBLOX + formulário.")]
        public async Task RobloxEnlistCommand(InteractionContext ctx)
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

            var interactivity = ctx.Client.GetInteractivity();

            DiscordDmChannel dmChannel;
            var formEmbed = new DiscordEmbedBuilder()
                .WithTitle("Formulário de Alistamento - 12° Regiment")
                .WithDescription($"{ctx.User.Mention}, por favor, preencha o formulário respondendo às perguntas abaixo:")
                .WithColor(DiscordColor.Blurple);

            try
            {
                dmChannel = await ctx.User.CreateDmChannelAsync();
                await dmChannel.SendMessageAsync(new DiscordMessageBuilder().AddEmbed(formEmbed));
            }
            catch (DisCatSharp.Exceptions.UnauthorizedException)
            {
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().WithContent("Could not reach your DMs please enable them."));
                return;
            }
            catch (DisCatSharp.Exceptions.NotFoundException)
            {
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().WithContent("Could not reach your DMs please enable them."));
                return;
            }
            catch (DisCatSharp.Exceptions.BadRequestException)
            {
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().WithContent("Could not reach your DMs please enable them."));
                return;
            }
            await ctx.EditResponseAsync(new DiscordWebhookBuilder()
                .WithContent("Enviei o formulário por DM. Responda às perguntas por lá."));

            async Task<string?> AskDmQuestionAsync(string title, string description, TimeSpan timeout)
            {
                var questionEmbed = new DiscordEmbedBuilder()
                    .WithTitle(title)
                    .WithDescription(description)
                    .WithColor(DiscordColor.Blurple);

                await dmChannel.SendMessageAsync(new DiscordMessageBuilder().AddEmbed(questionEmbed));

                var response = await interactivity.WaitForMessageAsync(
                    m => m.Author.Id == ctx.User.Id && m.Channel.Id == dmChannel.Id,
                    timeout);

                if (response.TimedOut || response.Result is null || string.IsNullOrWhiteSpace(response.Result.Content))
                {
                    return null;
                }

                return response.Result.Content.Trim();
            }

            static bool? ParseYesNo(string input)
            {
                var normalized = input.Trim().ToLowerInvariant();
                if (normalized == "sim" || normalized == "s")
                    return true;
                if (normalized == "não" || normalized == "nao" || normalized == "n")
                    return false;
                return null;
            }

            // Pergunta 1: Nome no Roblox
            var robloxName = await AskDmQuestionAsync(
                "1️⃣ Nome no Roblox",
                "Digite seu nome de usuário no Roblox:",
                TimeSpan.FromMinutes(2));

            if (robloxName is null)
            {
                var timeoutName = new DiscordEmbedBuilder()
                    .WithTitle("Tempo esgotado")
                    .WithDescription("Nome no Roblox não fornecido a tempo. Execute o comando novamente quando estiver pronto.")
                    .WithColor(DiscordColor.IndianRed);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(timeoutName));
                return;
            }

            // Pergunta 2: Nacionalidade
            var languageRaw = await AskDmQuestionAsync(
                "2️⃣ Nacionalidade",
                "Responda com `português` ou `brasileiro`:",
                TimeSpan.FromMinutes(2));

            if (languageRaw is null)
            {
                var timeoutLang = new DiscordEmbedBuilder()
                    .WithTitle("Tempo esgotado")
                    .WithDescription("Nacionalidade não fornecida a tempo. Execute o comando novamente quando estiver pronto.")
                    .WithColor(DiscordColor.IndianRed);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(timeoutLang));
                return;
            }

            var languageNormalized = languageRaw.Trim().ToLowerInvariant();
            string languageAnswer;
            if (languageNormalized.StartsWith("port"))
            {
                languageAnswer = "Português 🇵🇹";
            }
            else if (languageNormalized.StartsWith("bra"))
            {
                languageAnswer = "Brasileiro 🇧🇷";
            }
            else
            {
                await dmChannel.SendMessageAsync("Resposta inválida. Use `português` ou `brasileiro` e execute o comando novamente.");
                var invalidLang = new DiscordEmbedBuilder()
                    .WithTitle("Resposta inválida")
                    .WithDescription("Nacionalidade inválida. Execute o comando novamente e responda com `português` ou `brasileiro`.")
                    .WithColor(DiscordColor.IndianRed);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(invalidLang));
                return;
            }

            // Pergunta 3: Pendendo aos grupos
            var groupsRaw = await AskDmQuestionAsync(
                "3️⃣ Pendendo aos grupos?",
                "Responda com `sim` ou `não`:",
                TimeSpan.FromMinutes(2));

            if (groupsRaw is null)
            {
                var timeoutGroups = new DiscordEmbedBuilder()
                    .WithTitle("Tempo esgotado")
                    .WithDescription("Resposta sobre grupos não fornecida a tempo. Execute o comando novamente quando estiver pronto.")
                    .WithColor(DiscordColor.IndianRed);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(timeoutGroups));
                return;
            }

            var groupsParsed = ParseYesNo(groupsRaw);
            if (!groupsParsed.HasValue)
            {
                await dmChannel.SendMessageAsync("Resposta inválida. Use `sim` ou `não` e execute o comando novamente.");
                var invalidGroups = new DiscordEmbedBuilder()
                    .WithTitle("Resposta inválida")
                    .WithDescription("Resposta sobre grupos inválida. Execute o comando novamente e responda com `sim` ou `não`.")
                    .WithColor(DiscordColor.IndianRed);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(invalidGroups));
                return;
            }

            var groupsAnswer = groupsParsed.Value ? "Sim" : "Não";

            // Pergunta 4: Quem recrutou (opcional)
            var recruiterResponse = await AskDmQuestionAsync(
                "4️⃣ Quem te recrutou?",
                "Digite o nome de quem te recrutou (ou digite 'não sei' para pular):",
                TimeSpan.FromMinutes(2));

            string recruiterAnswer = string.Empty;
            if (!string.IsNullOrWhiteSpace(recruiterResponse))
            {
                var recruiterNormalized = recruiterResponse.Trim().ToLowerInvariant();
                if (recruiterNormalized != "não sei" && recruiterNormalized != "nao sei")
                {
                    recruiterAnswer = recruiterResponse.Trim();
                }
            }

            // Pergunta 5: Cargo social
            var socialRoleRaw = await AskDmQuestionAsync(
                "5️⃣ Cargo social?",
                "Responda com `sim` ou `não`:",
                TimeSpan.FromMinutes(2));

            if (socialRoleRaw is null)
            {
                var timeoutSocial = new DiscordEmbedBuilder()
                    .WithTitle("Tempo esgotado")
                    .WithDescription("Resposta sobre cargo social não fornecida a tempo. Execute o comando novamente quando estiver pronto.")
                    .WithColor(DiscordColor.IndianRed);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(timeoutSocial));
                return;
            }

            var socialParsed = ParseYesNo(socialRoleRaw);
            if (!socialParsed.HasValue)
            {
                await dmChannel.SendMessageAsync("Resposta inválida. Use `sim` ou `não` e execute o comando novamente.");
                var invalidSocial = new DiscordEmbedBuilder()
                    .WithTitle("Resposta inválida")
                    .WithDescription("Resposta sobre cargo social inválida. Execute o comando novamente e responda com `sim` ou `não`.")
                    .WithColor(DiscordColor.IndianRed);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(invalidSocial));
                return;
            }

            var wantsSocialRole = socialParsed.Value;
            var socialRoleAnswer = wantsSocialRole ? "Sim" : "Não";

            // Confirmação final
            var confirmEmbed = new DiscordEmbedBuilder()
                .WithTitle("✅ Formulário Completo")
                .WithDescription("Obrigado! Seu formulário foi preenchido. Processando suas informações...")
                .WithColor(DiscordColor.Green)
                .AddField(new DiscordEmbedField("Nome no Roblox", robloxName))
                .AddField(new DiscordEmbedField("Nacionalidade", languageAnswer))
                .AddField(new DiscordEmbedField("Pendendo aos grupos?", groupsAnswer))
                .AddField(new DiscordEmbedField("Quem recrutou?", string.IsNullOrWhiteSpace(recruiterAnswer) ? "Não informado" : recruiterAnswer))
                .AddField(new DiscordEmbedField("Cargo social?", socialRoleAnswer));

            await dmChannel.SendMessageAsync(new DiscordMessageBuilder().AddEmbed(confirmEmbed));

            
            int badgeCount= 0;
            int friendsCount;
            TimeSpan accountAge;
            long robloxUserId;
            bool badgesAvailable = true;

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

                    // Badges (opcional)
                    try
                    {
                        var badgesResponse = await http.GetAsync($"https://badges.roblox.com/v1/users/{robloxUserId}/badges?limit=100&sortOrder=Asc");
                        if (!badgesResponse.IsSuccessStatusCode)
                        {
                            badgesAvailable = false;
                        }
                        else
                        
                        {
                            var badgesJson = JObject.Parse(await badgesResponse.Content.ReadAsStringAsync());
                            var dataArray = badgesJson["data"] as JArray;
                            badgeCount = dataArray?.Count ?? 0;
                        }
                    }
                    catch
                    {
                        badgesAvailable = false;
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
            var hasBadgeBonus = badgesAvailable && badgeCount >= minBadgesForBonus;
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

            if (wantsSocialRole)
            {
                if (!config.enlistSocialRoleId.HasValue || config.enlistSocialRoleId.Value == 0)
                {
                    await ctx.Channel.SendMessageAsync("⚠️ **Aviso**: O cargo social não está configurado. Verifique o `enlistSocialRoleId` no config.jsonc.");
                }
                else if (!ctx.Guild.Roles.TryGetValue(config.enlistSocialRoleId.Value, out var socialRoleEntity))
                {
                    await ctx.Channel.SendMessageAsync("⚠️ **Aviso**: O cargo social configurado não foi encontrado no servidor. Verifique o `enlistSocialRoleId` no config.jsonc.");
                }
                else if (!targetMember.Roles.Any(r => r.Id == socialRoleEntity.Id))
                {
                    try
                    {
                        await targetMember.GrantRoleAsync(socialRoleEntity, "Cargo social via robloxenlist");
                        addedRoles.Add(socialRoleEntity);
                    }
                    catch (Exception ex)
                    {
                        var err = ex.Message ?? "";
                        if (err.Length > 150) err = err[..147] + "...";
                        await ctx.Channel.SendMessageAsync($"⚠️ **Aviso**: Falha ao adicionar o cargo social. Erro: {err}");
                    }
                }
            }

            // Atualiza nickname adicionando o prefixo [12°] se ainda não existir
            var currentNick = targetMember.Nickname ?? targetMember.Username;
            const string prefix = "[12°]";
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
                    if (logChannel is not null && logChannel.GuildId == ctx.Guild.Id)
                    {
                        var logEmbed = new DiscordEmbedBuilder()
                            .WithTitle("12° Regiment - Recruit Log (ROBLOX)")
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
                            .AddField(new DiscordEmbedField("Cargo social?", string.IsNullOrWhiteSpace(socialRoleAnswer) ? "N/A" : socialRoleAnswer, true))
                            .AddField(new DiscordEmbedField("Idade da conta (dias)", accountAge.Days.ToString(), true))
                            .AddField(new DiscordEmbedField("Amigos", friendsCount.ToString(), true))
                            .AddField(new DiscordEmbedField("Badges", badgesDisplay, true))
                            .AddField(new DiscordEmbedField("Cargos adicionados", addedRoles.Count > 0 ? string.Join(", ", addedRoles.Select(r => r.Mention)) : "Nenhum", false))
                            .AddField(new DiscordEmbedField("Verificação de alt", "Automática (aprovado)", true));

                        await logChannel.SendMessageAsync(new DiscordMessageBuilder().AddEmbed(logEmbed));
                    }
                }
                catch
                {
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

            var successEmbed = new DiscordEmbedBuilder()
                .WithTitle("Alistamento concluido")
                .WithDescription("Verificacao ROBLOX aprovada. Bem-vindo ao 12°.")
                .WithColor(DiscordColor.Green);

            await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(successEmbed));
        }
    }
}
