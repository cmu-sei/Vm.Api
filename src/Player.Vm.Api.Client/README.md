# How to create the nuget package for Player.Vm.Api.Client

1. cd ../Player.Vm.Api
2. dotnet tool restore
3. dotnet build
4. dotnet swagger tofile --output ../Player.Vm.Api.Client/swagger.json bin/Debug/net10.0/Player.Vm.Api.dll v1
5. cd ../Player.Vm.Api.Client
6. npm install
7. ./node_modules/.bin/nswag run /runtime:Net100
8. dotnet pack -c Release /p:version=<version>

Step 2 restores the Swashbuckle.AspNetCore.Cli tool (the `dotnet swagger` command) pinned in
Player.Vm.Api/.config/dotnet-tools.json. Its version must match the Swashbuckle.AspNetCore version in
Directory.Packages.props at the repository root (10.1.0).

Step 6 installs nswag from package.json; it is only needed the first time.

The "Build Client Package" workflow (.github/workflows/client.yml) runs the same steps in Release
configuration, publishes the package to NuGet as unlisted and creates a GitHub release. Start it by
hand from the Actions tab with the client version.
