# Keycloak Permission Cache

[![NuGet](https://img.shields.io/nuget/v/KeycloakAuthZ.Caching.svg)](https://www.nuget.org/packages/KeycloakAuthZ.Caching/)
[![.NET](https://img.shields.io/badge/.NET-8.0+-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

Automatic caching for Keycloak permission checks in Blazor Server applications. Reduces Keycloak API calls by caching authorization results with two-level cache support (in-memory + distributed).

> ⚠️ **Note:** This library has not been tested in production environments.

## Features

- **Automatic caching** for `[Authorize]` policies using `RequireProtectedResource`
- **Two-level cache**: Local (in-memory) + Distributed (Memory/Redis/SQL Server)
- **Zero code changes** required for existing authorization logic
- **Optional `IPermissionService`** for programmatic permission checks

## Installation

~~~bash
dotnet add package KeycloakAuthZ.Caching
~~~

## Quick Start

~~~csharp
// Program.cs
builder.Services.AddKeycloakWebApiAuthentication(builder.Configuration);
builder.Services.AddKeycloakAuthorization(builder.Configuration);

// Add caching
builder.Services.AddPermissionService(options =>
{
    options.CacheDuration = TimeSpan.FromMinutes(10);
    options.LocalCacheDuration = TimeSpan.FromMinutes(1);
});
builder.Services.AddCachingDecorator();
~~~

## Usage

### Automatic Caching (Recommended)

~~~csharp
// Define policies
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("CanViewDocuments", policy =>
        policy.RequireProtectedResource("document", "view"));
~~~

~~~razor
@page "/documents"
@attribute [Authorize(Policy = "CanViewDocuments")]

<!-- Permission check is automatically cached -->
~~~

### Programmatic Checks (Optional)

~~~razor
@inject IPermissionService PermissionService

@code {
    private bool _canEdit;

    protected override async Task OnInitializedAsync()
    {
        _canEdit = await PermissionService.HasPermissionAsync("document", "edit");
    }
}
~~~

## Configuration Options

| Parameter | Default | Description |
|-----------|---------|-------------|
| `CacheDuration` | 10 min | Distributed cache TTL |
| `LocalCacheDuration` | 1 min | Local cache TTL |
| `UseSlidingExpiration` | true | Extend TTL on access |
| `UseLocalCache` | true | Enable local cache |

## Redis Support

~~~bash
dotnet add package Microsoft.Extensions.Caching.StackExchangeRedis
~~~

~~~csharp
builder.Services.AddPermissionServiceWithRedis("localhost:6379");
builder.Services.AddCachingDecorator();
~~~

## Documentation

See [full documentation](https://github.com/YaroslavkaPizza/KeycloakAuthZCaching) for detailed usage examples and API reference.

## License

MIT