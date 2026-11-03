FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY FCG.Messaging.Contracts/FCG.Messaging.Contracts.csproj FCG.Messaging.Contracts/
COPY Users.Api/Users.Api.csproj Users.Api/
RUN dotnet restore Users.Api/Users.Api.csproj
COPY FCG.Messaging.Contracts/ FCG.Messaging.Contracts/
COPY Users.Api/ Users.Api/
WORKDIR /src/Users.Api
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
RUN mkdir -p /app/data
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Users.Api.dll"]
