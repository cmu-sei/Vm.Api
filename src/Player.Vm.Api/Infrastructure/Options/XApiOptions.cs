// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

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
}
