// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Player.Vm.Api.Data.Migrations.Postgres
{
    /// <summary>
    /// Data fix only, no schema change. Vm.Type was set from ProxmoxVmInfo on create but not
    /// on update, so Proxmox info attached to an existing Vm left Type as Unknown. Type is now
    /// the authoritative provider discriminator, so bring existing rows in line.
    /// 0 = Unknown, 2 = Proxmox (Domain.Models.VmType).
    /// </summary>
    public partial class BackfillProxmoxVmType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE vms
                SET type = 2
                WHERE type = 0
                  AND id IN (SELECT vm_id FROM proxmox_vm_info);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversible: rows corrected here are indistinguishable from rows that
            // were already correctly Proxmox before this migration ran.
        }
    }
}
