// Monolith - This file is licensed under AGPLv3
// Copyright (c) 2025 Monolith
// See AGPLv3.txt for details.

using Content.Server.Shuttles.Components;
using Content.Shared.Shuttles.BUIStates;

namespace Content.Server.Shuttles.Systems;

public sealed partial class ShuttleConsoleSystem
{
    /// <summary>
    /// Called when the shuttle console component starts up.
    /// </summary>
    /// <param name="uid">Entity UID of the console</param>
    /// <param name="component">The ShuttleConsoleComponent</param>
    /// <param name="args">Event arguments</param>
    private void OnConsoleStartup(EntityUid uid, ShuttleConsoleComponent component, ComponentStartup args)
    {
        // The implementation seems to be missing, but it's referenced in ShuttleConsoleSystem.cs
        // We'll handle updating the state and ensuring device link components
        DockingInterfaceState? dockState = null;
        UpdateState(uid, ref dockState);

        // Also ensure device link components are added for our port buttons
        EnsureDeviceLinkComponents(uid, component);
    }
}
