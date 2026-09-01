// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NSubstitute;
using Player.Vm.Api.Domain.Models;
using Xunit;

namespace Player.Vm.Api.Tests.Infrastructure;

/// <summary>
/// Tests for the harness itself. Each one guards a property the suite's other tests silently rely on,
/// and which would degrade without failing anything: the wrong provider, a shared database, migrations
/// that never ran, requests reaching a database no test owns.
/// </summary>
public class DatabaseHarnessTests(DatabaseFixture fixture, VmApiFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<VmApiFactory>
{
    /// <summary>
    /// The guarantee the whole step rests on. If this ever reports anything but Npgsql, the suite has
    /// gone back to proving things against a database production does not use.
    /// </summary>
    [Fact]
    public void TheProviderIsPostgreSql()
    {
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", Db.Database.ProviderName);
        Assert.True(Db.Database.IsNpgsql());
    }

    /// <summary>
    /// Migrations ran, rather than EnsureCreated having quietly produced a schema from the model. The
    /// distinction matters: a migration that does not match the model is exactly the bug a real
    /// migration run catches.
    /// </summary>
    [Fact]
    public async Task EveryMigrationIsApplied()
    {
        var applied = await Db.Database.GetAppliedMigrationsAsync(Ct);
        var pending = await Db.Database.GetPendingMigrationsAsync(Ct);

        Assert.NotEmpty(applied);
        Assert.Empty(pending);
    }

    /// <summary>
    /// The <c>if (Database.IsNpgsql())</c> branch of <c>VmContext.OnModelCreating</c> applies
    /// snake_case naming, which no other provider exercises. Read from the catalog rather than from the
    /// model, so this is what was actually created.
    /// </summary>
    [Fact]
    public async Task TablesAndColumnsUseSnakeCase()
    {
        var columns = await Db.Database
            .SqlQuery<string>(
                $"""
                SELECT column_name FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = 'vms'
                """)
            .ToListAsync(Ct);

        Assert.Contains("power_state", columns);
        Assert.Contains("has_pending_tasks", columns);
        Assert.DoesNotContain("PowerState", columns);
    }

    /// <summary>
    /// The usage log is a second database with its own migration history, per test, as production keeps
    /// it - <c>VmUsageLogging:PostgreSql</c> is a connection string of its own and is as often as not a
    /// different server. Asserted from both ends, because nothing else would fail if the two contexts
    /// were pointed at one database: the usage log's tables are there, and they are not in this test's
    /// <c>VmContext</c> database.
    /// </summary>
    [Fact]
    public async Task TheUsageLogIsASeparateMigratedDatabase()
    {
        Assert.NotEqual(Session.DatabaseName, Session.LoggingDatabaseName);

        await using var logging = NewLoggingContext();

        Assert.True(logging.Database.IsNpgsql());
        Assert.NotEmpty(await logging.Database.GetAppliedMigrationsAsync(Ct));
        Assert.Empty(await logging.Database.GetPendingMigrationsAsync(Ct));
        Assert.Empty(await logging.VmUsageLoggingSessions.ToListAsync(Ct));

        var tables = await Db.Database
            .SqlQuery<string>(
                $"""
                SELECT table_name FROM information_schema.tables
                WHERE table_schema = 'public' AND table_name LIKE 'vm_usage%'
                """)
            .ToListAsync(Ct);

        Assert.Empty(tables);
    }

    /// <summary>
    /// <c>AddPostgresUUIDGeneration</c> gives Guid keys a <c>uuid_generate_v4()</c> default, which needs
    /// the <c>uuid-ossp</c> extension and so needs the container's user to stay a superuser.
    /// </summary>
    [Fact]
    public async Task AGuidKeyIsGeneratedByTheStore()
    {
        var vm = new Domain.Models.Vm { Name = "store-generated" };

        await Seed(vm);

        Assert.NotEqual(Guid.Empty, vm.Id);
    }

    /// <summary>
    /// Real constraints are enforced now, which is the point of the change. In-memory accepted this.
    /// </summary>
    [Fact]
    public async Task AForeignKeyToAMissingVmIsRejected()
    {
        Db.VmTeams.Add(new Domain.Models.VmTeam(Guid.NewGuid(), Guid.NewGuid()));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => Db.SaveChangesAsync(Ct));

        Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal("23503", ((PostgresException)ex.InnerException).SqlState);
    }

    /// <summary>
    /// The property that replaced the old "scope your assertions to rows you seeded" rule: this test can
    /// assert on the whole table because no other test's rows are in it.
    /// </summary>
    [Fact]
    public async Task ThisTestSeesOnlyItsOwnRows()
    {
        await Seed(VmApiFactory.VsphereVm(), VmApiFactory.VsphereVm());

        await using var context = NewContext();

        Assert.Equal(2, await context.Vms.CountAsync(Ct));
    }

    /// <summary>
    /// A request writes to the database of the test that made it, and not to the throwaway database the
    /// host migrated at startup. That fallback exists for <c>InitializeDatabase</c> alone, and this is
    /// what keeps it from quietly becoming the database requests use.
    /// </summary>
    [Fact]
    public async Task ARequestWritesToThisTestsDatabase()
    {
        Assert.NotEqual(Factory.HostDatabaseName, Session.DatabaseName);

        // Without this the Vm is refused at the authorization gate, and the request is still answered
        // with 202 and a per-Vm error - so the write never happens for a reason that has nothing to do
        // with which database was reached.
        Factory.AllowEverything();

        var vm = VmApiFactory.VsphereVm();
        await Seed(vm);

        // An unstubbed BulkPowerOperation hands the handler a null dictionary, which becomes a 500 after
        // the write. Stubbed so a failure here means the database, not the hypervisor seam.
        Factory.Vsphere.BulkPowerOperation(Arg.Any<Guid[]>(), PowerOperation.PowerOn)
            .Returns(new Dictionary<Guid, string> { [vm.Id] = string.Empty });

        var response = await Client.PostAsJsonAsync(
            "/api/vms/actions/power-on", new { Ids = new[] { vm.Id } }, Ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // The handler sets this before calling the hypervisor. Seeing it means the request's context
        // reached this test's database, since that is the only one holding the row at all.
        await using var context = NewContext();
        var stored = await context.Vms.AsNoTracking().SingleAsync(x => x.Id == vm.Id, Ct);

        Assert.True(stored.HasPendingTasks);
    }

    /// <summary>
    /// A request with no session header must fail loudly and name the header, rather than fall through
    /// to the host's database. The message is the whole value of routing by header rather than by an
    /// ambient value.
    /// </summary>
    [Fact]
    public async Task ARequestWithNoSessionHeaderIsRefused()
    {
        using var unrouted = Factory.CreateAuthenticatedClient();

        // ExceptionMiddleware turns the InvalidOperationException into a 500; what matters is that the
        // request did not succeed against some other database.
        var response = await unrouted.PostAsJsonAsync(
            "/api/vms/actions/power-on", new { Ids = new[] { Guid.NewGuid() } }, Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }
}
