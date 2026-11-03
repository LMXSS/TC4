FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY FCG.Messaging.Contracts/FCG.Messaging.Contracts.csproj FCG.Messaging.Contracts/
COPY Catalog.Api/Catalog.Api.csproj Catalog.Api/
RUN dotnet restore Catalog.Api/Catalog.Api.csproj
COPY FCG.Messaging.Contracts/ FCG.Messaging.Contracts/
COPY Catalog.Api/ Catalog.Api/
WORKDIR /src/Catalog.Api
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
RUN mkdir -p /app/data
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Catalog.Api.dll"]
