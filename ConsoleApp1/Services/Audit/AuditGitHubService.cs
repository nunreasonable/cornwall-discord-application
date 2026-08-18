using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using CornwallUtilities.config;
using Octokit;

namespace CornwallUtilities.Services.Audit
{
    /// <summary>Erro de configuracao da auditoria, reportado ao staff em portugues.</summary>
    internal sealed class AuditConfigException : Exception
    {
        public AuditConfigException(string message) : base(message) { }
    }

    /// <summary>
    /// Publica os arquivos da auditoria numa branch dedicada via API do GitHub.
    /// Nao usa `git` local - so o PAT configurado.
    /// </summary>
    internal sealed class AuditGitHubService
    {
        private static GitHubClient? s_client;
        private static string? s_clientToken;
        private static readonly object s_clientLock = new();

        private readonly GitHubClient _client;
        private readonly string _owner;
        private readonly string _repo;
        private readonly string _branch;

        public string Owner => _owner;
        public string Repo => _repo;
        public string Branch => _branch;

        /// <summary>
        /// Cria o serviço resolvendo a credencial do GitHub (config, variável de
        /// ambiente ou login do gh CLI). É assíncrono porque a descoberta via gh
        /// CLI executa um processo.
        /// </summary>
        public static async Task<AuditGitHubService> CreateAsync(AuditConfig? cfg)
        {
            if (cfg is null)
                throw new AuditConfigException("A seção `audit` não existe no config.jsonc.");

            if (string.IsNullOrWhiteSpace(cfg.githubOwner) || string.IsNullOrWhiteSpace(cfg.githubRepo))
                throw new AuditConfigException("`audit.githubOwner` e `audit.githubRepo` precisam estar configurados.");

            var token = await GitHubTokenProvider.ResolveAsync(cfg).ConfigureAwait(false);
            return new AuditGitHubService(cfg, token);
        }

        private AuditGitHubService(AuditConfig cfg, string token)
        {
            _owner = cfg.githubOwner!;
            _repo = cfg.githubRepo!;
            _branch = string.IsNullOrWhiteSpace(cfg.githubBranch) ? "audit-data" : cfg.githubBranch!;
            _client = GetClient(token);
        }

        // O GitHubClient carrega um HttpClient proprio: reaproveitar evita o mesmo
        // acumulo de sockets que o HttpClientProvider resolve no resto do bot.
        private static GitHubClient GetClient(string token)
        {
            lock (s_clientLock)
            {
                if (s_client is null || !string.Equals(s_clientToken, token, StringComparison.Ordinal))
                {
                    s_client = new GitHubClient(new ProductHeaderValue("cornwall-bot"))
                    {
                        Credentials = new Credentials(token)
                    };
                    s_clientToken = token;
                }

                return s_client;
            }
        }

        /// <summary>Cria a branch de dados a partir da branch padrao, se ela ainda nao existir.</summary>
        public async Task EnsureBranchExistsAsync()
        {
            try
            {
                // Atencao: Get espera "heads/x", enquanto NewReference espera o
                // caminho completo "refs/heads/x".
                await _client.Git.Reference.Get(_owner, _repo, $"heads/{_branch}").ConfigureAwait(false);
            }
            catch (NotFoundException)
            {
                var repo = await _client.Repository.Get(_owner, _repo).ConfigureAwait(false);
                var baseRef = await _client.Git.Reference.Get(_owner, _repo, $"heads/{repo.DefaultBranch}").ConfigureAwait(false);
                await _client.Git.Reference
                    .Create(_owner, _repo, new NewReference($"refs/heads/{_branch}", baseRef.Object.Sha))
                    .ConfigureAwait(false);
            }
        }

        public async Task<string?> TryGetFileShaAsync(string path)
        {
            try
            {
                var contents = await _client.Repository.Content
                    .GetAllContentsByRef(_owner, _repo, path, _branch)
                    .ConfigureAwait(false);
                return contents.FirstOrDefault()?.Sha;
            }
            catch (NotFoundException)
            {
                return null;
            }
        }

        /// <summary>
        /// Cria ou atualiza um arquivo na branch. Refaz a busca do sha a cada
        /// tentativa: um sha velho devolve 409/422 e a nova tentativa resolve.
        /// </summary>
        public async Task<string> PutFileAsync(string path, string content, string commitMessage)
        {
            for (var attempt = 0; ; attempt++)
            {
                var sha = await TryGetFileShaAsync(path).ConfigureAwait(false);

                try
                {
                    if (sha is null)
                    {
                        // CreateFileRequest ja faz o base64 do conteudo - nao codificar antes.
                        var created = await _client.Repository.Content
                            .CreateFile(_owner, _repo, path, new CreateFileRequest(commitMessage, content, _branch))
                            .ConfigureAwait(false);
                        return created.Commit.Sha;
                    }

                    var updated = await _client.Repository.Content
                        .UpdateFile(_owner, _repo, path, new UpdateFileRequest(commitMessage, content, sha, _branch))
                        .ConfigureAwait(false);
                    return updated.Commit.Sha;
                }
                catch (ApiValidationException) when (attempt < 2)
                {
                    await Task.Delay(500 * (attempt + 1)).ConfigureAwait(false);
                }
                catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict && attempt < 2)
                {
                    await Task.Delay(500 * (attempt + 1)).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Caminho do arquivo de arquivamento do dia. Se ja existir um para hoje,
        /// acrescenta um sufixo para nao sobrescrever lotes do mesmo dia.
        /// </summary>
        public async Task<string> ResolveArchivePathAsync(string directory, DateTimeOffset utcNow)
        {
            var date = utcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            if (await TryGetFileShaAsync($"{directory}/{date}.json").ConfigureAwait(false) is null)
                return $"{directory}/{date}.json";

            for (var i = 2; i <= 50; i++)
            {
                var candidate = $"{directory}/{date}-{i}.json";
                if (await TryGetFileShaAsync(candidate).ConfigureAwait(false) is null)
                    return candidate;
            }

            throw new InvalidOperationException("Limite de arquivos de arquivamento para hoje atingido.");
        }

        public string CommitUrl(string sha) => $"https://github.com/{_owner}/{_repo}/commit/{sha}";

        /// <summary>Traduz as falhas mais comuns da API para uma mensagem util ao staff.</summary>
        public static string DescribeError(Exception ex)
        {
            // Um 401 quase sempre significa token vencido: descartar o cache faz a
            // proxima tentativa reler a credencial em vez de repetir a falha.
            if (ex is AuthorizationException)
                GitHubTokenProvider.Invalidate();

            return Describe(ex);
        }

        private static string Describe(Exception ex) => ex switch
        {
            AuditConfigException => ex.Message,
            AuthorizationException =>
                "Credencial do GitHub inválida ou expirada. Rode `gh auth login` na máquina do bot " +
                "(ou atualize `GH_TOKEN` / `audit.githubToken`, se estiver usando um deles) e tente de novo.",
            NotFoundException => "Repositório, dono ou branch não encontrado. Verifique `audit.githubOwner`, `audit.githubRepo` e as permissões do token.",
            RateLimitExceededException => "Limite de requisições do GitHub atingido. Tente novamente mais tarde.",
            ApiValidationException ave => $"O GitHub recusou a requisição: {ave.Message}",
            ApiException ae => $"Erro da API do GitHub ({ae.StatusCode}): {ae.Message}",
            _ => ex.Message
        };
    }
}
