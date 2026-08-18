using System.Collections.Generic;
using System.Linq;
using DisCatSharp.Entities;

namespace CornwallUtilities.Services.Audit
{
    internal static class ModalUtil
    {
        /// <summary>
        /// Le o valor de um campo de texto de um modal submetido.
        ///
        /// O Discord mudou o aninhamento dos componentes (Components V2), entao a
        /// busca e recursiva via GetChildren() em vez de assumir action rows -
        /// assim continua funcionando se o aninhamento mudar de novo.
        /// </summary>
        public static string? ReadModalValue(DiscordInteraction interaction, string customId)
        {
            var components = interaction.Data?.ModalComponents;
            if (components is null)
                return null;

            return Flatten(components)
                .OfType<DiscordTextInputComponent>()
                .FirstOrDefault(t => t.CustomId == customId)?
                .Value;
        }

        private static IEnumerable<DiscordComponent> Flatten(IEnumerable<DiscordComponent> components)
        {
            foreach (var component in components)
            {
                if (component is null)
                    continue;

                yield return component;

                var children = component.GetChildren();
                if (children is null)
                    continue;

                foreach (var child in Flatten(children))
                    yield return child;
            }
        }
    }
}
