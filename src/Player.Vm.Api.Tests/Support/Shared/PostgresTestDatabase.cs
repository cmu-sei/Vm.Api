// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// Shared by every Crucible API test suite (agent-docs/api-testing). Do not edit in a repo.
#nullable disable

using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Crucible.Api.Testing;

/// <summary>
/// What a <see cref="PostgresTestDatabase{TContext}"/> needs to know about one application.
/// </summary>
public sealed class PostgresTestDatabaseOptions<TContext> where TContext : DbContext
{
    /// <summary>
    /// The prefix of every database name: <c>{Name}_template</c> and <c>{Name}_test_N</c>. Lower case,
    /// for example <c>player</c>.
    /// </summary>
    public string Name { get; init; }

    /// <summary>
    /// The test assembly's name, for example <c>Player.Api.Tests</c>. It opens the provider banner
    /// (<see cref="PostgresTestDatabase{TContext}.Banner"/>) that CI greps for.
    /// </summary>
    public string TestAssembly { get; init; }

    /// <summary>
    /// The assembly holding the migrations, when it is not the context's own:
    /// <c>{App}.Api.Migrations.PostgreSQL</c>, as production's <c>DatabaseExtensions</c> computes it.
    /// Null when the migrations live in the context's assembly (caster.api, vm.api).
    /// </summary>
    public string MigrationsAssembly { get; init; }

    /// <summary>
    /// Builds a context wired the way production wires it (the entity event interceptor attached, the
    /// <c>ServiceProvider</c> set) over the provider configuration it is given. The app's
    /// <c>&lt;App&gt;ContextFactory.CreateContext</c>.
    /// </summary>
    public Func<Action<DbContextOptionsBuilder<TContext>>, IServiceProvider, TContext> CreateContext { get; init; }

    /// <summary>
    /// Builds the provider a session's own contexts resolve from, with the substituted mediator tests
    /// assert on. The app's <c>&lt;App&gt;ContextFactory.CreateServices</c>.
    /// </summary>
    public Func<(IServiceProvider Services, IMediator Mediator)> CreateServices { get; init; }
}

/// <summary>
/// Real PostgreSQL in a container, matching production: migrations are applied once to a template
/// database, and each test gets its own database created from that template.
/// </summary>
/// <remarks>
/// <para>
/// <c>CREATE DATABASE ... TEMPLATE</c> is a file-level copy, so it costs milliseconds where re-running
/// the migrations costs seconds. It is real isolation rather than a shared database with a rollback, so
/// <c>SaveChanges</c> behaves as it does in production; see <see cref="ITestDatabaseSession{TContext}"/>.
/// </para>
/// <para>
/// The container starts on the first request for a session, not when the fixture is built. An assembly
/// fixture that throws in <c>InitializeAsync</c> fails every test in the assembly, so starting eagerly
/// would stop a contributor without Docker from running the tests that need no database. The start is
/// cached, faulted included, so a machine without Docker gets one attempt and one clear error.
/// </para>
/// <para>
/// The migrations of every Crucible API emit <c>CREATE EXTENSION "uuid-ossp"</c>, which requires
/// superuser. The default <c>postgres</c> user of the official image is one, so the container must not
/// be reconfigured to a restricted role.
/// </para>
/// </remarks>
public sealed class PostgresTestDatabase<TContext> : IAsyncDisposable where TContext : DbContext
{
    /// <summary>The image every Crucible suite runs against, so they prove things against one server.</summary>
    public const string PostgresImage = "postgres:16-alpine";

    /// <summary>Cloning and dropping connect here, never to the database being cloned or dropped.</summary>
    private const string MaintenanceDatabase = "postgres";

    private readonly PostgresTestDatabaseOptions<TContext> _options;
    private readonly string _templateDatabase;
    private readonly Lazy<Task> _started;
    private PostgreSqlContainer _container;
    private int _databaseCount;

    public PostgresTestDatabase(PostgresTestDatabaseOptions<TContext> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.TestAssembly);
        ArgumentNullException.ThrowIfNull(options.CreateContext);
        ArgumentNullException.ThrowIfNull(options.CreateServices);

