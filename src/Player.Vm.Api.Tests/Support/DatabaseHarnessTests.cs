// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// vm.api: the isolation probe is view_networks' unique (view_id, provider_type, provider_instance_id,
// network_id) index, keyed on network_id under a fixed view (TestData.IsolationProbe). vm.api seeds no
// rows, so the template's seeded-role test is replaced by the usage-log database's own checks.

using System;
using System.Threading;
using System.Threading.Tasks;
using Crucible.Common.EntityEvents.Events;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Player.Vm.Api.Domain.Models;
using VmEntity = Player.Vm.Api.Domain.Models.Vm;

namespace Player.Vm.Api.Tests.Support;

/// <summary>
/// Tests for the harness itself. Every other test trusts it, and a harness that quietly does nothing reads
/// as a green suite.
/// </summary>
public class DatabaseHarnessTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    /// <summary>
    /// A network id under <see cref="TestData.ProbeViewId"/>: if the two <c>Duplicates_*</c> tests shared a
    /// database, whichever ran second would fail on the unique index.
    /// <see cref="A_second_row_with_the_isolation_key_is_refused"/> proves the index is there.
    /// </summary>
    private const string SharedKey = "Isolation Probe";

    [Fact]
    public async Task Saved_entities_survive_a_new_context()
    {
        var entity = TestData.VsphereVm();
        await Seed(entity);

        await using var context = NewContext();
        Assert.NotNull(await context.Vms.SingleOrDefaultAsync(x => x.Id == entity.Id, Ct));
    }

    [Fact]
    public async Task Duplicates_across_tests_are_isolated_first()
    {
        await Seed(TestData.IsolationProbe(SharedKey));

        Assert.Equal(1, await Db.ViewNetworks.CountAsync(x => x.NetworkId == SharedKey, Ct));
    }

    [Fact]
    public async Task Duplicates_across_tests_are_isolated_second()
    {
        await Seed(TestData.IsolationProbe(SharedKey));

        Assert.Equal(1, await Db.ViewNetworks.CountAsync(x => x.NetworkId == SharedKey, Ct));
    }

    /// <summary>
    /// The probes above are an isolation check only because the database refuses a second row with the
    /// same key; this proves the unique index on the probe columns is in force.
    /// </summary>
    [Fact]
    public async Task A_second_row_with_the_isolation_key_is_refused()
    {
        await Seed(TestData.IsolationProbe(SharedKey));
        await using var context = NewContext();
        context.ViewNetworks.Add(TestData.IsolationProbe(SharedKey));

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [Fact]
    public async Task Explicit_ids_survive_the_round_trip()
    {
        var entity = TestData.VsphereVm();
        var assignedId = entity.Id;

        await Seed(entity);

        await using var context = NewContext();
        Assert.NotNull(await context.Vms.FindAsync([assignedId], Ct));
    }

    /// <summary>
    /// Entity events must publish. This is what ruled out transaction-per-test isolation: the
    /// interceptor defers publishing to TransactionCommitted and discards it on rollback.
    /// </summary>
    [Fact]
    public async Task Saving_publishes_entity_events()
    {
        await Seed(TestData.VsphereVm());

        // INotification, not object: the context casts to INotification before publishing, which binds
        // the generic Publish overload, and a substitute records the two separately.
        await Mediator.Received(1).Publish(
            Arg.Is<INotification>(x => x is EntityCreated<VmEntity>),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Uses_the_real_postgres_provider()
    {
        Assert.True(Db.Database.IsNpgsql());
    }

    [Fact]
    public void Applies_postgres_snake_case_naming()
    {
        var entityType = Db.Model.FindEntityType(typeof(VmEntity));

        Assert.Equal("vms", entityType.GetTableName());
    }

    [Fact]
    public async Task Applies_the_real_migration_history()
    {
        // EnsureCreated leaves no history, so this proves the template was built by migrations.
        Assert.NotEmpty(await Db.Database.GetAppliedMigrationsAsync(Ct));
        Assert.Empty(await Db.Database.GetPendingMigrationsAsync(Ct));
    }

    /// <summary>
    /// Read from the catalog rather than from the model, so this is what the migrations created: the
    /// <c>if (Database.IsNpgsql())</c> branch of <c>VmContext.OnModelCreating</c> applies snake_case
    /// naming, which no other provider exercises.
    /// </summary>
    [Fact]
    public async Task The_created_columns_use_snake_case()
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
    /// it. Asserted from both ends, because nothing else would fail if the two contexts shared a database:
    /// the usage log's tables are in its database, and they are not in this test's <c>VmContext</c> one.
    /// </summary>
    [Fact]
    public async Task The_usage_log_is_a_separate_migrated_database()
    {
        Assert.NotEqual(Session.DatabaseName, Session.LoggingDatabaseName());

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
    public async Task A_guid_key_is_generated_by_the_store()
    {
        var vm = new VmEntity { Name = "store-generated" };

        await Seed(vm);

        Assert.NotEqual(Guid.Empty, vm.Id);
    }

    /// <summary>Real constraints are enforced: a team row for a Vm that does not exist is refused.</summary>
    [Fact]
    public async Task A_foreign_key_to_a_missing_vm_is_refused()
    {
        Db.VmTeams.Add(new VmTeam(Guid.NewGuid(), Guid.NewGuid()));

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => Db.SaveChangesAsync(Ct));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    /// <summary>A test can assert on a whole table, because no other test's rows are in it.</summary>
    [Fact]
    public async Task A_test_sees_only_its_own_rows()
    {
        await Seed(TestData.VsphereVm(), TestData.VsphereVm());

        await using var context = NewContext();

        Assert.Equal(2, await context.Vms.CountAsync(Ct));
    }
}
