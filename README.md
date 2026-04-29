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
- [Configuration](#️-configuration)
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

```bash
dotnet add package KeycloakAuthZ.Caching
