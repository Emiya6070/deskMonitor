FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY DeskMonitor.Core/ DeskMonitor.Core/
COPY DeskMonitor.NotesService/ DeskMonitor.NotesService/
RUN dotnet publish DeskMonitor.NotesService -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
RUN mkdir /data && chown "$APP_UID" /data
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
ENV NOTES_DATA_FILE=/data/notes.json
EXPOSE 8080
ENTRYPOINT ["dotnet", "DeskMonitor.NotesService.dll"]
