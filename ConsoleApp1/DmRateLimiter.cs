using System;
using System.Threading;
using System.Threading.Tasks;

namespace CornwallUtilities
{
    /// <summary>
    /// Limitador global das DMs do bot.
    ///
    /// Garante ao menos 3 segundos entre duas DMs consecutivas em todo o bot,
    /// para nao disparar a deteccao de spam do Discord.
    /// </summary>
    internal static class DmRateLimiter
    {
        private static readonly TimeSpan MinDelayBetweenDms = TimeSpan.FromSeconds(3);

        /// <summary>
        /// Semaforo, e nao lock com laco de re-tentativa.
        ///
        /// A versao antiga soltava o lock, dormia o tempo que faltava e voltava a
        /// disputar - sem fila. Com um envio em massa de 500 pessoas correndo
        /// junto com os alertas do MessageBlacklistService, uma das tarefas podia
        /// perder a disputa repetidamente e ficar para tras indefinidamente.
        /// O SemaphoreSlim e FIFO na pratica: quem chegou primeiro sai primeiro.
        /// </summary>
        private static readonly SemaphoreSlim s_gate = new(1, 1);

        private static DateTimeOffset? s_lastSentAt;

        /// <summary>
        /// Espera ate completar o intervalo minimo desde a ultima DM e entao
        /// marca o horario deste envio. Chamar antes de cada DM.
        /// </summary>
        public static async Task WaitForSlotAsync()
        {
            // O semaforo fica preso DURANTE a espera: e isso que serializa a fila
            // e da a ordem de chegada. Sem isso duas tarefas calculariam a mesma
            // folga e enviariam juntas.
            await s_gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (s_lastSentAt is { } last)
                {
                    var waitFor = MinDelayBetweenDms - (DateTimeOffset.UtcNow - last);
                    if (waitFor > TimeSpan.Zero)
                        await Task.Delay(waitFor).ConfigureAwait(false);
                }

                s_lastSentAt = DateTimeOffset.UtcNow;
            }
            finally
            {
                s_gate.Release();
            }
        }
    }
}
