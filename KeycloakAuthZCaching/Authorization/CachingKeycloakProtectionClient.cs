
using Keycloak.AuthServices.Authorization.AuthorizationServer;
using System.Reflection;

namespace Shifts_Tools.Services.Plumbing.Authorization
{

    public class CachingAuthorizationServerClientDecorator : IAuthorizationServerClient
    {
        private readonly IAuthorizationServerClient _inner;
        private readonly IPermissionCacheService _cacheService;

        public CachingAuthorizationServerClientDecorator(
            IAuthorizationServerClient inner,
            IPermissionCacheService cacheService)
        {
            _inner = inner;
            _cacheService = cacheService;
        }

        public async Task<bool> VerifyAccessToResource(
            string resource,
            string scopes,
            ScopesValidationMode? scopesValidationMode,
            CancellationToken cancellationToken)
        {
            // 1. Создаем ключ кэша через ваш сервис
            var cacheKey = await _cacheService.GetPermissionCacheKeyAsync(resource, scopes);

            // 2. Пытаемся получить результат из кэша
            var cachedResult = await _cacheService.GetAsync(cacheKey);
            if (cachedResult.HasValue)
            {
                return cachedResult.Value;
            }

            // 3. Если в кэше нет — делаем реальный запрос к Keycloak
            var result = await _inner.VerifyAccessToResource(
                resource, scopes, scopesValidationMode, cancellationToken);

            // 4. Сохраняем в кэш
            await _cacheService.SetAsync(cacheKey, result);

            return result;
        }
    }
}
