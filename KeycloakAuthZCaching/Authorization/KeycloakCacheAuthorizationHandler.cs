using Keycloak.AuthServices.Authorization.Requirements;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Threading.Tasks;

namespace Shifts_Tools.Services.Plumbing.Authorization
{
    /// <summary>
    /// Authorization Handler для кэширования проверок RequireProtectedResource через PermissionService
    /// </summary>
    public class KeycloakCacheAuthorizationHandler : IAuthorizationHandler
    {
        private readonly IPermissionCacheService _cacheService;
        private readonly ILogger<KeycloakCacheAuthorizationHandler> _logger;

        public KeycloakCacheAuthorizationHandler(
            IPermissionCacheService cacheService,
            ILogger<KeycloakCacheAuthorizationHandler> logger)
        {
            _cacheService = cacheService;
            _logger = logger;
        }

        public async Task HandleAsync(AuthorizationHandlerContext context)
        {
            // Обрабатываем только ResourceAccessRequirement (из Keycloak.AuthServices)
            var resourceRequirements = context.PendingRequirements
                .OfType<DecisionRequirement>()
                .ToList();

            if (!resourceRequirements.Any())
            {
                return; // Нет требований для обработки
            }

            _logger.LogDebug("Обработка {Count} ResourceAccessRequirement через кэш", resourceRequirements.Count);

            foreach (var requirement in resourceRequirements)
            {
                var resource = requirement.Resource;
                var scope = requirement.Scopes; // Keycloak.AuthServices использует Roles как scopes

                // Генерируем ключ кэша
                var cacheKey = await _cacheService.GetPermissionCacheKeyAsync(resource, scope.FirstOrDefault());

                // Проверяем кэш
                var cachedValue = await _cacheService.GetAsync(cacheKey);

                if (cachedValue.HasValue)
                {
                    if (cachedValue.Value)
                    {
                        _logger.LogDebug(
                            "Разрешение '{Resource}:{Scope}' найдено в кэше: РАЗРЕШЕНО",
                            resource, scope);

                        context.Succeed(requirement);
                    }
                    else
                    {
                        _logger.LogDebug(
                            "Разрешение '{Resource}:{Scope}' найдено в кэше: ЗАПРЕЩЕНО",
                            resource, scope);

                        // НЕ вызываем context.Fail() - это блокирует других хендлеров
                        // Просто не вызываем context.Succeed()
                    }
                }
                else
                {
                    // Не найдено в кэше - пропускаем, другой хендлер (например, ProtectedResourceHandler) обработает
                    _logger.LogTrace(
                        "Разрешение '{Resource}:{Scope}' НЕ найдено в кэше, передаём другим хендлерам",
                        resource, scope);
                }
            }
        }
    }
}