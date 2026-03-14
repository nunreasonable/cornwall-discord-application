using System.Threading.Tasks;
using CornwallUtilities.config;
using DisCatSharp;
using DisCatSharp.Entities;
using DisCatSharp.ApplicationCommands;
using DisCatSharp.Enums;
using DisCatSharp.ApplicationCommands.Context;
using DisCatSharp.ApplicationCommands.Attributes;

namespace CornwallUtilities.commands
{
    internal class SimpleTest : ApplicationCommandsModule
    {
        [SlashCommand("simpletest", "Teste simples do deployment")]
        public async Task SimpleTestCommand(InteractionContext ctx)
        {
            await ctx.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);

            var config = new JSONReader();
            await config.ReadJSON();

            var embed = new DiscordEmbedBuilder()
                .WithTitle("🔍 Teste Simples")
                .WithColor(DiscordColor.Blurple)
                .WithTimestamp(DateTimeOffset.UtcNow);

            // Teste 1: Verificar se o bot pode enviar mensagens no canal atual
            try
            {
                await ctx.Channel.SendMessageAsync("Teste de mensagem");
                embed.AddField(new DiscordEmbedField("✅ Canal Atual", "Bot consegue enviar mensagens"));
            }
            catch (Exception ex)
            {
                embed.AddField(new DiscordEmbedField("❌ Canal Atual", $"Erro: {ex.Message}"));
            }

            // Teste 2: Verificar se o bot pode acessar o canal de deployment
            if (config.deploymentChannelId.HasValue)
            {
                try
                {
                    var deploymentChannel = await ctx.Client.GetChannelAsync(config.deploymentChannelId.Value);
                    if (deploymentChannel != null)
                    {
                        embed.AddField(new DiscordEmbedField("✅ Canal Deployment", $"Canal encontrado: {deploymentChannel.Name}"));
                        
                        // Teste 3: Tentar enviar mensagem no canal de deployment
                        try
                        {
                            var testMessage = await deploymentChannel.SendMessageAsync("Teste de mensagem");
                            embed.AddField(new DiscordEmbedField("✅ Envio Deployment", "Mensagem enviada com sucesso"));
                            await testMessage.DeleteAsync(); // Limpar teste
                        }
                        catch (Exception ex)
                        {
                            embed.AddField(new DiscordEmbedField("❌ Envio Deployment", $"Erro ao enviar: {ex.Message}"));
                        }
                    }
                    else
                    {
                        embed.AddField(new DiscordEmbedField("❌ Canal Deployment", "Canal não encontrado (null)"));
                    }
                }
                catch (Exception ex)
                {
                    embed.AddField(new DiscordEmbedField("❌ Canal Deployment", $"Erro ao acessar: {ex.Message}"));
                }
            }
            else
            {
                embed.AddField(new DiscordEmbedField("❌ Configuração", "deploymentChannelId não configurado"));
            }

            await ctx.EditResponseAsync(new DiscordWebhookBuilder().AddEmbed(embed.Build()));
        }
    }
}
