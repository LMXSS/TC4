FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY FCG.Messaging.Contracts/FCG.Messaging.Contracts.csproj FCG.Messaging.Contracts/
COPY Payments.Api/Payments.Api.csproj Payments.Api/
RUN dotnet restore Payments.Api/Payments.Api.csproj
COPY FCG.Messaging.Contracts/ FCG.Messaging.Contracts/
COPY Payments.Api/ Payments.Api/
WORKDIR /src/Payments.Api
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
RUN mkdir -p /app/data
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Payments.Api.dll"]
