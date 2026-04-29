using Keycloak.AuthServices.Authorization.Requirements;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Shifts_Tools.Services.Plumbing.Authorization
{
    public interface IPermissionService
    {
        Task<bool> HasPermissionAsync(string resource, string? scope = null);
        Task<bool> HasPolicyAsync(string policyName);
        Task<Dictionary<string, bool>> HasPermissionsAsync(params (string resource, string? scope)[] permissions);
        Task<Dictionary<string, bool>> HasPoliciesAsync(params string[] policyNames);
        Task<bool> CanAsync(string permissionOrPolicy, string? scope = null);
        Task PreloadPermissionsAsync(params (string resource, string? scope)[] permissions);
        Task PreloadPoliciesAsync(params string[] policyNames);
    }

    public class PermissionService : IPermissionService
    {
        private readonly IAuthorizationService _authorizationService;
        private readonly IAuthorizationPolicyProvider _policyProvider;
        private readonly AuthenticationStateProvider _authenticationStateProvider;
        private readonly IPermissionCacheService _cacheService;
        private readonly ILogger<PermissionService> _logger;

        // Защита от race condition (параллельные проверки одного и того же разрешения)
        private readonly ConcurrentDictionary<string, Task<bool>> _pendingTasks = new();

        public PermissionService(
            IServiceProvider serviceProvider,
            IAuthorizationPolicyProvider policyProvider,
            AuthenticationStateProvider authenticationStateProvider,
            IPermissionCacheService cacheService,
            ILogger<PermissionService> logger)
        {
            _authorizationService = serviceProvider.GetRequiredService<IAuthorizationService>();
            _policyProvider = policyProvider;
            _authenticationStateProvider = authenticationStateProvider;
            _cacheService = cacheService;
            _logger = logger;
        }

        public async Task<bool> HasPermissionAsync(string resource, string? scope = null)
        {
            var user = await GetCurrentUserAsync();

            if (user?.Identity?.IsAuthenticated != true)
            {
                return false;
            }

            var cacheKey = await _cacheService.GetPermissionCacheKeyAsync(resource, scope);

            // Проверяем кэш
            var cachedValue = await _cacheService.GetAsync(cacheKey);
            if (cachedValue.HasValue)
            {
                _logger.LogTrace("Разрешение '{Resource}:{Scope}' найдено в кэше: {Value}", resource, scope, cachedValue.Value);
                return cachedValue.Value;
            }

            // Защита от race condition
            var checkTask = _pendingTasks.GetOrAdd(cacheKey, _ =>
            {
                _logger.LogDebug("Создана задача проверки разрешения '{Resource}:{Scope}'", resource, scope);

                return Task.Run(async () =>
                {
                    try
                    {
                        // Двойная проверка кэша
                        var recheckValue = await _cacheService.GetAsync(cacheKey);
                        if (recheckValue.HasValue)
                        {
                            _logger.LogDebug("Разрешение '{Resource}:{Scope}' загружено другой задачей", resource, scope);
                            return recheckValue.Value;
                        }

                        // Реальная проверка у Keycloak
                        _logger.LogInformation("Проверка разрешения '{Resource}:{Scope}' у Keycloak", resource, scope);

                        var result = await CheckPermissionInternalAsync(resource, scope, user);

                        // Сохраняем в кэш
                        await _cacheService.SetAsync(cacheKey, result);

                        return result;
                    }
                    finally
                    {
                        _pendingTasks.TryRemove(cacheKey, out Task<bool>_);
                    }
                });
            });

            return await checkTask;
        }

        public async Task<bool> HasPolicyAsync(string policyName)
        {
            var user = await GetCurrentUserAsync();

            if (user?.Identity?.IsAuthenticated != true)
            {
                return false;
            }

            var cacheKey = await _cacheService.GetPolicyCacheKeyAsync(policyName);

            // Проверяем кэш
            var cachedValue = await _cacheService.GetAsync(cacheKey);
            if (cachedValue.HasValue)
            {
                _logger.LogTrace("Политика '{Policy}' найдена в кэше: {Value}", policyName, cachedValue.Value);
                return cachedValue.Value;
            }

            // Защита от race condition
            var checkTask = _pendingTasks.GetOrAdd(cacheKey, _ =>
            {
                _logger.LogDebug("Создана задача проверки политики '{Policy}'", policyName);

                return Task.Run(async () =>
                {
                    try
                    {
                        var recheckValue = await _cacheService.GetAsync(cacheKey);
                        if (recheckValue.HasValue)
                        {
                            _logger.LogDebug("Политика '{Policy}' загружена другой задачей", policyName);
                            return recheckValue.Value;
                        }

                        _logger.LogInformation("Проверка политики '{Policy}' у Keycloak", policyName);

                        var result = await CheckPolicyInternalAsync(policyName, user);

                        await _cacheService.SetAsync(cacheKey, result);

                        return result;
                    }
                    finally
                    {
                        _pendingTasks.TryRemove(cacheKey, out Task<bool> _);
                    }
                });
            });

            return await checkTask;
        }

        public async Task<bool> CanAsync(string permissionOrPolicy, string? scope = null)
        {
            if (scope == null && await IsPolicyAsync(permissionOrPolicy))
            {
                return await HasPolicyAsync(permissionOrPolicy);
            }

            return await HasPermissionAsync(permissionOrPolicy, scope);
        }

        public async Task<Dictionary<string, bool>> HasPermissionsAsync(params (string resource, string? scope)[] permissions)
        {
            var user = await GetCurrentUserAsync();

            if (user?.Identity?.IsAuthenticated != true)
            {
                var emptyResults = new Dictionary<string, bool>();
                foreach (var (resource, scope) in permissions)
                {
                    var key = await _cacheService.GetPermissionCacheKeyAsync(resource, scope);
                    emptyResults[key] = false;
                }
                return emptyResults;
            }

            var results = new Dictionary<string, bool>();
            var tasksToWait = new List<Task<(string key, bool result)>>();

            foreach (var (resource, scope) in permissions)
            {
                var cacheKey = await _cacheService.GetPermissionCacheKeyAsync(resource, scope);

                // Проверяем кэш
                var cachedValue = await _cacheService.GetAsync(cacheKey);
                if (cachedValue.HasValue)
                {
                    results[cacheKey] = cachedValue.Value;
                    continue;
                }

                // Добавляем задачу на проверку
                tasksToWait.Add(Task.Run(async () =>
                {
                    var result = await HasPermissionAsync(resource, scope);
                    return (cacheKey, result);
                }));
            }

            if (tasksToWait.Count > 0)
            {
                _logger.LogDebug("Параллельная проверка {Count} разрешений", tasksToWait.Count);

                var completedTasks = await Task.WhenAll(tasksToWait);
                foreach (var (key, result) in completedTasks)
                {
                    results[key] = result;
                }
            }

            return results;
        }

        public async Task<Dictionary<string, bool>> HasPoliciesAsync(params string[] policyNames)
        {
            var user = await GetCurrentUserAsync();

            if (user?.Identity?.IsAuthenticated != true)
            {
                return policyNames.ToDictionary(p => p, _ => false);
            }

            var results = new Dictionary<string, bool>();
            var tasksToWait = new List<Task<(string key, bool result)>>();

            foreach (var policyName in policyNames)
            {
                var cacheKey = await _cacheService.GetPolicyCacheKeyAsync(policyName);

                var cachedValue = await _cacheService.GetAsync(cacheKey);
                if (cachedValue.HasValue)
                {
                    results[policyName] = cachedValue.Value;
                    continue;
                }

                tasksToWait.Add(Task.Run(async () =>
                {
                    var result = await HasPolicyAsync(policyName);
                    return (policyName, result);
                }));
            }

            if (tasksToWait.Count > 0)
            {
                _logger.LogDebug("Параллельная проверка {Count} политик", tasksToWait.Count);

                var completedTasks = await Task.WhenAll(tasksToWait);
                foreach (var (key, result) in completedTasks)
                {
                    results[key] = result;
                }
            }

            return results;
        }

        public async Task PreloadPermissionsAsync(params (string resource, string? scope)[] permissions)
        {
            _logger.LogInformation("Предзагрузка {Count} разрешений", permissions.Length);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await HasPermissionsAsync(permissions);
            stopwatch.Stop();

            _logger.LogInformation("Предзагрузка завершена за {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
        }

        public async Task PreloadPoliciesAsync(params string[] policyNames)
        {
            _logger.LogInformation("Предзагрузка {Count} политик", policyNames.Length);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await HasPoliciesAsync(policyNames);
            stopwatch.Stop();

            _logger.LogInformation("Предзагрузка завершена за {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
        }

        // ========================================================================
        // ПРИВАТНЫЕ МЕТОДЫ
        // ========================================================================

        private async Task<ClaimsPrincipal?> GetCurrentUserAsync()
        {
            var authState = await _authenticationStateProvider.GetAuthenticationStateAsync();
            return authState.User;
        }

        private async Task<bool> CheckPermissionInternalAsync(string resource, string? scope, ClaimsPrincipal user)
        {
            try
            {
                var requirement = new DecisionRequirement(resource, scope ?? string.Empty);

                var authResult = await _authorizationService.AuthorizeAsync(
                    user,
                    resource: null,
                    requirements: new[] { requirement });

                var hasAccess = authResult.Succeeded;

                _logger.LogDebug("Доступ к ресурсу '{Resource}:{Scope}': {HasAccess}", resource, scope, hasAccess);

                return hasAccess;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при проверке доступа к ресурсу '{Resource}:{Scope}'", resource, scope);
                return false;
            }
        }

        private async Task<bool> CheckPolicyInternalAsync(string policyName, ClaimsPrincipal user)
        {
            try
            {
                var result = await _authorizationService.AuthorizeAsync(user, policyName);
                var hasAccess = result.Succeeded;

                _logger.LogDebug("Политика '{Policy}': {HasAccess}", policyName, hasAccess);

                return hasAccess;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при проверке политики '{Policy}'", policyName);
                return false;
            }
        }

        private async Task<bool> IsPolicyAsync(string name)
        {
            try
            {
                var policy = await _policyProvider.GetPolicyAsync(name);
                return policy != null;
            }
            catch
            {
                return false;
            }
        }
    }
}