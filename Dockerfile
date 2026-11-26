FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY WebhookDelivery/WebhookDelivery.csproj WebhookDelivery/
RUN dotnet restore WebhookDelivery/WebhookDelivery.csproj
COPY WebhookDelivery/ WebhookDelivery/
RUN dotnet publish WebhookDelivery/WebhookDelivery.csproj -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "WebhookDelivery.dll"]
