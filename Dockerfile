# Multi-stage build for Platform.Api. The SDK image compiles; only the much smaller ASP.NET runtime
# image ships, so no compiler, source, or NuGet cache ends up in production.
#   docker build -t platform-api .
#   docker run --rm -p 8080:8080 -e ConnectionStrings__Platform="..." platform-api

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, from project files only: this layer is reused until a dependency changes, so a
# source-only change skips the slow restore.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Kernel/Kernel.Contracts/Kernel.Contracts.csproj src/Kernel/Kernel.Contracts/
COPY src/Kernel/Kernel/Kernel.csproj src/Kernel/Kernel/
COPY src/Packs/Ticketing/Packs.Ticketing.csproj src/Packs/Ticketing/
COPY src/Host/Platform.Api/Platform.Api.csproj src/Host/Platform.Api/
RUN dotnet restore src/Host/Platform.Api/Platform.Api.csproj

COPY .editorconfig ./
COPY src/ src/
RUN dotnet publish src/Host/Platform.Api/Platform.Api.csproj -c Release -o /app --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# The base image defines a non-root "app" user (APP_UID). Running as root would let a compromised
# process modify the image's files.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Platform.Api.dll"]
