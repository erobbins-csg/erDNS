# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Technitium DNS Server is a cross-platform, open-source authoritative and recursive DNS server written in C# targeting .NET 9. It provides a web-based admin console on port 5380 and supports encrypted DNS transports (DoT, DoH, DoQ).

## Build Requirements

This project depends on [TechnitiumLibrary](https://github.com/TechnitiumSoftware/TechnitiumLibrary), which must be cloned as a sibling directory alongside this repo:

```
parent/
  TechnitiumLibrary/   ← must exist here
  DnsServer/           ← this repo
```

## Build Commands

```bash
# Build TechnitiumLibrary dependencies first (run from parent directory)
dotnet build TechnitiumLibrary/TechnitiumLibrary.ByteTree/TechnitiumLibrary.ByteTree.csproj -c Release
dotnet build TechnitiumLibrary/TechnitiumLibrary.Net/TechnitiumLibrary.Net.csproj -c Release
dotnet build TechnitiumLibrary/TechnitiumLibrary.Security.OTP/TechnitiumLibrary.Security.OTP.csproj -c Release

# Build/publish the main application (from this repo's parent)
dotnet publish DnsServer/DnsServerApp/DnsServerApp.csproj -c Release

# Build solution (from repo root)
dotnet build DnsServer.sln -c Release

# Docker build (from repo root)
docker build -t technitium/dns-server:latest .
docker compose up -d
```

## Tests

There is no automated test suite in this repository. Testing is done manually via the web console (port 5380) or DNS client tools (`dig`, `nslookup`).

## Architecture

### Entry Point & Service Initialization

`DnsServerApp/Program.cs` starts the application and instantiates `DnsWebService` (in `DnsServerCore/`), which is the top-level orchestrator. It initializes all subsystems: DNS engine, DHCP server, authentication, clustering, logging, and all web API handlers.

### Core DNS Engine: `DnsServerCore/Dns/DnsServer.cs`

The largest and most critical file (~345KB). It handles:
- Incoming queries over UDP, TCP, DoT, DoH (HTTP/1.1, HTTP/2, HTTP/3), and DoQ
- Recursive resolution with caching, prefetching, and DNSSEC validation
- Authoritative responses via zone lookups
- Plugin/app processing pipeline (request handlers → zone lookup/recursion → post-processors)

### Zone System: `DnsServerCore/Dns/Zones/` and `ZoneManagers/`

Zone types: Primary, Secondary, Stub, Forwarder, Catalog (RFC 9432), and CatalogSubDomain. Zone trees (`Trees/`) use trie-based domain tree structures for fast lookup. `AuthZoneManager` manages zone lifecycle (load, create, delete, DNSSEC signing).

### Plugin/App System: `Apps/`

28+ built-in DNS applications loaded dynamically at runtime. Each app implements interfaces from `DnsServerCore.ApplicationCommon/`:
- `IDnsApplication` — base lifecycle interface
- `IDnsRequestHandler` / `IDnsAuthoritativeRequestHandler` — intercept queries before resolution
- `IDnsPostProcessor` — modify responses after resolution
- `IDnsQueryLogger` — log queries to external sinks (SQLite, MySQL, SQL Server)
- `IDnsRequestBlockingHandler` — block queries matching criteria

Apps are stored in `bin/apps/<AppName>/` and configured via `dnsApp.config` JSON files.

### Web API Layer: `DnsServerCore/WebServiceXxxApi.cs`

Each feature area has a dedicated API class instantiated and owned by `DnsWebService`:
- `WebServiceZonesApi` — zone CRUD, import/export, DNSSEC management
- `WebServiceSettingsApi` — server configuration
- `WebServiceAuthApi` — user/group management, TOTP 2FA
- `WebServiceDashboardApi` — statistics and monitoring
- `WebServiceAppsApi` — plugin install/configure/update
- `WebServiceDhcpApi` — DHCP scope management
- `WebServiceClusterApi` — multi-node cluster management
- `WebServiceLogsApi` — query and system log access

The web console static files live in `DnsServerCore/www/`.

### Supporting Libraries

- `DnsServerCore.ApplicationCommon/` — public interfaces for plugin authors
- `DnsServerCore.HttpApi/` — HTTP client library for programmatic server control (used by clustering and external tooling)

### Key Supporting Services in `DnsServerCore/`

- `StatsManager.cs` — collects and aggregates DNS query statistics
- `LogManager.cs` — system and query log management
- `Auth/` — role-based access control, user/group management
- `Dhcp/` — full DHCP server implementation with multi-scope support
- `Cluster/` — multi-node clustering with centralized admin

## REST API

Full API documentation is in [APIDOCS.md](APIDOCS.md). Authentication uses session tokens (from `/api/user/login`) or long-lived API tokens. 2FA is TOTP-based.
