# Multi-stage build, following the same shape as the other apps on
# muthu-linux (see /data/apps/lms/backend) — no host port exposed here;
# the container is only reachable by name on the apps-network, with the
# host's Nginx doing the /api/ proxy per-domain.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/Certyfi.Domain/Certyfi.Domain.csproj", "Certyfi.Domain/"]
COPY ["src/Certyfi.Infrastructure/Certyfi.Infrastructure.csproj", "Certyfi.Infrastructure/"]
COPY ["src/Certyfi.Api/Certyfi.Api.csproj", "Certyfi.Api/"]
RUN dotnet restore "Certyfi.Api/Certyfi.Api.csproj"

COPY src/ .
WORKDIR /src/Certyfi.Api
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Uploads directory owned by the image's non-root user so the
# certyfi_uploads volume (compose.yaml) is writable — Docker seeds a new
# named volume with this directory's ownership.
RUN mkdir -p /app/wwwroot/uploads && chown -R $APP_UID /app/wwwroot
USER $APP_UID

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Certyfi.Api.dll"]
