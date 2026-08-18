using System.Linq;
using CornwallUtilities.config;
using CornwallUtilities.Services.Audit;
using DisCatSharp.ApplicationCommands.Context;
using DisCatSharp.Entities;

namespace CornwallUtilities.commands
{
    /// <summary>
    /// Portao de staff dos comandos /audit-*. Reaproveita exatamente o mesmo
    /// criterio de /enlistuser: o cargo em `enlistPermissionRoleId`.
    /// </summary>
    internal static class AuditPermissions
    {
        /// <summary>Retorna null quando o uso e permitido, ou o embed de recusa.</summary>
        public static DiscordEmbed? CheckStaff(InteractionContext ctx, JSONReader config)
        {
            if (ctx.Guild is null || ctx.Member is null)
                return AuditEmbeds.Error("Comando indisponível", "Este comando só pode ser usado dentro do servidor.");

            if (!config.enlistPermissionRoleId.HasValue)
                return AuditEmbeds.Error("Configuração ausente", "`enlistPermissionRoleId` não está definido no config.jsonc.");

            var requiredRoleId = config.enlistPermissionRoleId.Value;
            var hasPermission = ctx.Member.Roles.Any(r => r.Id == requiredRoleId);

            return hasPermission
                ? null
                : AuditEmbeds.Denied($"Você precisa do cargo <@&{requiredRoleId}> para usar os comandos de auditoria.");
        }
    }
}
