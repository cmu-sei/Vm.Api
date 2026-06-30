# How to create the nuget package for Player.Vm.Api.Client

1. cd ../Player.Vm.Api
2. swagger tofile --output ../Player.Vm.Api.Client/swagger.json bin/Debug/net10.0/Player.Vm.Api.dll v1
3. cd ../Player.Vm.Api.Client
4. ./node_modules/.bin/nswag run /runtime:Net100
5. dotnet pack -c Release /p:version=0.1.2

*** NOTE: If dotnet swagger is not recognized, in the Player.Vm.Api folder run the following:
    dotnet new tool-manifest
    dotnet tool install --version 10.1.0 Swashbuckle.AspNetCore.Cli

The version installed must match the version in Player.Vm.Api.csproj file.

Also, if nswag is not found, run npm install from Player.Vm.Api.Client folder
