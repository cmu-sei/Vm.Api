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

    // Home page for the team accounts on context.team. This is the Player UI, not the VM UI, and it
    // has to be the same value Player API sends for its own XApiOptions.UiUrl, because the account
    // identifies the team in the LRS: a different home page makes one team look like two accounts
    // and splits team level reporting. Named for the application it points at, like PlayerApiUrl,
    // because the UI this application serves is the VM UI.
    public string PlayerUiUrl { get; set; }
    public int RetentionDays { get; set; } = 7;
    public int ProcessingDelaySeconds { get; set; } = 5;
}
