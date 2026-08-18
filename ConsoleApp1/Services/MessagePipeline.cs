using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DisCatSharp.Entities;

namespace CornwallUtilities.Services
{
    /// <summary>
    /// Fila de trabalho para o que precisa de REST ao receber uma mensagem.
    ///
    /// O handler de MessageCreated e aguardado pelo despachante de eventos do
    /// gateway: qualquer chamada REST lenta (ou um 429) ali dentro trava o
    /// recebimento de eventos e, por consequencia, o heartbeat. Aqui a mensagem
    /// so e enfileirada; um unico consumidor processa em segundo plano,
    /// preservando a ordem de chegada.
    /// </summary>
    internal static class MessagePipeline
    {
        private static readonly Channel<DiscordMessage> s_channel =
            Channel.CreateBounded<DiscordMessage>(new BoundedChannelOptions(512)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropWrite
            });

        private static int s_started;
        private static long s_dropped;

        public static long DroppedCount => Interlocked.Read(ref s_dropped);

        public static void Start(Func<DiscordMessage, Task> handler)
        {
            if (Interlocked.Exchange(ref s_started, 1) == 1)
                return;

            _ = Task.Run(() => ConsumeAsync(handler));
        }

        public static void Enqueue(DiscordMessage message)
        {
            if (!s_channel.Writer.TryWrite(message))
                Interlocked.Increment(ref s_dropped);
        }

        private static async Task ConsumeAsync(Func<DiscordMessage, Task> handler)
        {
            await foreach (var message in s_channel.Reader.ReadAllAsync())
            {
                try
                {
                    await handler(message);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[pipeline] erro ao processar mensagem: {ex}");
                }
            }
        }
    }
}
