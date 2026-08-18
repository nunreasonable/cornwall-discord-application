using System;
using System.Net;
using System.Net.Http;

namespace CornwallUtilities.Services
{
    /// <summary>
    /// HttpClient compartilhado do bot.
    ///
    /// Antes cada comando fazia `using var http = new HttpClient()`, o que acumula
    /// sockets em TIME_WAIT e, sob uso continuo, esgota portas locais. Um unico
    /// cliente estatico com SocketsHttpHandler resolve isso e ainda recicla as
    /// conexoes periodicamente para nao ficar preso a um DNS antigo.
    ///
    /// IMPORTANTE: nunca altere Shared.Timeout depois do primeiro request -
    /// isso lanca InvalidOperationException. Use um CancellationTokenSource
    /// para prazos por requisicao.
    /// </summary>
    internal static class HttpClientProvider
    {
        private static readonly SocketsHttpHandler s_handler = new()
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 20,
            AutomaticDecompression = DecompressionMethods.All
        };

        public static readonly HttpClient Shared = new(s_handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
    }
}
