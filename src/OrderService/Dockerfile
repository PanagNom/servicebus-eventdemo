FROM  mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json ./
COPY nuget.config ./
COPY src/OrderService/OrderService.csproj ./src/OrderService/
COPY src/Contracts/Contracts.csproj ./src/Contracts/
COPY src/Messaging/Messaging.csproj ./src/Messaging/
COPY src/ServiceDefaults/ServiceDefaults.csproj ./src/ServiceDefaults/

RUN dotnet restore src/OrderService/OrderService.csproj

COPY src/ src/

RUN dotnet publish src/OrderService/OrderService.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "OrderService.dll"]