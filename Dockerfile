FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/SocialNetwork/SocialNetwork.csproj src/SocialNetwork/
RUN dotnet restore src/SocialNetwork/SocialNetwork.csproj
COPY src/SocialNetwork/ src/SocialNetwork/
RUN dotnet publish src/SocialNetwork/SocialNetwork.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out/ ./
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "SocialNetwork.dll"]
