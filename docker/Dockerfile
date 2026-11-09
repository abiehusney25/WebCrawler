# =============================================================================
# docker/Dockerfile
# One image holding the API (/app/api, with the built React UI in its wwwroot)
# and the Worker (/app/worker). Build context is the repo root (see
# docker-compose.yml); compose picks which one a container runs. Config/ sits
# at /app/Config, which Settings.FindRootFolder finds by walking up.
# =============================================================================
FROM node:20-alpine AS web
WORKDIR /web
# install first, so packages are cached until package-lock.json changes
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
# vite.config writes to ../src/WebCrawler.Api/wwwroot = /src/WebCrawler.Api/wwwroot
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# restore first, so package downloads are cached until a .csproj changes
COPY Directory.Build.props Directory.Packages.props ./
COPY src/WebCrawler.Core/WebCrawler.Core.csproj src/WebCrawler.Core/
COPY src/WebCrawler.Api/WebCrawler.Api.csproj src/WebCrawler.Api/
COPY src/WebCrawler.Worker/WebCrawler.Worker.csproj src/WebCrawler.Worker/
RUN dotnet restore src/WebCrawler.Api/WebCrawler.Api.csproj \
 && dotnet restore src/WebCrawler.Worker/WebCrawler.Worker.csproj

COPY src/ src/
# the web page comes from the node stage above, so the .NET build skips it
RUN dotnet publish src/WebCrawler.Api -c Release -o /out/api --no-restore -p:SkipWebPage=true \
 && dotnet publish src/WebCrawler.Worker -c Release -o /out/worker --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /out ./
COPY --from=web /src/WebCrawler.Api/wwwroot ./api/wwwroot
COPY Config/ ./Config/

WORKDIR /app/api
EXPOSE 5000
ENTRYPOINT ["dotnet", "WebCrawler.Api.dll"]
