FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY global.json ./
COPY src/HackerNewsBestStories/HackerNewsBestStories.csproj src/HackerNewsBestStories/
RUN dotnet restore src/HackerNewsBestStories/HackerNewsBestStories.csproj

COPY src/ src/
RUN dotnet publish src/HackerNewsBestStories/HackerNewsBestStories.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "HackerNewsBestStories.dll"]
