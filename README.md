<h1 align="center">Discordtest</h1>

<p align="center">
  A work-in-progress Discord clone built with ASP.NET Core: servers, channels, and real-time messaging over WebSockets.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/ASP.NET%20Core-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt="ASP.NET Core">
  <img src="https://img.shields.io/badge/.NET%209-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 9">
  <img src="https://img.shields.io/badge/C%23-239120?style=for-the-badge&logo=csharp&logoColor=white" alt="C#">
  <img src="https://img.shields.io/badge/Discord-clone-5865F2?style=for-the-badge&logo=discord&logoColor=white" alt="Discord clone">
  <img src="https://img.shields.io/badge/status-WIP-orange?style=for-the-badge" alt="Status: WIP">
</p>

<p align="center">
  <img src="https://img.shields.io/badge/JWT-000000?logo=jsonwebtokens&logoColor=white" alt="JWT">
  <img src="https://img.shields.io/badge/WebSockets-010101?logo=socketdotio&logoColor=white" alt="WebSockets">
  <img src="https://img.shields.io/badge/Redis-DC382D?logo=redis&logoColor=white" alt="Redis">
  <img src="https://img.shields.io/badge/OpenAPI-6BA539?logo=openapiinitiative&logoColor=white" alt="OpenAPI">
</p>

<p align="center">
  <a href="#features">Features</a> •
  <a href="#tech-stack">Tech Stack</a> •
  <a href="#installation">Installation</a> •
  <a href="#configuration">Configuration</a> •
  <a href="#project-structure">Structure</a>
</p>

> [!NOTE]
> This project is a **work in progress** and not production-ready.

---

## Features

- **JWT authentication**: bearer-token auth with signed tokens
- **CSRF protection**: custom middleware on every `/api` route
- **Real-time messaging**: WebSocket sessions and per-channel connection tracking
- **Servers and channels**: server-to-user and channel-to-socket connection management
- **Default avatars**: preloaded default avatar set for new users
- **Background workers**: hosted workers for background tasks
- **Rate limiting**: fixed-window limiter policy (100 requests/minute)
- **Proxy-aware**: honors `X-Forwarded-For` and `X-Forwarded-Proto` headers
- **OpenAPI**: spec generated in development

## Tech Stack

| Layer | Tech |
|---|---|
| Backend | ASP.NET Core (.NET 9), C# |
| Auth | JWT Bearer, custom auth and CSRF middleware |
| Real-time | ASP.NET Core WebSockets |
| Database | `DatabaseHandler` singleton (see `main/`) |
| Cache | Redis via `RedisHandler` |
| Docs | OpenAPI (development only) |

## Installation

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/lumerions/discordtest.git
cd discordtest
dotnet restore
```

Run the dev server:

```bash
dotnet run
```

The launch URL is set in `Properties/launchSettings.json`. Example requests are in `discordtest.http`.

## Configuration

Settings live in `appsettings.json` and `appsettings.Development.json`, or in user secrets and environment variables (use `__` as the section separator, e.g. `Main__JWTKEY`).

| Key | Description |
|---|---|
| `Main:JWTKEY` | Secret used to sign and validate JWTs (the app throws on startup if missing) |
| Database connection | Connection string for the database used by `DatabaseHandler` |
| Redis connection | Connection string for the instance used by `RedisHandler` |

Check `appsettings.json` and the files in `main/` for the exact key names for the database and Redis. Keep secrets out of the repo.

## Project Structure

```
discordtest/
├── Program.cs         # app setup, auth, DI, middleware pipeline
├── controllers/       # API controllers
├── middleware/        # CSRF and authentication middleware
├── main/              # database, Redis, and shared helpers
├── schema/            # request/response models
├── workers/           # background workers
├── defaultavatars/    # default avatar images
├── Properties/        # launch settings
├── discordtest.csproj
├── discordtest.http   # sample requests
└── appsettings*.json
```
