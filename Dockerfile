#
#multi-stage target: dev
#
FROM mcr.microsoft.com/dotnet/sdk:6.0 AS dev

ENV ASPNETCORE_URLS=http://0.0.0.0:4302 \
    ASPNETCORE_ENVIRONMENT=DEVELOPMENT

COPY . /app
WORKDIR /app

RUN dotnet publish -c Release -o /app/dist

CMD ["dotnet", "run"]

#
#multi-stage target: prod
#
FROM mcr.microsoft.com/dotnet/aspnet:6.0 AS prod
ARG commit
ENV COMMIT=$commit

COPY --from=dev /app/dist /app

WORKDIR /app
ENV ASPNETCORE_URLS=http://*:80
EXPOSE 80

CMD ["dotnet","Player.Vm.Api.dll"]

RUN apt-get update && \
    apt-get install -y jq
