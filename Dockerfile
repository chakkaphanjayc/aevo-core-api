FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/Aevo.CoreApi/Aevo.CoreApi.csproj src/Aevo.CoreApi/
RUN dotnet restore src/Aevo.CoreApi/Aevo.CoreApi.csproj

COPY src/Aevo.CoreApi/ src/Aevo.CoreApi/
RUN dotnet publish src/Aevo.CoreApi/Aevo.CoreApi.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app/publish ./
USER $APP_UID
ENTRYPOINT ["dotnet", "Aevo.CoreApi.dll"]
