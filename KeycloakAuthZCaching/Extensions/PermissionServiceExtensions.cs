using Keycloak.AuthServices.Authorization.AuthorizationServer;
using KeycloakAuthZCaching.Services.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;

namespace KeycloakAuthZCaching.Plumbing.Authorization.Extensions
{
    public static class PermissionServiceExtensions
    {
        /// <summary>
        /// Добавляет кэширование результатов проверки разрешений Keycloak. Использовать после вызова AddKeycloakAuthorization
        /// </summary>
        /// <param name="services"></param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        public static IServiceCollection AddCachingDecorator(this IServiceCollection services)
        {
            var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IAuthorizationServerClient));

            if (descriptor != null)
            {
                services.Remove(descriptor);

                services.AddScoped<IAuthorizationServerClient>(sp =>
                {
                    IAuthorizationServerClient inner;

                    // Если библиотека использовала фабрику (самый вероятный случай)
                    if (descriptor.ImplementationFactory != null)
                    {
                        inner = (IAuthorizationServerClient)descriptor.ImplementationFactory(sp);
                    }
                    // Если был указан конкретный тип реализации
                    else if (descriptor.ImplementationType != null)
                    {
                        inner = (IAuthorizationServerClient)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType);
                    }
                    // Если был передан уже готовый экземпляр
                    else if (descriptor.ImplementationInstance != null)
                    {
                        inner = (IAuthorizationServerClient)descriptor.ImplementationInstance;
                    }
                    else
                    {
                        throw new InvalidOperationException("Не удалось найти способ создания IAuthorizationServerClient");
                    }

                    var cacheService = sp.GetRequiredService<IPermissionCacheService>();
                    return new CachingAuthorizationServerClientDecorator(inner, cacheService);
                });
            }

            return services;
        }

        /// <summary>
        /// Добавляет Permission Service с in-memory distributed cache
        /// </summary>
        public static IServiceCollection AddPermissionService(
            this IServiceCollection services,
            Action<PermissionCacheOptions>? configureOptions = null)
        {
            // Настраиваем опции кэша
            if (configureOptions != null)
            {
                services.Configure(configureOptions);
            }
            else
            {
                services.Configure<PermissionCacheOptions>(options => { });
            }

            // In-memory distributed cache
            services.AddDistributedMemoryCache();

            // Регистрируем сервисы
            services.AddScoped<IPermissionCacheService, PermissionCacheService>();
            services.AddScoped<IPermissionService, PermissionService>();

            // Регистрируем Authorization Handler для автоматического кэширования
            services.AddSingleton<IAuthorizationHandler, KeycloakCacheAuthorizationHandler>();

            return services;
        }

        /// <summary>
        /// Добавляет Permission Service с Redis distributed cache
        /// </summary>
        public static IServiceCollection AddPermissionServiceWithRedis(
            this IServiceCollection services,
            string redisConnectionString,
            Action<PermissionCacheOptions>? configureOptions = null)
        {
            // Настраиваем опции кэша
            if (configureOptions != null)
            {
                services.Configure(configureOptions);
            }
            else
            {
                services.Configure<PermissionCacheOptions>(options => { });
            }

            // Redis distributed cache
            services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redisConnectionString;
                options.InstanceName = "PermissionCache_";
            });

            // Регистрируем сервисы
            services.AddScoped<IPermissionCacheService, PermissionCacheService>();
            services.AddScoped<IPermissionService, PermissionService>();

            // Регистрируем Authorization Handler
            services.AddScoped<IAuthorizationHandler, KeycloakCacheAuthorizationHandler>();

            return services;
        }

        /// <summary>
        /// Добавляет Permission Service с Redis distributed cache
        /// </summary>
        public static IServiceCollection AddPermissionServiceWithRedis(
            this IServiceCollection services,
            string redisConnectionString,
            string? instanceName = "PermissionCache_",
            Action<PermissionCacheOptions>? configureOptions = null)
        {
            // Настраиваем опции кэша
            if (configureOptions != null)
            {
                services.Configure(configureOptions);
            }
            else
            {
                services.Configure<PermissionCacheOptions>(options => { });
            }

            // Redis distributed cache
            services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redisConnectionString;
                options.InstanceName = instanceName;
            });

            // Регистрируем сервисы
            services.AddScoped<IPermissionCacheService, PermissionCacheService>();
            services.AddScoped<IPermissionService, PermissionService>();

            // Регистрируем Authorization Handler
            services.AddScoped<IAuthorizationHandler, KeycloakCacheAuthorizationHandler>();

            return services;
        }

        /// <summary>
        /// Добавляет Permission Service с кастомным IDistributedCache
        /// </summary>
        public static IServiceCollection AddPermissionServiceWithCustomCache(
            this IServiceCollection services,
            Action<PermissionCacheOptions>? configureOptions = null)
        {
            // Настраиваем опции кэша
            if (configureOptions != null)
            {
                services.Configure(configureOptions);
            }
            else
            {
                services.Configure<PermissionCacheOptions>(options => { });
            }

            // НЕ добавляем IDistributedCache - пользователь должен зарегистрировать его сам

            // Регистрируем сервисы
            services.AddScoped<IPermissionCacheService, PermissionCacheService>();
            services.AddScoped<IPermissionService, PermissionService>();

            // Регистрируем Authorization Handler
            services.AddScoped<IAuthorizationHandler, KeycloakCacheAuthorizationHandler>();

            return services;
        }

        // ========================================================================
        // EXTENSION МЕТОДЫ ДЛЯ IPermissionService
        // ========================================================================

        /// <summary>
        /// Выполняет метод, если есть необходимые разрешения (проверка через атрибуты)
        /// </summary>
        public static async Task<bool> ExecuteIfAuthorizedAsync<T>(
            this IPermissionService permissionService,
            T instance,
            Expression<Func<T, Task>> methodExpression)
        {
            if (methodExpression.Body is not MethodCallExpression methodCall)
            {
                throw new ArgumentException("Expression must be a method call", nameof(methodExpression));
            }

            var method = methodCall.Method;

            // Проверяем RequirePermission
            var permissionAttr = method.GetCustomAttribute<RequirePermissionAttribute>();
            if (permissionAttr != null)
            {
                var hasPermission = await permissionService.HasPermissionAsync(
                    permissionAttr.Resource,
                    permissionAttr.Scope);

                if (!hasPermission)
                {
                    return false;
                }
            }

            // Проверяем RequirePolicy
            var policyAttr = method.GetCustomAttribute<RequirePolicyAttribute>();
            if (policyAttr != null)
            {
                var hasPolicy = await permissionService.HasPolicyAsync(policyAttr.PolicyName);

                if (!hasPolicy)
                {
                    return false;
                }
            }

            // Выполняем метод
            var compiledMethod = methodExpression.Compile();
            await compiledMethod(instance);

            return true;
        }

        /// <summary>
        /// Проверяет разрешение используя объект Permission
        /// </summary>
        public static Task<bool> HasPermissionAsync(
            this IPermissionService service,
            IPermission permission)
        {
            return service.HasPermissionAsync(permission.Resource, permission.Scope);
        }

        /// <summary>
        /// Проверяет несколько разрешений одновременно
        /// </summary>
        public static async Task<Dictionary<IPermission, bool>> HasPermissionsAsync(
            this IPermissionService service,
            params IPermission[] permissions)
        {
            var tuples = permissions.Select(p => (p.Resource, p.Scope)).ToArray();
            var results = await service.HasPermissionsAsync(tuples);

            // Маппим результаты обратно на IPermission объекты
            var mappedResults = new Dictionary<IPermission, bool>();

            foreach (var permission in permissions)
            {
                // Ищем соответствующий результат в словаре
                var matchingResult = results.FirstOrDefault(kvp =>
                    kvp.Key.Contains(permission.Resource) &&
                    (string.IsNullOrEmpty(permission.Scope) || kvp.Key.Contains(permission.Scope!)));

                mappedResults[permission] = matchingResult.Value;
            }

            return mappedResults;
        }

        /// <summary>
        /// Проверяет, есть ли хотя бы одно из указанных разрешений
        /// </summary>
        public static async Task<bool> HasAnyPermissionAsync(
            this IPermissionService service,
            params IPermission[] permissions)
        {
            var results = await service.HasPermissionsAsync(permissions);
            return results.Values.Any(v => v);
        }

        /// <summary>
        /// Проверяет, есть ли все указанные разрешения
        /// </summary>
        public static async Task<bool> HasAllPermissionsAsync(
            this IPermissionService service,
            params IPermission[] permissions)
        {
            var results = await service.HasPermissionsAsync(permissions);
            return results.Values.All(v => v);
        }

        /// <summary>
        /// Предзагружает разрешения
        /// </summary>
        public static Task PreloadPermissionsAsync(
            this IPermissionService service,
            params IPermission[] permissions)
        {
            var tuples = permissions.Select(p => (p.Resource, p.Scope)).ToArray();
            return service.PreloadPermissionsAsync(tuples);
        }

        // ========================================================================
        // EXTENSION МЕТОДЫ ДЛЯ IPermissionCacheService
        // ========================================================================

        /// <summary>
        /// Получить статистику кэша (удобный extension)
        /// </summary>
        public static PermissionCacheStatistics GetStatistics(this IPermissionCacheService cacheService)
        {
            return cacheService.GetStatistics();
        }

        /// <summary>
        /// Очистить весь кэш (удобный extension)
        /// </summary>
        public static Task ClearAllAsync(this IPermissionCacheService cacheService)
        {
            return cacheService.ClearCacheAsync();
        }
    }

    // ========================================================================
    // ИНТЕРФЕЙСЫ И АТРИБУТЫ
    // ========================================================================

    /// <summary>
    /// Интерфейс для объектов разрешений
    /// </summary>
    public interface IPermission
    {
        string Resource { get; }
        string? Scope { get; }
    }

    /// <summary>
    /// Реализация по умолчанию
    /// </summary>
    public class Permission : IPermission
    {
        public string Resource { get; set; } = string.Empty;
        public string? Scope { get; set; }

        public Permission() { }

        public Permission(string resource, string? scope = null)
        {
            Resource = resource;
            Scope = scope;
        }
    }

    /// <summary>
    /// Атрибут для указания требуемого разрешения на методе
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class RequirePermissionAttribute : Attribute
    {
        public string Resource { get; }
        public string? Scope { get; }

        public RequirePermissionAttribute(string resource, string? scope = null)
        {
            Resource = resource;
            Scope = scope;
        }
    }

    /// <summary>
    /// Атрибут для указания требуемой политики на методе
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class RequirePolicyAttribute : Attribute
    {
        public string PolicyName { get; }

        public RequirePolicyAttribute(string policyName)
        {
            PolicyName = policyName;
        }
    }
}