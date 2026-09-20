# =============================================================================
# Multi-stage Dockerfile för Guardly Inspection API
#
# Steg 1 (build):   full .NET SDK, ~800 MB. Bygger och publicerar appen.
# Steg 2 (runtime): bara ASP.NET-runtime, ~220 MB. Får bara det publicerade
#                   resultatet kopierat till sig.
#
# Poängen: den färdiga imagen innehåller varken källkod, NuGet-cache eller
# kompilator. Mindre image betyder snabbare pull i Container Apps (kortare
# kallstart) och mindre angreppsyta.
# =============================================================================

# ---------- Steg 1: build ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Kopiera bara projektfilerna först. Docker cachar lagret, så restore körs bara
# om en .csproj faktiskt har ändrats — inte vid varje kodändring.
COPY ["src/Guardly.Api/Guardly.Api.csproj", "src/Guardly.Api/"]
COPY ["tests/Guardly.Tests/Guardly.Tests.csproj", "tests/Guardly.Tests/"]
RUN dotnet restore "src/Guardly.Api/Guardly.Api.csproj"

# Nu resten av koden.
COPY . .

RUN dotnet publish "src/Guardly.Api/Guardly.Api.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# ---------- Steg 2: runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Kör som icke-root. Imagen har redan en användare 'app' (uid 1654).
USER app

# Container Apps skickar trafik till porten i ingress.targetPort — vi kör på 8080.
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "Guardly.Api.dll"]
