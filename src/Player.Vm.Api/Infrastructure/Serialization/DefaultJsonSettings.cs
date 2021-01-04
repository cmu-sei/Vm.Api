// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Player.Vm.Api.Infrastructure.Serialization
{
    public static class DefaultJsonSettings
    {
        public static JsonSerializerOptions Settings
        {
            get
            {
                // must be synced with Startup.cs
                var settings = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                settings.Converters.Add(new JsonStringEnumConverter());

                return settings;
            }
        }
    }

}
