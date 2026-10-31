# Builds the AI Farm server. Build context: src/Server (see compose.yaml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY AIFarm.Contracts/AIFarm.Contracts.csproj AIFarm.Contracts/
COPY AIFarm.Server/AIFarm.Server.csproj AIFarm.Server/
RUN dotnet restore AIFarm.Server/AIFarm.Server.csproj
COPY AIFarm.Contracts/ AIFarm.Contracts/
COPY AIFarm.Server/ AIFarm.Server/
RUN dotnet publish AIFarm.Server/AIFarm.Server.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# Npgsql probes for Kerberos (GSSAPI) when connecting and logs an error if the library is missing.
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "AIFarm.Server.dll"]
