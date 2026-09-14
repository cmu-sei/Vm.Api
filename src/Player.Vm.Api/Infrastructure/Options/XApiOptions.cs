// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Player.Vm.Api.Infrastructure.Options;

public class XApiOptions
{
    public bool Enabled { get; set; }
    public string Endpoint { get; set; }
    public string Username { get; set; }
    public string Password { get; set; }
    public string IssuerUrl { get; set; }
    public string Platform { get; set; }
    public string ApiUrl { get; set; }
    public string PlayerApiUrl { get; set; }
    public int RetentionDays { get; set; } = 7;
    public int ProcessingDelaySeconds { get; set; } = 5;
}
