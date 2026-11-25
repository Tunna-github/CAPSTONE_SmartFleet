FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY SmartFleetBE/SmartFleetBE.csproj ./
RUN dotnet restore
COPY SmartFleetBE/ ./
COPY SmartFleetBE/appsettings.example.json ./appsettings.json
RUN dotnet publish SmartFleetBE.csproj -c Release -o /out --no-restore -p:UseAppHost=false
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out ./
COPY simulation/config/scene-manifest.json /simulation/config/scene-manifest.json
RUN mkdir -p /app/App_Data && chown -R $APP_UID /app
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "SmartFleetBE.dll"]
