using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CornwallUtilities.config;
using CornwallUtilities.Services.Audit;
using DisCatSharp;
using DisCatSharp.Entities;
using DisCatSharp.Enums;
using Newtonsoft.Json.Linq;

namespace CornwallUtilities.Services
{
    /// <summary>
    /// Resultado da verificacao da conta ROBLOX.
    ///
    /// <paramref name="Error"/> preenchido significa que a consulta nao pode ser
    /// concluida; <paramref name="IsLikelyMain"/> falso com Error nulo significa
    /// que a consulta funcionou e a conta foi REPROVADA como provavel alt. Sao
    /// situacoes diferentes e o chamador precisa distinguir.
    /// </summary>
    internal sealed record RobloxCheck(
        bool Ok,
        string? Error,
        long RobloxUserId,
        TimeSpan AccountAge,
        int FriendsCount,
        int BadgeCount,
        bool BadgesAvailable,
        bool IsLikelyMain)
    {
        public string BadgesDisplay => BadgesAvailable ? BadgeCount.ToString() : "Indisponível";

        public static RobloxCheck Failure(string error) =>
            new(false, error, 0, TimeSpan.Zero, 0, 0, false, false);
    }

    /// <summary>O que a aplicacao do alistamento conseguiu fazer.</summary>
    internal sealed record EnlistApplyResult(
        List<DiscordRole> AddedRoles,
        bool NicknameChanged,
        List<string> Warnings);

    /// <summary>
    /// Alistamento: verificacao anti-alt no ROBLOX e aplicacao de cargos,
    /// apelido e logs.
    ///
    /// Estava todo dentro do handler de /enlistuser, o que o tornava
    /// inalcancavel pelo dashboard. Os avisos ("o bot nao tem permissao para
    /// gerenciar cargos", "cargo acima da hierarquia do bot") sao devolvidos
    /// como lista em vez de escritos direto no canal - assim o comando continua
    /// publicando-os no canal e a API consegue devolve-los na resposta.
    /// </summary>
    internal static class EnlistmentService
    {
        /// <summary>Idade minima da conta para nao ser tratada como alt.</summary>
        private static readonly TimeSpan MinAccountAge = TimeSpan.FromDays(90);

        private const int MinFriends = 1;

        public static async Task<RobloxCheck> VerifyRobloxAsync(string? robloxUsername)
        {
            var robloxName = robloxUsername?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(robloxName))
                return RobloxCheck.Failure("O username fornecido está vazio.");

            // Cliente compartilhado: criar um HttpClient por chamada acumula
            // sockets em TIME_WAIT. Nao alterar Timeout aqui - lanca excecao
            // depois do primeiro request.
            var http = HttpClientProvider.Shared;

            long robloxUserId;
            TimeSpan accountAge;
            int friendsCount;
            var badgeCount = 0;
            var badgesAvailable = true;

