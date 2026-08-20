using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using CornwallUtilities.config;
using CornwallUtilities.Services;
using CornwallUtilities.Services.Audit;
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
        /// <summary>
        /// Recusa antes do modal.
        ///
        /// Aqui a interacao ainda nao foi respondida, entao a saida e uma resposta
        /// direta e nao um EditResponse: o comando nao defere mais no inicio,
        /// porque deferir impediria o modal de ser exibido.
        /// </summary>
        private static Task DenyAsync(InteractionContext ctx, string title, string description) =>
            ctx.CreateResponseAsync(InteractionResponseType.ChannelMessageWithSource,
                new DiscordInteractionResponseBuilder()
                    .AddEmbed(new DiscordEmbedBuilder()
                        .WithTitle(title)
                        .WithDescription(description)
                        .WithColor(DiscordColor.IndianRed))
                    .AsEphemeral());

        /// <summary>Valores estaveis das opcoes; o rotulo exibido pode mudar sem mexer no codigo.</summary>
        private const string Portugues = "pt";
        private const string Brasileiro = "br";
        private const string Sim = "sim";
        private const string Nao = "nao";

        [SlashCommand("alistar-se", "Aliste-se usando verificação automática de conta ROBLOX + formulário.")]
        public async Task RobloxEnlistCommand(InteractionContext ctx)
        {
            /*
             * Nada de deferir aqui.
             *
             * Um modal precisa ser a PRIMEIRA resposta da interacao - depois de um
             * DeferredChannelMessageWithSource nao da mais para exibi-lo. Por isso
             * todas as validacoes rodam antes de qualquer resposta. Todas leem
             * cache (ctx.Guild, ctx.Channel, targetMember.Roles) e o config e
             * cacheado por carimbo de modificacao, entao cabem nos 3 segundos que
             * o Discord da para responder. E a mesma forma do /audit-add.
             */
            var config = new JSONReader();
            await config.ReadJSON();

            if (ctx.Guild is null)
            {
                await DenyAsync(ctx, "Comando inválido",
                    "Este comando só pode ser executado em um servidor (guild).");
                return;
            }

            // Canal específico de alistamento
            if (!config.robloxEnlistChannelId.HasValue || config.robloxEnlistChannelId.Value == 0)
            {
                await DenyAsync(ctx, "Configuração inválida",
                    "O ID do canal de alistamento ROBLOX não está configurado. Verifique o arquivo config.json (robloxEnlistChannelId).");
                return;
            }

            if (ctx.Channel.Id != config.robloxEnlistChannelId.Value)
            {
                await DenyAsync(ctx, "Canal incorreto",
                    "Este comando só pode ser utilizado no canal de alistamento.");
                return;
            }

            DiscordMember targetMember = ctx.Member!;

            // Verifica se o usuário já possui algum dos cargos bloqueados
            if (config.robloxEnlistBlockedRoleIds != null && config.robloxEnlistBlockedRoleIds.Length > 0)
            {
                var hasBlockedRole = targetMember.Roles.Any(r => config.robloxEnlistBlockedRoleIds.Contains(r.Id));
                if (hasBlockedRole)
                {
                    await DenyAsync(ctx, "Alistamento negado",
                        "Este usuário já está alistado em algum outro regimento, favor redirecionar-se ao canal <#1397974742228533322> para solicitar a transferência.");
                    return;
                }
            }

            /*
             * O formulario inteiro num modal so.
             *
             * Antes eram cinco perguntas por DM, uma de cada vez, com dois minutos
             * de espera cada. Isso barrava quem tinha DM fechada e recusava
             * resposta livre que fugisse do esperado - digitar "pt" em vez de
             * "portugues" cancelava o alistamento inteiro.
             *
             * Sao exatamente cinco perguntas, e o modal do Discord aceita
             * exatamente cinco componentes: cabe justo. As tres perguntas fechadas
             * viram radio, entao nao existe mais resposta invalida nelas.
             */
            var modalId = $"roblox_enlist:{ctx.User.Id}:{Guid.NewGuid():N}";

            var modal = new DiscordInteractionModalBuilder()
                .WithTitle("Alistamento — 12° Regiment")
                .WithCustomId(modalId)
                .AddLabelComponent(new DiscordLabelComponent(
                        "Nome no Roblox",
                        "Exatamente como aparece na sua conta",
                        null)
                    .WithTextComponent(new DiscordTextInputComponent(
                        TextComponentStyle.Small,
                        customId: "enlist_roblox",
                        placeholder: "Ex.: RafaOdebrecht",
                        minLength: 1,
                        // Teto de nome de usuario do ROBLOX; digitar mais que isso
                        // so levaria a uma consulta que nunca acha ninguem.
                        maxLength: 20,
                        required: true,
                        defaultValue: null)))
                .AddLabelComponent(new DiscordLabelComponent("Nacionalidade", null, null)
                    .WithRadioGroupComponent(new DiscordRadioGroupComponent(
                        new[]
                        {
                            new DiscordRadioGroupComponentOption("Português 🇵🇹", Portugues, null, false),
                            new DiscordRadioGroupComponentOption("Brasileiro 🇧🇷", Brasileiro, null, false)
                        },
                        "enlist_language",
                        true)))
                .AddLabelComponent(new DiscordLabelComponent(
                        "Pertence a outros grupos?",
                        "Outros regimentos ou grupos no ROBLOX",
                        null)
                    .WithRadioGroupComponent(new DiscordRadioGroupComponent(
                        new[]
                        {
                            new DiscordRadioGroupComponentOption("Sim", Sim, null, false),
                            new DiscordRadioGroupComponentOption("Não", Nao, null, false)
                        },
                        "enlist_groups",
                        true)))
                .AddLabelComponent(new DiscordLabelComponent(
                        "Quem te recrutou?",
                        "Opcional — deixe vazio se não souber",
                        null)
                    .WithTextComponent(new DiscordTextInputComponent(
                        TextComponentStyle.Small,
                        customId: "enlist_recruiter",
                        placeholder: "Nome de quem te trouxe",
                        minLength: 0,
                        maxLength: 60,
                        required: false,
                        defaultValue: null)))
                .AddLabelComponent(new DiscordLabelComponent(
                        "Quer o cargo social?",
                        "Acesso aos canais sociais do regimento",
                        null)
                    .WithRadioGroupComponent(new DiscordRadioGroupComponent(
                        new[]
                        {
                            new DiscordRadioGroupComponentOption("Sim", Sim, null, false),
                            new DiscordRadioGroupComponentOption("Não", Nao, null, false)
                        },
                        "enlist_social",
                        true)));

            var interactivity = ctx.Client.GetInteractivity();

            // Registrar o waiter ANTES de exibir o modal fecha a janela de corrida.
            var waiter = interactivity.WaitForModalAsync(modalId, TimeSpan.FromMinutes(10));
            await ctx.CreateModalResponseAsync(modal);

            var response = await waiter;
            if (response.TimedOut)
                return;

            var modalInteraction = response.Result.Interaction;
            await modalInteraction.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource,
                new DiscordInteractionResponseBuilder().AsEphemeral());

            /*
             * As respostas seguem pelo token do MODAL, nao pelo do comando: o ctx
             * ja foi respondido com o modal e nao tem resposta original para
             * editar. Como o fluxo agora acaba em segundos, o InteractionReply
             * .SafeEditAsync - que existia para o caso do token vencer durante o
             * formulario por DM - deixou de ser necessario aqui.
             */
            async Task ReplyAsync(DiscordEmbedBuilder embed) =>
                await modalInteraction.EditOriginalResponseAsync(
                    new DiscordWebhookBuilder().AddEmbed(embed));

            async Task FailAsync(string title, string description)
            {
                await ReplyAsync(new DiscordEmbedBuilder()
                    .WithTitle(title)
                    .WithDescription(description)
                    .WithColor(DiscordColor.IndianRed));
            }

            var robloxName = (ModalUtil.ReadModalValue(modalInteraction, "enlist_roblox") ?? string.Empty).Trim();
            if (robloxName.Length == 0)
            {
                await FailAsync("Nome não informado",
                    "O nome no Roblox é obrigatório. Rode `/alistar-se` de novo.");
                return;
            }

            var languageChoice = ModalUtil.ReadModalSelection(modalInteraction, "enlist_language");
            var groupsChoice = ModalUtil.ReadModalSelection(modalInteraction, "enlist_groups");
            var socialChoice = ModalUtil.ReadModalSelection(modalInteraction, "enlist_social");

            // Os tres radios sao obrigatorios, entao so cai aqui se o Discord
            // mandar algo fora do combinado - vale dizer isso em vez de assumir um
            // padrao silencioso e registrar no log uma resposta que ninguem deu.
            if (languageChoice is null || groupsChoice is null || socialChoice is null)
            {
                await FailAsync("Formulário incompleto",
                    "Alguma das perguntas de escolha voltou sem resposta. Rode `/alistar-se` de novo.");
                return;
            }

            var languageAnswer = languageChoice == Brasileiro ? "Brasileiro 🇧🇷" : "Português 🇵🇹";
            var groupsAnswer = groupsChoice == Sim ? "Sim" : "Não";

            var wantsSocialRole = socialChoice == Sim;
            var socialRoleAnswer = wantsSocialRole ? "Sim" : "Não";

            // Campo opcional de verdade: vazio ja significa "nao informado", sem o
            // "digite 'nao sei' para pular" que o formulario por DM precisava.
            var recruiterAnswer = (ModalUtil.ReadModalValue(modalInteraction, "enlist_recruiter") ?? string.Empty).Trim();

            int badgeCount= 0;
            int friendsCount;
            TimeSpan accountAge;
            long robloxUserId;
            bool badgesAvailable = true;

            // Cliente compartilhado: criar um HttpClient por comando acumula
            // sockets em TIME_WAIT. Nao alterar Timeout aqui - lanca excecao
            // depois do primeiro request.
            {
                var http = HttpClientProvider.Shared;

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

                            await ReplyAsync(errLookup);
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

                            await ReplyAsync(notFound);
                            return;
                        }

                        robloxUserId = (long?)dataArrayLookup[0]?["id"] ?? 0;
                        if (robloxUserId <= 0)
                        {
                            var invalidLookup = new DiscordEmbedBuilder()
                                .WithTitle("Conta ROBLOX inválida")
                                .WithDescription("Não foi possível determinar o ID da conta ROBLOX a partir do nome informado.")
                                .WithColor(DiscordColor.IndianRed);

                            await ReplyAsync(invalidLookup);
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

                        await ReplyAsync(errEmbed);
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

                        await ReplyAsync(errEmbed);
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

                        await ReplyAsync(errEmbed);
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

                    await ReplyAsync(timeoutEmbed);
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

                    await ReplyAsync(genericEmbed);
                    return;
                }
            }

            // Critérios principais para NÃO ser alt
            var minAccountAge = TimeSpan.FromDays(90); // > 3 meses
            const int minFriends = 1;

            var passesAge = accountAge >= minAccountAge;
            var passesFriends = friendsCount >= minFriends;
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

                await ReplyAsync(deniedEmbed);
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
            if (!NicknameUtil.HasPrefix(currentNick))
            {
                // WithPrefix corta o nome quando necessario: o Discord recusa
                // apelido com mais de 32 caracteres.
                var newNick = NicknameUtil.WithPrefix(currentNick);
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

            await ReplyAsync(successEmbed);
        }
    }
}
