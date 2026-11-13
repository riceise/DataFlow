FROM mcr.microsoft.com/dotnet/sdk:10.0.100 AS build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
COPY tools/DataFlow.Migrator/ tools/DataFlow.Migrator/
RUN dotnet publish tools/DataFlow.Migrator/DataFlow.Migrator.csproj -c Release -o /app /p:UseAppHost=false
FROM mcr.microsoft.com/dotnet/aspnet:10.0.0
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/*
COPY --from=build /app .
USER app
ENTRYPOINT ["dotnet", "DataFlow.Migrator.dll"]
