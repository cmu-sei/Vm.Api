// Copyright 2025 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Diagnostics.Metrics;

namespace Player.Vm.Api.Domain.Services
{
    public interface ITelemetryService
    {
    }

    public class TelemetryService : ITelemetryService
    {
        public  readonly Meter VmConsolesMeter = new Meter("cmu_sei_player_vm_consoles", "1.0");
        public  Gauge<int> PlayerViewActiveConsoles;
        public Counter<int> ConsoleAccessCounter;

        public TelemetryService()
        {
            PlayerViewActiveConsoles = VmConsolesMeter.CreateGauge<int>("player_view_active_consoles");
            ConsoleAccessCounter = VmConsolesMeter.CreateCounter<int>("console_access_counter");
        }

    }
}
