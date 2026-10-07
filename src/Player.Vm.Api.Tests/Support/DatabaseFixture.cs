// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// vm.api specifics: VmContext's migrations live in the API assembly (no MigrationsAssembly), and the usage
// log is a second database, VmLoggingContext, with a migration history of its own. Each session is a pair
// of databases, one cloned from each template.

using System;
using System.Threading.Tasks;
using MediatR;
using Player.Vm.Api.Data;

namespace Player.Vm.Api.Tests.Support;

/// <summary>
/// Owns the PostgreSQL databases for the whole test run: starts them on first use and hands out an
/// isolated pair of databases per test.
/// </summary>
/// <remarks>
/// <para>
/// PostgreSQL exercises production's actual database, including the <c>if (Database.IsNpgsql())</c>
/// branch of <c>VmContext.OnModelCreating</c> (snake_case naming and <c>uuid_generate_v4()</c> defaults)
/// and the real migration history. A usable Docker daemon is therefore required by every test that takes
/// a database. The mechanics are the shared <see cref="PostgresTestDatabase{TContext}"/>.
/// </para>
/// <para>
/// The usage log lives in a second database because production puts it there: <c>VmLoggingContext</c> is
/// registered against <c>VmUsageLogging:PostgreSql</c>, a connection string separate from
/// <c>ConnectionStrings:PostgreSQL</c>, often on another server. Pointing both contexts at one database
/// would let a test pass that production could not.
/// </para>
/// </remarks>
public sealed class DatabaseFixture : IAsyncLifetime, ITestDatabaseSessionSource<VmContext>
{
    public const string TestAssembly = "Player.Vm.Api.Tests";

    // VmContext's migrations are in its own assembly, so no MigrationsAssembly: production's
    // DatabaseExtensions does not set one either.
    private readonly PostgresTestDatabase<VmContext> _database = new(new()
    {
        Name = "vm",
        TestAssembly = TestAssembly,
        CreateContext = VmContextFactory.CreateContext,
        CreateServices = VmContextFactory.CreateServices
    });

    // The usage log's migrations are in the API assembly too (Data/Migrations/Postgres/VmLogging).
    private readonly PostgresTestDatabase<VmLoggingContext> _logging = new(new()
    {
        Name = "vm_logging",
        TestAssembly = TestAssembly,
        CreateContext = VmContextFactory.CreateLoggingContext,
        CreateServices = VmContextFactory.CreateServices
    });

    /// <summary>Nothing to do here: the containers start on the first request for a session.</summary>
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// A pair of databases no other test can see: the application's and the usage log's. The returned
    /// session is also an <see cref="ITestDatabaseSession{VmLoggingContext}"/>, which is how a request
    /// resolves the usage log (<see cref="VmAppFactory"/>) and how
    /// <see cref="VmSessionExtensions.CreateLoggingContext"/> reaches it.
    /// </summary>
    public async Task<ITestDatabaseSession<VmContext>> BeginSessionAsync()
    {
        var main = _database.BeginSessionAsync();
        var logging = _logging.BeginSessionAsync();

        // Both containers start on the first call; started together, the first test waits for one start.
        await Task.WhenAll(main, logging);

        return new VmTestDatabaseSession(await main, await logging);
    }

    public async ValueTask DisposeAsync()
    {
        await _database.DisposeAsync();
        await _logging.DisposeAsync();
    }
}

/// <summary>One test's pair of databases. Disposing it drops both.</summary>
internal sealed class VmTestDatabaseSession(
    ITestDatabaseSession<VmContext> main,
    ITestDatabaseSession<VmLoggingContext> logging)
    : ITestDatabaseSession<VmContext>, ITestDatabaseSession<VmLoggingContext>
{
    public IMediator Mediator => main.Mediator;

    public string DatabaseName => main.DatabaseName;

    public string ConnectionString => main.ConnectionString;

    public VmContext CreateContext() => main.CreateContext();

    public VmContext CreateContext(IServiceProvider services) => main.CreateContext(services);

    IMediator ITestDatabaseSession<VmLoggingContext>.Mediator => logging.Mediator;

    string ITestDatabaseSession<VmLoggingContext>.DatabaseName => logging.DatabaseName;

    string ITestDatabaseSession<VmLoggingContext>.ConnectionString => logging.ConnectionString;

    VmLoggingContext ITestDatabaseSession<VmLoggingContext>.CreateContext() => logging.CreateContext();

    VmLoggingContext ITestDatabaseSession<VmLoggingContext>.CreateContext(IServiceProvider services) =>
        logging.CreateContext(services);

    public async ValueTask DisposeAsync()
    {
        await main.DisposeAsync();
        await logging.DisposeAsync();
    }
}

/// <summary>The usage-log half of a session handed out by <see cref="DatabaseFixture"/>.</summary>
internal static class VmSessionExtensions
{
    /// <summary>A context over the session's usage-log database. The caller owns it.</summary>
    public static VmLoggingContext CreateLoggingContext(this ITestDatabaseSession<VmContext> session) =>
        Logging(session).CreateContext();

    /// <summary>The name of the session's usage-log database, for the harness's own tests.</summary>
    public static string LoggingDatabaseName(this ITestDatabaseSession<VmContext> session) =>
        Logging(session).DatabaseName;

    /// <summary>The session's usage-log database as a connection string, for a host's configuration.</summary>
    public static string LoggingConnectionString(this ITestDatabaseSession<VmContext> session) =>
        Logging(session).ConnectionString;

    private static ITestDatabaseSession<VmLoggingContext> Logging(ITestDatabaseSession<VmContext> session) =>
        session as ITestDatabaseSession<VmLoggingContext>
        ?? throw new InvalidOperationException(
            "This session has no usage-log database; take sessions from DatabaseFixture.BeginSessionAsync.");
}
