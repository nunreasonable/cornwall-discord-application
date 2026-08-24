using System;
using System.Collections.Generic;
using System.Linq;
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

        /// <summary>
        /// Cooldown por usuario. Sem ele, um membro repetia /alistar-se em laco e
        /// martelava a API do ROBLOX (uma consulta de perfil + amigos por
        /// execucao) - o alvo classico de rate limit e de banimento de IP.
        /// </summary>
        private static readonly TimeSpan s_enlistCooldown = TimeSpan.FromSeconds(30);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, DateTimeOffset> s_lastEnlistAttempt = new();

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

            var now = DateTimeOffset.UtcNow;
            if (s_lastEnlistAttempt.TryGetValue(ctx.User.Id, out var last) && now - last < s_enlistCooldown)
            {
                var wait = s_enlistCooldown - (now - last);
                await DenyAsync(ctx, "Aguarde um momento",
                    $"Você iniciou um alistamento há pouco. Tente de novo em {Math.Ceiling(wait.TotalSeconds)}s.");
                return;
            }
            s_lastEnlistAttempt[ctx.User.Id] = now;

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

            // Verificacao ROBLOX, cargos, apelido e log saem todos do
            // EnlistmentService - o mesmo caminho de /enlistuser e de
            // POST /api/enlist.
            //
            // Ate agosto/2026 este comando trazia sua propria copia de ~270
            // linhas disso, e ela ja tinha divergido: nao conferia se o bot tem
            // ManageRoles/ManageNicknames, nao comparava a hierarquia de cargos
            // antes de conceder, e engolia falha de GrantRoleAsync com catch {}
            // vazio - um alistamento que nao concedeu cargo nenhum terminava
            // dizendo "Alistamento concluido".
            var check = await EnlistmentService.VerifyRobloxAsync(robloxName);
            if (!check.Ok)
            {
                await FailAsync("Erro ao consultar ROBLOX", check.Error ?? "Falha desconhecida.");
                return;
            }

            if (!check.IsLikelyMain)
            {
                await ReplyAsync(new DiscordEmbedBuilder()
                    .WithTitle("Alistamento negado - Conta provavelmente ALT")
                    .WithDescription("A conta ROBLOX fornecida não atende aos critérios mínimos de confiabilidade.")
                    .WithColor(DiscordColor.IndianRed)
                    .AddField(new DiscordEmbedField("Idade da conta", $"{check.AccountAge.Days} dias", true))
                    .AddField(new DiscordEmbedField("Amigos", check.FriendsCount.ToString(), true))
                    .AddField(new DiscordEmbedField("Badges (bônus)", check.BadgesDisplay, true)));
                return;
            }

            // ctx.Channel como escopo: e nele que o bot precisa das permissoes.
            var applied = await EnlistmentService.ApplyAsync(
                ctx.Client, ctx.Guild, targetMember, config, wantsSocialRole, ctx.Channel);

            var logEmbed = EnlistmentService.BuildLogEmbed(
                ctx.Client,
                targetMember,
                ctx.User.Mention,
                robloxName,
                check,
                applied,
                wantsSocialRole,
                title: "12° Regiment - Recruit Log (ROBLOX)",
                description: "Registro de alistamento realizado com verificação automática de conta ROBLOX.",
                extraFields: new[]
                {
                    ("Idioma", languageAnswer),
                    ("Pertence a outros grupos?", groupsAnswer),
                    ("Quem recrutou?", recruiterAnswer)
                });

            var warnings = new List<string>(applied.Warnings);
            warnings.AddRange(await EnlistmentService.AnnounceAsync(
                ctx.Client, ctx.Guild, config, logEmbed, targetMember));

            // Aviso nao vira excecao, mas tambem nao some: o alistamento pode ter
            // terminado sem conceder cargo nenhum, e quem se alistou precisa saber
            // disso em vez de ver so "concluido".
            var successEmbed = new DiscordEmbedBuilder()
                .WithTitle(warnings.Count == 0 ? "Alistamento concluido" : "Alistamento concluido com avisos")
                .WithDescription("Verificacao ROBLOX aprovada. Bem-vindo ao 12°.")
                .WithColor(warnings.Count == 0 ? DiscordColor.Green : DiscordColor.Orange);

            if (warnings.Count > 0)
            {
                successEmbed.AddField(new DiscordEmbedField(
                    "Avisos",
                    AuditEmbeds.FieldValue(warnings),
                    false));
            }

            await ReplyAsync(successEmbed);
        }
    }
}
