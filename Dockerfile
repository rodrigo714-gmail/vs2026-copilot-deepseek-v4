FROM mcr.microsoft.com/dotnet/nightly/sdk:10.0-preview AS build
WORKDIR /src
COPY *.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish ai-proxy-hub.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/nightly/aspnet:10.0-preview
WORKDIR /app
COPY --from=build /app .

# The usage rollup is written at runtime. Create its directory and hand it to the app user
# *before* dropping privileges — otherwise the non-root process cannot write there and usage
# silently degrades to memory-only, which makes a monthly free-tier budget meaningless.
# Mount a volume at /app/data to keep the history across container recreations.
RUN mkdir -p /app/data && chown -R $APP_UID:$APP_UID /app/data
ENV PROXY_DATA_DIR=/app/data
VOLUME /app/data

USER $APP_UID
EXPOSE 11434
ENTRYPOINT ["dotnet", "ai-proxy-hub.dll"]
