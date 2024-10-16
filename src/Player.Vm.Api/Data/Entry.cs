// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Player.Vm.Api.Data;

public class Entry
{
    public object Entity { get; set; }
    public EntityState State { get; set; }
    public IEnumerable<PropertyEntry> Properties { get; set; }
    private Dictionary<string, bool> IsPropertyModified { get; set; } = new();

    public Entry(EntityEntry entry, Entry oldEntry = null)
    {
        Entity = entry.Entity;
        State = entry.State;
        Properties = entry.Properties;

        ProcessOldEntry(oldEntry);

        foreach (var prop in Properties)
        {
            IsPropertyModified[prop.Metadata.Name] = prop.IsModified;
        }
    }

    private void ProcessOldEntry(Entry oldEntry)
    {
        if (oldEntry == null) return;

        if (oldEntry.State != EntityState.Unchanged && oldEntry.State != EntityState.Detached)
        {
            State = oldEntry.State;
        }

        var modifiedProperties = oldEntry.GetModifiedProperties();

        foreach (var property in Properties)
        {
            if (modifiedProperties.Contains(property.Metadata.Name))
            {
                property.IsModified = true;
            }
        }
    }

    public string[] GetModifiedProperties()
    {
        return IsPropertyModified
            .Where(x => x.Value)
            .Select(x => x.Key)
            .ToArray();
    }
}
