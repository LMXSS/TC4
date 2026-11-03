# Build multi-stage: o runtime final e a imagem oficial do host de Azure Functions v4,
# a mesma usada pelo Function App na nuvem — o que roda no compose e no Kubernetes e
# exatamente o artefato que vai para a Azure.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/FcgNotifications.Functions/FcgNotifications.Functions.csproj src/FcgNotifications.Functions/
RUN dotnet restore src/FcgNotifications.Functions/FcgNotifications.Functions.csproj

COPY src/ src/
RUN dotnet publish src/FcgNotifications.Functions/FcgNotifications.Functions.csproj \
    -c Release -o /home/site/wwwroot --no-restore

FROM mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated8.0 AS final

ENV AzureWebJobsScriptRoot=/home/site/wwwroot \
    AzureFunctionsJobHost__Logging__Console__IsEnabled=true \
    FUNCTIONS_WORKER_RUNTIME=dotnet-isolated

COPY --from=build /home/site/wwwroot /home/site/wwwroot
