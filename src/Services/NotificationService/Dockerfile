FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Build settings and package versions first: restore needs them, and the csproj files have no versions.
COPY global.json nuget.config Directory.Build.props Directory.Packages.props ./
COPY src/Services/NotificationService/NotificationService.csproj ./src/Services/NotificationService/
COPY src/Shared/Contracts/Contracts.csproj ./src/Shared/Contracts/
COPY src/Shared/Messaging/Messaging.csproj ./src/Shared/Messaging/
COPY src/Shared/ServiceDefaults/ServiceDefaults.csproj ./src/Shared/ServiceDefaults/

RUN dotnet restore src/Services/NotificationService/NotificationService.csproj

COPY src/ src/

RUN dotnet publish src/Services/NotificationService/NotificationService.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "NotificationService.dll"]
