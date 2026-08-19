namespace CornwallUtilities.Services
{
    /// <summary>
    /// Regras do apelido do regimento.
    ///
    /// O apelido do Discord tem teto de 32 caracteres. Colar o prefixo sem
    /// conferir isso fazia a API recusar a alteracao para quem ja tinha nome
    /// comprido - o alistamento seguia, mas o apelido nao era aplicado e a
    /// pessoa ficava sem a marca do regimento.
    /// </summary>
    internal static class NicknameUtil
    {
        public const string Prefix = "[12°]";
        private const int MaxNicknameLength = 32;

        public static bool HasPrefix(string nickname) =>
            nickname.StartsWith(Prefix, System.StringComparison.OrdinalIgnoreCase);

        /// <summary>Apelido com o prefixo, cortando o nome se necessario para caber.</summary>
        public static string WithPrefix(string current)
        {
            var room = MaxNicknameLength - Prefix.Length - 1; // -1 do espaco
            var name = current.Length <= room ? current : current[..room];

            return $"{Prefix} {name}";
        }
    }
}
