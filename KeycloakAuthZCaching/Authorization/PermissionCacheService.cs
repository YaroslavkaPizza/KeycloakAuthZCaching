using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace KeycloakAuthZCaching.Plumbing.Authorization
{
    public interface IPermissionCacheService
    {
        Task<bool?> GetAsync(string key);
        Task SetAsync(string key, bool value);
        Task<string> GetPermissionCacheKeyAsync(string resource, string? scope);
        Task<string> GetPermissionCacheKeyAsync(ClaimsPrincipal user, string resource, string? scope);
        Task<string> GetPolicyCacheKeyAsync(string policyName);
        Task ClearCacheAsync();
        PermissionCacheStatistics GetStatistics();
    }

    public class PermissionCacheService : IPermissionCacheService, IDisposable
    {
        private readonly AuthenticationStateProvider _authenticationStateProvider;
        private readonly IDistributedCache _distributedCache;
        private readonly ILogger<PermissionCacheService> _logger;
        private readonly PermissionCacheOptions _options;

        // Локальный кэш (Level 1)
        private readonly ConcurrentDictionary<string, (bool value, DateTime expiry)> _localCache = new();

        // Таймер для фоновой очистки
        private Timer? _cleanupTimer;

        // Отслеживание статуса аутентификации
        private bool? _wasAuthenticated;

        // Кэшированные идентификаторы пользователя
        private string? _cachedUserId;
        private string? _cachedSessionId;

        public PermissionCacheService(
            AuthenticationStateProvider authenticationStateProvider,
            IDistributedCache distributedCache,
            ILogger<PermissionCacheService> logger,
            Microsoft.Extensions.Options.IOptions<PermissionCacheOptions> options)
        {
            _authenticationStateProvider = authenticationStateProvider;
            _distributedCache = distributedCache;
            _logger = logger;
            _options = options.Value;

            // Подписка на изменение статуса аутентификации
            _authenticationStateProvider.AuthenticationStateChanged += OnAuthenticationStateChanged;

            // Фоновая очистка локального кэша
            if (_options.UseLocalCache)
            {
                _cleanupTimer = new Timer(
                    CleanupExpiredLocalCache,
                    null,
                    TimeSpan.FromMinutes(1),
                    TimeSpan.FromMinutes(1));
            }

            _logger.LogDebug("PermissionCacheService создан");
        }

        /// <summary>
        /// Получить значение из кэша (локального или распределённого)
        /// </summary>
        public async Task<bool?> GetAsync(string key)
        {
            // Level 1: Локальный кэш
            if (_options.UseLocalCache && TryGetFromLocalCache(key, out var localValue))
            {
                _logger.LogTrace("Значение для ключа '{Key}' найдено в локальном кэше", key);
                return localValue;
            }

            // Level 2: Distributed cache
            var cachedBytes = await _distributedCache.GetAsync(key);
            if (cachedBytes != null)
            {
                var cachedValue = DeserializeCacheValue(cachedBytes);
                _logger.LogTrace("Значение для ключа '{Key}' найдено в распределённом кэше", key);

                // Сохраняем в локальный кэш
                if (_options.UseLocalCache)
                {
                    AddToLocalCache(key, cachedValue);
                }

                return cachedValue;
            }

            // Не найдено
            return null;
        }

        /// <summary>
        /// Сохранить значение в кэш
        /// </summary>
        public async Task SetAsync(string key, bool value)
        {
            // Сохраняем в локальный кэш
            if (_options.UseLocalCache)
            {
                AddToLocalCache(key, value);
            }

            // Сохраняем в distributed cache
            await SaveToDistributedCacheAsync(key, value);

            _logger.LogTrace("Значение для ключа '{Key}' сохранено в кэш: {Value}", key, value);
        }

        /// <summary>
        /// Получить ключ кэша для разрешения (resource + scope)
        /// </summary>
        public async Task<string> GetPermissionCacheKeyAsync(string resource, string? scope)
        {
            await EnsureUserInfoLoadedAsync();

            return string.IsNullOrEmpty(scope)
                ? $"perm:{_cachedUserId}:{_cachedSessionId}:{resource}"
                : $"perm:{_cachedUserId}:{_cachedSessionId}:{resource}:{scope}";
        }

        /// <summary>
        /// Получить ключ кэша для политики
        /// </summary>
        public async Task<string> GetPolicyCacheKeyAsync(string policyName)
        {
            await EnsureUserInfoLoadedAsync();

            return $"policy:{_cachedUserId}:{_cachedSessionId}:{policyName}";
        }

        /// <summary>
        /// Получить ключ кэша для разрешения (resource + scope)
        /// </summary>
        public async Task<string> GetPermissionCacheKeyAsync(ClaimsPrincipal user, string resource, string? scope)
        {
            await EnsureUserInfoLoadedAsync(user);

            return string.IsNullOrEmpty(scope)
                ? $"perm:{_cachedUserId}:{_cachedSessionId}:{resource}"
                : $"perm:{_cachedUserId}:{_cachedSessionId}:{resource}:{scope}";
        }

        /// <summary>
        /// Получить ключ кэша для политики
        /// </summary>
        public async Task<string> GetPolicyCacheKeyAsync(ClaimsPrincipal user, string policyName)
        {
            await EnsureUserInfoLoadedAsync(user);

            return $"policy:{_cachedUserId}:{_cachedSessionId}:{policyName}";
        }

        /// <summary>
        /// Очистить весь кэш
        /// </summary>
        public async Task ClearCacheAsync()
        {
            _logger.LogWarning("Очистка всего кэша");

            _localCache.Clear();

            await Task.CompletedTask;
        }

        /// <summary>
        /// Получить статистику кэша
        /// </summary>
        public PermissionCacheStatistics GetStatistics()
        {
            return new PermissionCacheStatistics
            {
                LocalCacheCount = _localCache.Count,
                LocalCacheEnabled = _options.UseLocalCache,
                CacheDuration = _options.CacheDuration,
                LocalCacheDuration = _options.LocalCacheDuration,
                SlidingExpiration = _options.UseSlidingExpiration,
                IsAuthenticated = _wasAuthenticated
            };
        }

        // ========================================================================
        // ПРИВАТНЫЕ МЕТОДЫ
        // ========================================================================

        private async Task EnsureUserInfoLoadedAsync()
        {
            // Если уже загружено - возвращаем
            if (_cachedUserId != null && _cachedSessionId != null)
                return;

            var authState = await _authenticationStateProvider.GetAuthenticationStateAsync();
            var user = authState.User;

            if (user?.Identity?.IsAuthenticated != true)
            {
                _cachedUserId = "anonymous";
                _cachedSessionId = "none";
                return;
            }

            _cachedUserId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? user.FindFirst("sub")?.Value
                ?? user.FindFirst("preferred_username")?.Value
                ?? "unknown";

            _cachedSessionId = GetSessionIdFromUser(user);
        }

        private async Task EnsureUserInfoLoadedAsync(ClaimsPrincipal user)
        {
            // Если уже загружено - возвращаем
            if (_cachedUserId != null && _cachedSessionId != null)
                return;

            if (user?.Identity?.IsAuthenticated != true)
            {
                _cachedUserId = "anonymous";
                _cachedSessionId = "none";
                return;
            }

            _cachedUserId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? user.FindFirst("sub")?.Value
                ?? user.FindFirst("preferred_username")?.Value
                ?? "unknown";

            _cachedSessionId = GetSessionIdFromUser(user);
        }

        private string GetSessionIdFromUser(ClaimsPrincipal user)
        {
            var sid = user.FindFirst("sid")?.Value;
            if (!string.IsNullOrEmpty(sid))
                return sid;

            var sessionState = user.FindFirst("session_state")?.Value;
            if (!string.IsNullOrEmpty(sessionState))
                return sessionState;

            var authTime = user.FindFirst("auth_time")?.Value;
            var sub = user.FindFirst("sub")?.Value ?? "unknown";

            if (!string.IsNullOrEmpty(authTime))
                return ComputeHash($"{sub}:{authTime}");

            _logger.LogWarning("sid не найден, используется fallback");
            return ComputeHash(sub);
        }

        private string ComputeHash(string input)
        {
            using var sha256 = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(input);
            var hash = sha256.ComputeHash(bytes);
            var base64 = Convert.ToBase64String(hash);
            return base64.Length > 16 ? base64.Substring(0, 16) : base64;
        }

        private bool TryGetFromLocalCache(string key, out bool value)
        {
            value = false;

            if (_localCache.TryGetValue(key, out var cached))
            {
                if (cached.expiry > DateTime.UtcNow)
                {
                    value = cached.value;
                    return true;
                }

                _localCache.TryRemove(key, out _);
            }

            return false;
        }

        private void AddToLocalCache(string key, bool value)
        {
            var expiry = DateTime.UtcNow.Add(_options.LocalCacheDuration);
            _localCache[key] = (value, expiry);
        }

        private async Task SaveToDistributedCacheAsync(string key, bool value)
        {
            try
            {
                var options = new DistributedCacheEntryOptions();

                if (_options.UseSlidingExpiration)
                {
                    options.SlidingExpiration = _options.CacheDuration;
                }
                else
                {
                    options.AbsoluteExpirationRelativeToNow = _options.CacheDuration;
                }

                var bytes = SerializeCacheValue(value);
                await _distributedCache.SetAsync(key, bytes, options);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при сохранении в distributed cache: '{Key}'", key);
            }
        }

        private byte[] SerializeCacheValue(bool value)
        {
            var json = JsonSerializer.Serialize(value);
            return Encoding.UTF8.GetBytes(json);
        }

        private bool DeserializeCacheValue(byte[] bytes)
        {
            var json = Encoding.UTF8.GetString(bytes);
            return JsonSerializer.Deserialize<bool>(json);
        }

        private async void OnAuthenticationStateChanged(Task<AuthenticationState> authStateTask)
        {
            try
            {
                var authState = await authStateTask;
                var isAuthenticated = authState.User?.Identity?.IsAuthenticated == true;

                if (_wasAuthenticated == null)
                {
                    _wasAuthenticated = isAuthenticated;
                    return;
                }

                if (_wasAuthenticated == isAuthenticated)
                {
                    return;
                }

                // Пользователь стал неаутентифицированным
                if (_wasAuthenticated == true && isAuthenticated == false)
                {
                    _logger.LogWarning("Пользователь стал неаутентифицированным. Очистка кэша.");

                    _localCache.Clear();
                    _cachedUserId = null;
                    _cachedSessionId = null;
                }

                _wasAuthenticated = isAuthenticated;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при обработке изменения состояния аутентификации");
            }
        }

        private void CleanupExpiredLocalCache(object? state)
        {
            try
            {
                var now = DateTime.UtcNow;
                var expiredKeys = _localCache
                    .Where(kvp => kvp.Value.expiry <= now)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in expiredKeys)
                {
                    _localCache.TryRemove(key, out _);
                }

                if (expiredKeys.Count > 0)
                {
                    _logger.LogDebug("Фоновая очистка: удалено {Count} истекших записей", expiredKeys.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при фоновой очистке локального кэша");
            }
        }

        public void Dispose()
        {
            _authenticationStateProvider.AuthenticationStateChanged -= OnAuthenticationStateChanged;
            _cleanupTimer?.Dispose();

            _logger.LogDebug("PermissionCacheService disposed");
        }
    }

    public class PermissionCacheOptions
    {
        public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(10);
        public bool UseSlidingExpiration { get; set; } = true;
        public bool UseLocalCache { get; set; } = true;
        public TimeSpan LocalCacheDuration { get; set; } = TimeSpan.FromMinutes(1);
    }

    public class PermissionCacheStatistics
    {
        public int LocalCacheCount { get; set; }
        public bool LocalCacheEnabled { get; set; }
        public TimeSpan CacheDuration { get; set; }
        public TimeSpan LocalCacheDuration { get; set; }
        public bool SlidingExpiration { get; set; }
        public bool? IsAuthenticated { get; set; }
    }
}