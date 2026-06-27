FROM mcr.microsoft.com/dotnet/nightly/sdk:10.0-preview AS build
WORKDIR /src
COPY *.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish ai-proxy-hub.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/nightly/aspnet:10.0-preview
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 11434
ENTRYPOINT ["dotnet", "ai-proxy-hub.dll"]