            try
            {
                var lookupPayload = new JObject
                {
                    ["usernames"] = new JArray(robloxName),
                    ["excludeBannedUsers"] = true
                };

                using (var content = new StringContent(lookupPayload.ToString(), Encoding.UTF8, "application/json"))
                {
                    var usernameResponse = await http.PostAsync("https://users.roblox.com/v1/usernames/users", content);
                    if (!usernameResponse.IsSuccessStatusCode)
                        return RobloxCheck.Failure("Não foi possível encontrar uma conta ROBLOX com esse nome. Verifique se o nome foi digitado corretamente.");

                    var usernameJson = JObject.Parse(await usernameResponse.Content.ReadAsStringAsync());
                    if (usernameJson["data"] is not JArray dataArrayLookup || dataArrayLookup.Count == 0)
                        return RobloxCheck.Failure("Nenhuma conta ROBLOX foi encontrada com o nome informado.");

                    robloxUserId = (long?)dataArrayLookup[0]?["id"] ?? 0;
                    if (robloxUserId <= 0)
                        return RobloxCheck.Failure("Não foi possível determinar o ID da conta ROBLOX a partir do nome informado.");
                }

                // Dados básicos (inclui data de criação)
                var userInfoResponse = await http.GetAsync($"https://users.roblox.com/v1/users/{robloxUserId}");
                if (!userInfoResponse.IsSuccessStatusCode)
                    return RobloxCheck.Failure("Não foi possível obter as informações da conta ROBLOX.");

                var userInfoJson = JObject.Parse(await userInfoResponse.Content.ReadAsStringAsync());
                var createdStr = userInfoJson["created"]?.Value<string>()?.Trim();
                if (string.IsNullOrWhiteSpace(createdStr) ||
                    !DateTimeOffset.TryParse(createdStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var createdAt))
                    return RobloxCheck.Failure("Não foi possível determinar a data de criação da conta ROBLOX.");

                accountAge = DateTimeOffset.UtcNow - createdAt;

                var friendsResponse = await http.GetAsync($"https://friends.roblox.com/v1/users/{robloxUserId}/friends/count");
                if (!friendsResponse.IsSuccessStatusCode)
                    return RobloxCheck.Failure("Não foi possível obter a quantidade de amigos da conta ROBLOX.");

                var friendsJson = JObject.Parse(await friendsResponse.Content.ReadAsStringAsync());
                friendsCount = (int?)friendsJson["count"] ?? 0;

                // Badges sao opcionais: contam como informacao, nunca bloqueiam.
                try
                {
                    var badgesResponse = await http.GetAsync($"https://badges.roblox.com/v1/users/{robloxUserId}/badges?limit=100&sortOrder=Asc");
                    if (!badgesResponse.IsSuccessStatusCode)
                    {
                        badgesAvailable = false;
                        Console.WriteLine($"[enlist] falha ao consultar badges ROBLOX. Status: {badgesResponse.StatusCode}");
                    }
                    else
                    {
                        var badgesJson = JObject.Parse(await badgesResponse.Content.ReadAsStringAsync());
                        if (badgesJson["data"] is not JArray dataArray)
                        {
                            badgesAvailable = false;
                            Console.WriteLine("[enlist] resposta de badges ROBLOX sem campo 'data'.");
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
                    Console.WriteLine("[enlist] tempo excedido ao consultar badges ROBLOX.");
                }
                catch (Exception ex)
                {
                    badgesAvailable = false;
                    Console.WriteLine($"[enlist] falha ao consultar badges ROBLOX: {ex.Message}");
                }
            }
            catch (TaskCanceledException)
            {
                return RobloxCheck.Failure("A consulta à API do ROBLOX demorou demais. Tente novamente em instantes.");
            }
            catch (Exception ex)
            {
                var err = ex.Message ?? string.Empty;
                if (err.Length > 150)
                    err = err[..147] + "...";

                return RobloxCheck.Failure($"Ocorreu um erro inesperado ao consultar a conta ROBLOX: {err}");
            }

            // A decisão de ALT usa apenas idade da conta + amigos.
            // Badges contam apenas como informação/bônus, não bloqueiam.
            var isLikelyMain = accountAge >= MinAccountAge && friendsCount >= MinFriends;

            return new RobloxCheck(true, null, robloxUserId, accountAge, friendsCount, badgeCount, badgesAvailable, isLikelyMain);
        }

        /// <summary>
        /// Aplica cargos e apelido. <paramref name="permissionScope"/> e o canal
        /// usado para conferir as permissoes do bot; quando null (chamada pela
        /// API, que nao tem canal) usa as permissoes do servidor.
        /// </summary>
        public static async Task<EnlistApplyResult> ApplyAsync(
            DiscordClient client,
            DiscordGuild guild,
            DiscordMember targetMember,
            JSONReader config,
            bool socialRole,
            DiscordChannel? permissionScope = null)
        {
            var addedRoles = new List<DiscordRole>();
            var warnings = new List<string>();

            DiscordMember? botMember = null;
            try
            {
                botMember = await guild.GetMemberAsync(client.CurrentUser.Id);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[enlist] nao foi possivel obter o membro do bot: {ex.Message}");
            }

            var botCanManageRoles = HasPermission(botMember, permissionScope, Permissions.ManageRoles);
            var botHighestRole = botMember?.Roles.OrderByDescending(r => r.Position).FirstOrDefault();

            if (config.enlistTargetRoleIds is { Length: > 0 })
            {
                if (!botCanManageRoles)
                {
                    warnings.Add("O bot não tem permissão para gerenciar cargos. Verifique as permissões do bot.");
                }
                else
                {
                    foreach (var roleId in config.enlistTargetRoleIds)
                        await GrantAsync(targetMember, guild, roleId, botHighestRole, "Alistamento via comando", addedRoles, warnings);
                }
            }

            if (socialRole)
            {
                if (!config.enlistSocialRoleId.HasValue || config.enlistSocialRoleId.Value == 0)
                    warnings.Add("O cargo social não está configurado. Verifique o `enlistSocialRoleId` no config.jsonc.");
                else if (!botCanManageRoles)
                    warnings.Add("O bot não tem permissão para gerenciar cargos (cargo social).");
                else
                    await GrantAsync(targetMember, guild, config.enlistSocialRoleId.Value, botHighestRole, "Cargo social via comando", addedRoles, warnings);
            }

            // Prefixo [12°] no apelido, se ainda não existir.
            var nicknameChanged = false;
            var currentNick = targetMember.Nickname ?? targetMember.Username;

            if (!NicknameUtil.HasPrefix(currentNick))
            {
                if (!HasPermission(botMember, permissionScope, Permissions.ManageNicknames))
                {
                    warnings.Add("O bot não tem permissão para gerenciar apelidos. Verifique as permissões do bot.");
                }
                else
                {
                    try
                    {
                        // WithPrefix corta o nome quando necessario: o Discord
                        // recusa apelido com mais de 32 caracteres.
                        var newNick = NicknameUtil.WithPrefix(currentNick);
                        await targetMember.ModifyAsync(m => m.Nickname = newNick);
                        nicknameChanged = true;
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Não foi possível alterar o apelido. Erro: {ex.Message}");
                    }
                }
            }

            return new EnlistApplyResult(addedRoles, nicknameChanged, warnings);
        }

        private static async Task GrantAsync(
            DiscordMember targetMember,
            DiscordGuild guild,
            ulong roleId,
            DiscordRole? botHighestRole,
            string reason,
            List<DiscordRole> addedRoles,
            List<string> warnings)
        {
            if (!guild.Roles.TryGetValue(roleId, out var role))
            {
                warnings.Add($"Cargo ID {roleId} não encontrado no servidor.");
                return;
            }

            // Ja tem: nao e erro nem aviso.
            if (targetMember.Roles.Any(r => r.Id == roleId))
                return;

            // O bot so consegue atribuir cargos abaixo do proprio cargo mais alto.
            if (botHighestRole is not null && role.Position >= botHighestRole.Position)
            {
                warnings.Add($"Não foi possível atribuir o cargo {role.Name}: posição igual ou superior ao cargo mais alto do bot.");
                return;
            }

            try
            {
                await targetMember.GrantRoleAsync(role, reason);
                addedRoles.Add(role);
            }
            catch (Exception ex)
            {
                // Continua tentando os outros cargos mesmo que um falhe.
                warnings.Add($"Falha ao adicionar o cargo {role.Name}: {ex.Message}");
            }
        }

        private static bool HasPermission(DiscordMember? botMember, DiscordChannel? scope, Permissions permission)
        {
            if (botMember is null)
                return false;

            return scope is not null
                ? botMember.PermissionsIn(scope).HasPermission(permission)
                : botMember.Permissions.HasPermission(permission);
        }

        /// <summary>
        /// Embed do canal de log de alistamento.
        ///
        /// <paramref name="title"/>, <paramref name="description"/> e
        /// <paramref name="extraFields"/> existem para o /alistar-se, que registra
        /// as mesmas informacoes mais as tres respostas do formulario (idioma,
        /// outros grupos, quem recrutou). Sem eles aquele comando precisaria de um
        /// embed proprio - que foi exatamente como a duplicacao comecou.
        /// </summary>
        public static DiscordEmbed BuildLogEmbed(
            DiscordClient client,
            DiscordMember targetMember,
            string executorMention,
            string robloxName,
            RobloxCheck check,
            EnlistApplyResult applied,
            bool socialRole,
            string? title = null,
            string? description = null,
            IEnumerable<(string Name, string Value)>? extraFields = null)
        {
            var embed = new DiscordEmbedBuilder()
                .WithTitle(title ?? "12° Regiment - Recruit Log")
                .WithDescription(description ?? "Registro de alistamento realizado com sucesso.")
                .WithColor(DiscordColor.Blurple)
                .WithThumbnail(targetMember.GetAvatarUrl(MediaFormat.Auto))
                .WithFooter("Recruit log gerado por CornwallBot", client.CurrentUser.AvatarUrl)
                .WithTimestamp(DateTimeOffset.UtcNow)
                .AddField(new DiscordEmbedField("Executor", executorMention, true))
                .AddField(new DiscordEmbedField("Alistado", targetMember.Mention, true))
                .AddField(new DiscordEmbedField("Nome no ROBLOX", robloxName, true))
                .AddField(new DiscordEmbedField("ROBLOX ID", check.RobloxUserId.ToString(), true));

            foreach (var (name, value) in extraFields ?? Enumerable.Empty<(string, string)>())
            {
                embed.AddField(new DiscordEmbedField(
                    name,
                    string.IsNullOrWhiteSpace(value) ? "N/A" : AuditEmbeds.Trim(value, 1024),
                    true));
            }

            return embed
                .AddField(new DiscordEmbedField("Idade da conta (dias)", check.AccountAge.Days.ToString(), true))
                .AddField(new DiscordEmbedField("Amigos", check.FriendsCount.ToString(), true))
                .AddField(new DiscordEmbedField("Badges", check.BadgesDisplay, true))
                .AddField(new DiscordEmbedField("Cargos adicionados", applied.AddedRoles.Count > 0 ? string.Join(", ", applied.AddedRoles.Select(r => r.Mention)) : "Nenhum", false))
                .AddField(new DiscordEmbedField("Cargo social?", socialRole ? "Sim" : "Não", true))
                .AddField(new DiscordEmbedField("Verificação de alt", "Automática (aprovado)", true))
                .Build();
        }

        /// <summary>
        /// Publica o log e a boas-vindas. Falhas viram avisos, nunca excecao: o
        /// alistamento em si ja aconteceu e nao deve ser reportado como erro
        /// porque o canal de log esta mal configurado.
        /// </summary>
        public static async Task<List<string>> AnnounceAsync(
            DiscordClient client,
            DiscordGuild guild,
            JSONReader config,
            DiscordEmbed logEmbed,
            DiscordMember targetMember)
        {
            var warnings = new List<string>();

            if (config.enlistLogChannelId.HasValue)
            {
                await TrySendAsync(client, guild, config.enlistLogChannelId.Value, warnings, "canal de logs",
                    channel => channel.SendMessageAsync(new DiscordMessageBuilder().AddEmbed(logEmbed)));
            }

            if (config.enlistWelcomeChannelId is { } welcomeId && welcomeId != 0)
            {
                await TrySendAsync(client, guild, welcomeId, warnings, "canal de boas-vindas",
                    channel => channel.SendMessageAsync($"Bem-vindo ao 12°, {targetMember.Mention}!"));
            }

            return warnings;
        }

        private static async Task TrySendAsync(
            DiscordClient client,
            DiscordGuild guild,
            ulong channelId,
            List<string> warnings,
            string label,
            Func<DiscordChannel, Task> send)
        {
            try
            {
                var channel = await client.GetChannelAsync(channelId);
                if (channel is null)
                {
                    warnings.Add($"Não foi possível encontrar o {label} (ID {channelId}).");
                    return;
                }

                if (channel.GuildId != guild.Id)
                {
                    warnings.Add($"O {label} configurado pertence a outro servidor.");
                    return;
                }

                await send(channel);
            }
            catch (Exception ex)
            {
                var err = ex.Message ?? "";
                if (err.Length > 150)
                    err = err[..147] + "...";

                warnings.Add($"Erro ao escrever no {label}: {err}. Confira o ID e se o bot tem **Ver canal** e **Enviar mensagens** nele.");
            }
        }
    }
}
