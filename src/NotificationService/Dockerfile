FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json ./
COPY nuget.config ./
COPY src/NotificationService/NotificationService.csproj ./src/NotificationService/
COPY src/Contracts/Contracts.csproj ./src/Contracts/
COPY src/Messaging/Messaging.csproj ./src/Messaging/
COPY src/ServiceDefaults/ServiceDefaults.csproj ./src/ServiceDefaults/

RUN dotnet restore src/NotificationService/NotificationService.csproj

COPY src/ src/

RUN dotnet publish src/NotificationService/NotificationService.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "NotificationService.dll"]