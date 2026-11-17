# Multi-stage build for .NET 9 Web API
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /app

# Copy csproj files first for layer caching during restore
COPY ["src/MoneyPlus.Api/MoneyPlus.Api.csproj", "src/MoneyPlus.Api/"]
COPY ["src/MoneyPlus.Core/MoneyPlus.Core.csproj", "src/MoneyPlus.Core/"]
RUN dotnet restore "src/MoneyPlus.Api/MoneyPlus.Api.csproj"

# Copy full source and publish release build
COPY . .
WORKDIR "/app/src/MoneyPlus.Api"
RUN dotnet publish "MoneyPlus.Api.csproj" -c Release -o /out /p:UseAppHost=false

# Production runtime image
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /out .

# Port configuration (ASP.NET 9 default is 8080)
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

ENTRYPOINT ["dotnet", "MoneyPlus.Api.dll"]

