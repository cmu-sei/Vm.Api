# Adapted from https://github.com/dotnet/dotnet-docker/blob/main/samples/aspnetapp/Dockerfile.chiseled

# Build stage
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /source

# Copy project files and restore as distinct layers
COPY --link src/Player.Vm.Api/*.csproj ./Player.Vm.Api/
WORKDIR /source/Player.Vm.Api
RUN dotnet restore -a $TARGETARCH

# Copy source code and publish app
WORKDIR /source
COPY --link src/Player.Vm.Api/ ./Player.Vm.Api/
WORKDIR /source/Player.Vm.Api
RUN dotnet publish -a $TARGETARCH --no-restore -o /app

# Debug Stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS debug
ENV DOTNET_HOSTBUILDER__RELOADCONFIGCHANGE=false
EXPOSE 8080
WORKDIR /app
COPY --link --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "Player.Vm.Api.dll"]

# Production stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS prod
ARG commit
ENV COMMIT=$commit
ENV DOTNET_HOSTBUILDER__RELOADCONFIGCHANGE=false
EXPOSE 8080
WORKDIR /app
COPY --link --from=build /app .
ENTRYPOINT ["dotnet", "Player.Vm.Api.dll"]