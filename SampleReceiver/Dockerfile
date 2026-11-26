FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY SampleReceiver/SampleReceiver.csproj SampleReceiver/
RUN dotnet restore SampleReceiver/SampleReceiver.csproj
COPY SampleReceiver/ SampleReceiver/
RUN dotnet publish SampleReceiver/SampleReceiver.csproj -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "SampleReceiver.dll"]
