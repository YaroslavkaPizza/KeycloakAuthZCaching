# Keycloak Permission Cache for Blazor Server

> A library for caching Keycloak permission check results in Blazor Server applications. Features two-level caching (in-memory + distributed) and integrates with standard ASP.NET Core authorization mechanisms.

[![.NET](https://img.shields.io/badge/.NET-8.0+-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Keycloak](https://img.shields.io/badge/Keycloak-Compatible-blue?logo=keycloak)](https://www.keycloak.org/)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

> ⚠️ **Note:** This library has not been tested in production environments. Use at your own risk and conduct thorough testing before deploying to production.

---

## 📋 Table of Contents

- [About](#-about)
- [Installation](#-installation)
- [Quick Start](#-quick-start)
- [Configuration](#-configuration)
- [Usage Examples](#-usage-examples)
- [Architecture](#-architecture)
- [API Reference](#-api-reference)
- [FAQ](#-faq)

---

## 📖 About

### Core Concept

This library provides **automatic caching** for all Keycloak Protection API calls when using standard ASP.NET Core authorization mechanisms:

- `[Authorize]` attribute with policies based on `RequireProtectedResource`
- Direct calls to `IAuthorizationService.AuthorizeAsync`
- Any calls to `IAuthorizationServerClient` (from `Keycloak.AuthServices.Authorization`)

### What Gets Cached

✅ **Automatically cached:**
- Permission checks via `[Authorize(Policy = "...")]`
- Calls to `IAuthorizationServerClient.VerifyAccessToResource`
- Checks via `IAuthorizationService.AuthorizeAsync`

❌ **No code changes required** — caching works transparently.

### Optional PermissionService

`IPermissionService` is provided for **programmatic permission checks** in component and service code. Its use is optional — the library's core functionality works through the `CachingAuthorizationServerClientDecorator`.

### Features

- **Two-level caching**: Local (in-memory) + Distributed cache (Memory/Redis/SQL)
- **Request deduplication**: Parallel checks for the same permission are executed only once
- **Lifecycle management**: Automatic cache clearing on user logout
- **Scoped architecture**: Compatible with Blazor Server Circuit

---

## 📦 Installation

### Requirements

- .NET 8.0+
- Blazor Server
- Keycloak.AuthServices.Authentication 2.0+
- Keycloak.AuthServices.Authorization 2.0+

### NuGet Package

    dotnet add package KeycloakAuthZ.Caching

The package includes all necessary dependencies:
- `Keycloak.AuthServices.Authentication`
- `Keycloak.AuthServices.Authorization`
- `Microsoft.Extensions.Caching.Abstractions`

### Optional Dependencies

For Redis support:

    dotnet add package Microsoft.Extensions.Caching.StackExchangeRedis

For SQL Server cache support:

    dotnet add package Microsoft.Extensions.Caching.SqlServer

---

## 🚀 Quick Start

### Step 1: Configure Keycloak

    // appsettings.json
    {
      "Keycloak": {
        "realm": "your-realm",
        "auth-server-url": "https://your-keycloak-server/",
        "ssl-required": "external",
        "resource": "your-client-id",
        "credentials": {
          "secret": "your-client-secret"
        },
        "confidential-port": 0,
        "verify-token-audience": false
      }
    }

### Step 2: Register Services (Program.cs)

    using YourPackageNamespace.Extensions;
    
    var builder = WebApplication.CreateBuilder(args);
    
    // 1. Standard Keycloak setup
    builder.Services.AddKeycloakWebApiAuthentication(builder.Configuration);
    builder.Services.AddKeycloakAuthorization(builder.Configuration);
    
    // 2. Add permission caching service
    builder.Services.AddPermissionService(options =>
    {
        options.CacheDuration = TimeSpan.FromMinutes(10);
        options.LocalCacheDuration = TimeSpan.FromMinutes(1);
        options.UseSlidingExpiration = true;
        options.UseLocalCache = true;
    });
    
    // 3. Add decorator for automatic caching
    builder.Services.AddCachingDecorator();
    
    var app = builder.Build();
    
    app.UseAuthentication();
    app.UseAuthorization();
    
    app.Run();

### Step 3: Configure Authorization Policies

    builder.Services.AddAuthorizationBuilder()
        .AddPolicy("CanViewDocuments", policy =>
            policy.RequireProtectedResource("document", "view"))
        .AddPolicy("CanEditDocuments", policy =>
            policy.RequireProtectedResource("document", "edit"))
        .AddPolicy("CanDeleteDocuments", policy =>
            policy.RequireProtectedResource("document", "delete"));

### Step 4: Use in Components

    @page "/documents"
    @attribute [Authorize(Policy = "CanViewDocuments")]
    
    <h3>Documents</h3>
    
    <p>Document list...</p>
    
    @code {
        // Component code
    }

---

## ⚙️ Configuration

### Option 1: In-Memory Cache (Default)

    builder.Services.AddPermissionService(options =>
    {
        options.CacheDuration = TimeSpan.FromMinutes(10);
        options.LocalCacheDuration = TimeSpan.FromSeconds(30);
        options.UseSlidingExpiration = true;
        options.UseLocalCache = true;
    });
    
    builder.Services.AddCachingDecorator();

**Use case:** Development, testing, single-server environments.

---

### Option 2: Redis Cache

    builder.Services.AddPermissionServiceWithRedis(
        redisConnectionString: "localhost:6379",
        configureOptions: options =>
        {
            options.CacheDuration = TimeSpan.FromMinutes(30);
            options.LocalCacheDuration = TimeSpan.FromMinutes(5);
            options.UseSlidingExpiration = true;
            options.UseLocalCache = true;
        });
    
    builder.Services.AddCachingDecorator();

**Use case:** Production environments, multi-server deployments, high load.

---

### Option 3: SQL Server Distributed Cache

    // 1. Create cache table
    // dotnet sql-cache create "YourConnectionString" dbo PermissionCache
    
    // 2. Configure services
    builder.Services.AddDistributedSqlServerCache(options =>
    {
        options.ConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
        options.SchemaName = "dbo";
        options.TableName = "PermissionCache";
    });
    
    builder.Services.AddPermissionServiceWithCustomCache(options =>
    {
        options.CacheDuration = TimeSpan.FromMinutes(20);
        options.LocalCacheDuration = TimeSpan.FromMinutes(2);
    });
    
    builder.Services.AddCachingDecorator();

**Use case:** SQL Server-based infrastructure without Redis.

---

### Configuration Parameters

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `CacheDuration` | `TimeSpan` | `10 minutes` | Entry lifetime in distributed cache |
| `LocalCacheDuration` | `TimeSpan` | `1 minute` | Entry lifetime in local in-memory cache |
| `UseSlidingExpiration` | `bool` | `true` | Extend entry lifetime on each access |
| `UseLocalCache` | `bool` | `true` | Use local cache to reduce distributed cache calls |

---

## 💡 Usage Examples

### 1. Using [Authorize] Attribute (Recommended)

    @page "/documents"
    @attribute [Authorize(Policy = "CanViewDocuments")]
    
    <h3>Documents</h3>
    
    <p>Document list...</p>
    
    @code {
        // If the user reached this page, the check passed successfully
    }

---

### 2. Programmatic Check via IPermissionService (Optional)

    @page "/documents"
    @inject IPermissionService PermissionService
    
    <h3>Documents</h3>
    
    <p>Document list...</p>
    
    @if (_canEdit)
    {
        <button @onclick="EditDocument">Edit</button>
    }
    
    @if (_canDelete)
    {
        <button @onclick="DeleteDocument">Delete</button>
    }
    
    @code {
        private bool _canEdit;
        private bool _canDelete;
    
        protected override async Task OnInitializedAsync()
        {
            _canEdit = await PermissionService.HasPermissionAsync("document", "edit");
            _canDelete = await PermissionService.HasPermissionAsync("document", "delete");
        }
    }

---

### 3. Batch Check for Multiple Permissions

    @inject IPermissionService PermissionService
    
    @code {
        private async Task CheckMultiplePermissions()
        {
            var results = await PermissionService.HasPermissionsAsync(
                ("document", "read"),
                ("document", "write"),
                ("document", "delete"),
                ("user", "manage")
            );
    
            foreach (var (key, hasAccess) in results)
            {
                Console.WriteLine($"{key}: {hasAccess}");
            }
        }
    }

---

### 4. Using IPermission Objects

    using YourPackageNamespace.Extensions;
    
    @inject IPermissionService PermissionService
    
    @code {
        private async Task CheckWithPermissionObjects()
        {
            var permissions = new IPermission[]
            {
                new Permission("document", "read"),
                new Permission("document", "write"),
                new Permission("user", "manage")
            };
    
            var results = await PermissionService.HasPermissionsAsync(permissions);
            bool hasAny = await PermissionService.HasAnyPermissionAsync(permissions);
            bool hasAll = await PermissionService.HasAllPermissionsAsync(permissions);
            await PermissionService.PreloadPermissionsAsync(permissions);
        }
    }

---

### 5. Using Attributes for Methods

    using YourPackageNamespace.Extensions;
    
    public class DocumentService
    {
        [RequirePermission("document", "read")]
        public async Task ReadDocumentAsync()
        {
            // Document reading logic
        }
    
        [RequirePolicy("AdminOnly")]
        public async Task DeleteAllDocumentsAsync()
        {
            // Delete all documents logic
        }
    }
    
    @inject IPermissionService PermissionService
    @inject DocumentService DocumentService
    
    @code {
        private async Task TryReadDocument()
        {
            bool wasExecuted = await PermissionService.ExecuteIfAuthorizedAsync(
                DocumentService,
                service => service.ReadDocumentAsync()
            );
    
            if (!wasExecuted)
            {
                Console.WriteLine("Access denied");
            }
        }
    }

---

### 6. Permission Preloading

    @inject IPermissionService PermissionService
    
    @code {
        protected override async Task OnInitializedAsync()
        {
            await PermissionService.PreloadPermissionsAsync(
                ("document", "read"),
                ("document", "write"),
                ("document", "delete")
            );
    
            await PermissionService.PreloadPoliciesAsync(
                "AdminOnly",
                "ManagerOnly",
                "UserOnly"
            );
        }
    }

---

### 7. Cache Management

    @inject IPermissionCacheService CacheService
    
    @code {
        private async Task ManageCache()
        {
            var stats = CacheService.GetStatistics();
            Console.WriteLine($"Local cache entries: {stats.LocalCacheCount}");
            Console.WriteLine($"Local cache enabled: {stats.LocalCacheEnabled}");
            Console.WriteLine($"User authenticated: {stats.IsAuthenticated}");
    
            await CacheService.ClearAllAsync();
    
            var key = await CacheService.GetPermissionCacheKeyAsync("document", "read");
            Console.WriteLine($"Cache key: {key}");
        }
    }

---

### 8. Form Integration

    @page "/edit-document/{DocumentId}"
    @inject IPermissionService PermissionService
    @inject NavigationManager Navigation
    
    <h3>Edit Document</h3>
    
    @if (_isLoading)
    {
        <p>Loading...</p>
    }
    else if (!_canEdit)
    {
        <p>You don't have permission to edit this document</p>
        <button @onclick="GoBack">Back</button>
    }
    else
    {
        <EditForm Model="@_document" OnValidSubmit="SaveDocument">
            <DataAnnotationsValidator />
            
            <InputText @bind-Value="_document.Title" />
            <InputTextArea @bind-Value="_document.Content" />
            
            <button type="submit" disabled="@(!_canSave)">Save</button>
            
            @if (_canDelete)
            {
                <button @onclick="DeleteDocument" class="btn-danger">Delete</button>
            }
        </EditForm>
    }
    
    @code {
        [Parameter] public string DocumentId { get; set; } = string.Empty;
    
        private bool _isLoading = true;
        private bool _canEdit;
        private bool _canSave;
        private bool _canDelete;
        private DocumentModel _document = new();
    
        protected override async Task OnInitializedAsync()
        {
            var permissions = await PermissionService.HasPermissionsAsync(
                ("document", "edit"),
                ("document", "save"),
                ("document", "delete")
            );
    
            foreach (var (key, value) in permissions)
            {
                if (key.Contains("edit")) _canEdit = value;
                if (key.Contains("save")) _canSave = value;
                if (key.Contains("delete")) _canDelete = value;
            }
    
            _isLoading = false;
    
            if (_canEdit)
            {
                await LoadDocument();
            }
        }
    
        private async Task SaveDocument()
        {
            if (!await PermissionService.HasPermissionAsync("document", "save"))
            {
                return;
            }
        }
    
        private void GoBack() => Navigation.NavigateTo("/documents");
        private Task LoadDocument() => Task.CompletedTask;
        private Task DeleteDocument() => Task.CompletedTask;
    }

---

## 🏗️ Architecture

### Caching Flow Diagram

    ┌─────────────────────────────────────────────────────────────┐
    │  Blazor Component                                           │
    │  [Authorize(Policy = "CanViewDocuments")]                   │
    └────────────────────────┬────────────────────────────────────┘
                             │
                             ▼
    ┌─────────────────────────────────────────────────────────────┐
    │  ASP.NET Core Authorization Pipeline                        │
    │  ├─ AuthorizationMiddleware                                │
    │  └─ ProtectedResourcePolicyHandler                         │
    │     (from Keycloak.AuthServices.Authorization)             │
    └────────────────────────┬────────────────────────────────────┘
                             │
                             ▼
    ┌─────────────────────────────────────────────────────────────┐
    │  CachingAuthorizationServerClientDecorator                  │
    │  ├─ 1. Check PermissionCacheService                        │
    │  ├─ 2. If cached → return result                           │
    │  └─ 3. If not → call IAuthorizationServerClient           │
    └────────────────────────┬────────────────────────────────────┘
                             │
                             ▼
    ┌─────────────────────────────────────────────────────────────┐
    │  IAuthorizationServerClient                                 │
    │  (original implementation from Keycloak.AuthServices)       │
    │  └─ HTTP request to Keycloak Protection API                │
    └─────────────────────────────────────────────────────────────┘

### Two-Level Cache

    ┌─────────────────────────────────────────────────────────────┐
    │  PermissionCacheService                                     │
    │                                                             │
    │  ┌──────────────────────────────────────────────────────┐  │
    │  │  Level 1: Local Cache (In-Memory)                    │  │
    │  │  - ConcurrentDictionary<string, (bool, DateTime)>    │  │
    │  │  - TTL: 30 seconds - 5 minutes                       │  │
    │  │  - Scoped (Circuit level)                            │  │
    │  └──────────────────────────────────────────────────────┘  │
    │                          │                                  │
    │                          ▼                                  │
    │  ┌──────────────────────────────────────────────────────┐  │
    │  │  Level 2: Distributed Cache                          │  │
    │  │  - IDistributedCache (Memory/Redis/SQL)              │  │
    │  │  - TTL: 5-30 minutes                                 │  │
    │  │  - Shared (across servers)                           │  │
    │  └──────────────────────────────────────────────────────┘  │
    └─────────────────────────────────────────────────────────────┘

### Lifecycle Management

    ┌─────────────────────────────────────────────────────────────┐
    │  AuthenticationStateProvider                                │
    │  └─ AuthenticationStateChanged event                       │
    └────────────────────────┬────────────────────────────────────┘
                             │
                             ▼
    ┌─────────────────────────────────────────────────────────────┐
    │  PermissionCacheService                                     │
    │  ├─ Track authentication status                            │
    │  ├─ Clear cache on Authenticated → Unauthenticated         │
    │  └─ Background cleanup of expired entries (every minute)   │
    └─────────────────────────────────────────────────────────────┘

---

## 📚 API Reference

### IPermissionService (Optional)

Interface for programmatic permission checks in component code.

| Method | Description |
|--------|-------------|
| `HasPermissionAsync(resource, scope)` | Check permission for a resource |
| `HasPolicyAsync(policyName)` | Check policy execution |
| `CanAsync(permissionOrPolicy, scope)` | Universal check (auto-detects type) |
| `HasPermissionsAsync(params tuples)` | Batch check multiple permissions in parallel |
| `HasPoliciesAsync(params policyNames)` | Batch check multiple policies in parallel |
| `PreloadPermissionsAsync(params tuples)` | Preload permissions into cache |
| `PreloadPoliciesAsync(params policyNames)` | Preload policies into cache |

### IPermissionCacheService

Interface for permission cache management.

| Method | Description |
|--------|-------------|
| `GetAsync(key)` | Get value from cache by key |
| `SetAsync(key, value)` | Save value to cache |
| `GetPermissionCacheKeyAsync(resource, scope)` | Get cache key for permission |
| `GetPolicyCacheKeyAsync(policyName)` | Get cache key for policy |
| `ClearCacheAsync()` | Clear all cache for current user |
| `GetStatistics()` | Get cache usage statistics |

### Extension Methods

| Method | Description |
|--------|-------------|
| `ExecuteIfAuthorizedAsync(instance, expression)` | Execute method only if permission exists (checks via attributes) |
| `HasPermissionAsync(IPermission)` | Check permission via IPermission object |
| `HasPermissionsAsync(params IPermission[])` | Batch check via IPermission objects |
| `HasAnyPermissionAsync(params IPermission[])` | Check if at least one permission exists |
| `HasAllPermissionsAsync(params IPermission[])` | Check if all permissions exist |
| `PreloadPermissionsAsync(params IPermission[])` | Preload via IPermission objects |

---

## ❓ FAQ

### How does automatic caching work?

The `CachingAuthorizationServerClientDecorator` intercepts all calls to `IAuthorizationServerClient`, which is used by `Keycloak.AuthServices.Authorization` to check permissions. This provides automatic caching when using:
- `[Authorize]` attribute with policies based on `RequireProtectedResource`
- Direct calls to `IAuthorizationService.AuthorizeAsync`
- Any calls to Keycloak Protection API via `IAuthorizationServerClient`

### Do I need to manually clear the cache?

The cache is automatically cleared in the following cases:
- On user logout (transition from Authenticated to Unauthenticated state)
- On entry TTL expiration
- During background cleanup of expired entries (every minute)

**Important:** Automatic cache clearing when an administrator terminates a session in Keycloak is not implemented. If this functionality is needed, use a short TTL for critical permissions.

### How does the cache work when refreshing tokens?

Cache keys are tied to `sessionId` from the JWT `sid` claim, which does not change when refreshing tokens. The cache remains valid throughout the user's Keycloak session.

### Can I use it without Redis?

Yes. By default, `IDistributedMemoryCache` is used, which stores data in process memory. Redis is only necessary for multi-server environments where a shared cache between servers is required.

### How does caching affect security?

Caching creates a delay between permission changes in Keycloak and their application in the app. To minimize this delay:
- Use a short `CacheDuration` (5-10 minutes)
- For critical operations, set an even shorter TTL (1-2 minutes)
- Consider that permission changes in Keycloak will take effect after the cache entry TTL expires

### Is IPermissionService mandatory?

No. `IPermissionService` is provided for convenience in programmatic checks. The library's core functionality (automatic caching) works through the decorator and doesn't require `IPermissionService`. Use standard ASP.NET Core mechanisms (`[Authorize]`, `IAuthorizationService`) — they automatically use the cache.

### How does it work in multi-server environments?

In multi-server environments, use Redis or SQL Server as the distributed cache:

    builder.Services.AddPermissionServiceWithRedis("your-redis-connection-string");
    builder.Services.AddCachingDecorator();

The local in-memory cache works independently on each server (reduces Redis calls), while the distributed cache is shared across all servers.

### What to do when user permissions change in Keycloak?

Changes will take effect automatically after the cache entry TTL expires. For immediate application of changes:
- Ask the user to logout/login (cache will be cleared automatically)
- Call `IPermissionCacheService.ClearCacheAsync()` programmatically (clears cache for current user)

### Is this library production-ready?

This library has not been tested in production environments. Before deploying to production:
- Conduct thorough testing in staging environments
- Monitor cache performance and hit rates
- Validate security implications for your use case
- Consider implementing additional monitoring and logging

---

## 📝 License

MIT License - see [LICENSE](LICENSE)

---

## 🤝 Contributing

Pull requests are welcome. For major changes, please open an issue first to discuss proposed changes.

---

## 📧 Support

For questions or issues, please create an [issue](https://github.com/your-repo/issues) in the project repository.