        _options = options;
        _templateDatabase = $"{options.Name}_template";
        _started = new Lazy<Task>(StartAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// The line every run prints once the template is migrated, as a console line and as an xUnit
    /// diagnostic message. CI greps for it, so a harness change cannot silently stop exercising the
    /// production database provider.
    /// </summary>
    public static string Banner(string testAssembly) =>
        $"[{testAssembly}] database provider: PostgreSQL ({PostgresImage}, real migrations)";

    /// <summary>Hands out an isolated database for a single test, starting PostgreSQL on first call.</summary>
    public async Task<ITestDatabaseSession<TContext>> BeginSessionAsync()
    {
        await _started.Value;

        var databaseName = $"{_options.Name}_test_{Interlocked.Increment(ref _databaseCount)}";

        await ExecuteMaintenanceAsync($"""CREATE DATABASE "{databaseName}" TEMPLATE "{_templateDatabase}";""");

        var (services, mediator) = _options.CreateServices();

        return new Session(this, databaseName, services, mediator);
    }

    public async ValueTask DisposeAsync()
    {
        // Nothing to tear down if no test ever asked for a database.
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <remarks>
    /// No cancellation token: the one available belongs to whichever test asked first, and cancelling
    /// that test would poison the cached start for every test after it. The container has its own
    /// start timeout.
    /// </remarks>
    private async Task StartAsync()
    {
        try
        {
            // Built here rather than in a field initializer: Build() resolves the Docker endpoint, and a
            // throw from a constructor would defeat the one clear error below.
            _container = new PostgreSqlBuilder(PostgresImage).WithDatabase(_templateDatabase).Build();
            await _container.StartAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not start {PostgresImage} in Docker, which these tests require. There is " +
                "deliberately no in-memory or SQLite fallback: one would report a green run that never " +
                "touched the database production uses. Start Docker and run again; see docs/Testing.md.",
                ex);
        }

        var (services, _) = _options.CreateServices();

        await using (var context = CreateContextFor(_templateDatabase, services))
        {
            await context.Database.MigrateAsync();
        }

        // Load-bearing. CREATE DATABASE ... TEMPLATE fails while any session is connected to the
        // template, and disposing a connection returns it to the pool rather than closing it.
        await using (var pooled = new NpgsqlConnection(ConnectionStringFor(_templateDatabase)))
        {
            NpgsqlConnection.ClearPool(pooled);
        }

        AnnounceProvider(Banner(_options.TestAssembly));
    }

    /// <summary>
    /// Sent as an xUnit diagnostic message as well as written to the console: the VSTest bridge that
    /// <c>dotnet test</c> uses discards the test host's plain stdout, and the diagnostic sink is surfaced
    /// with <c>-- xUnit.DiagnosticMessages=true</c>. Never set <c>diagnosticMessages</c> in
    /// <c>xunit.runner.json</c> instead: the run then hangs after the last test.
    /// </summary>
    private static void AnnounceProvider(string banner)
    {
        Console.WriteLine(banner);
        TestContext.Current?.SendDiagnosticMessage(banner);
    }

    private string ConnectionStringFor(string databaseName) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = databaseName
        }.ConnectionString;

    private TContext CreateContextFor(string databaseName, IServiceProvider services) =>
        _options.CreateContext(
            builder => builder.UseNpgsql(
                ConnectionStringFor(databaseName),
                npgsql =>
                {
                    if (_options.MigrationsAssembly is not null)
                    {
                        npgsql.MigrationsAssembly(_options.MigrationsAssembly);
                    }
                }),
            services);

    private async Task ExecuteMaintenanceAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionStringFor(MaintenanceDatabase));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task DropDatabaseAsync(string databaseName)
    {
        // Only this database's pool. ClearAllPools would churn connections of tests running in parallel.
        await using (var pooled = new NpgsqlConnection(ConnectionStringFor(databaseName)))
        {
            NpgsqlConnection.ClearPool(pooled);
        }

        // FORCE (PostgreSQL 13+) terminates any lingering session rather than failing the drop, which
        // keeps teardown from turning into a flaky failure in an unrelated test.
        await ExecuteMaintenanceAsync($"""DROP DATABASE IF EXISTS "{databaseName}" WITH (FORCE);""");
    }

    private sealed class Session(
        PostgresTestDatabase<TContext> database,
        string databaseName,
        IServiceProvider services,
        IMediator mediator) : ITestDatabaseSession<TContext>
    {
        public IMediator Mediator { get; } = mediator;

        public string DatabaseName { get; } = databaseName;

        public string ConnectionString => database.ConnectionStringFor(DatabaseName);

        public TContext CreateContext() => CreateContext(services);

        public TContext CreateContext(IServiceProvider provider) =>
            database.CreateContextFor(DatabaseName, provider);

        public async ValueTask DisposeAsync() => await database.DropDatabaseAsync(DatabaseName);
    }
}
