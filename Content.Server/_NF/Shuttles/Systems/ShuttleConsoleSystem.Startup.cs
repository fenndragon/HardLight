// Monolith - This file is licensed under AGPLv3
// Copyright (c) 2025 Monolith
// See AGPLv3.txt for details.

using Content.Server.DeviceLinking.Systems;
using Content.Server.Shuttles.Components;
using Content.Shared.DeviceLinking;

namespace Content.Server.Shuttles.Systems;

public sealed partial class ShuttleConsoleSystem
{
    /// <summary>
    /// Ensures the shuttle console has the necessary components for device linking
    /// </summary>
    private void EnsureDeviceLinkComponents(EntityUid uid, ShuttleConsoleComponent component)
    {
        // Get the DeviceLinkSystem which has proper access to modify DeviceLinkSourceComponent
        var deviceLinkSystem = EntityManager.System<DeviceLinkSystem>();

        DeviceLinkSourceComponent sourceComp;

        // Check if the component exists
        if (!HasComp<DeviceLinkSourceComponent>(uid))
        {
            // If not, add it and register the ports
            sourceComp = AddComp<DeviceLinkSourceComponent>(uid);

            // Now let the DeviceLinkSystem handle setting up the ports
            deviceLinkSystem.EnsureSourcePorts(uid, component.SourcePorts.ToArray());
        }
        else
        {
            // If it exists, make sure all ports are registered
            sourceComp = Comp<DeviceLinkSourceComponent>(uid);
            deviceLinkSystem.EnsureSourcePorts(uid, component.SourcePorts.ToArray());
        }

        // Clear all signal states to prevent unwanted signals when establishing new connections
        foreach (var sourcePort in component.SourcePorts)
        {
            deviceLinkSystem.ClearSignal((uid, sourceComp), sourcePort);
        }
    }
}
