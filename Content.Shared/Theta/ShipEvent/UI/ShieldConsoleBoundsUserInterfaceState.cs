using Content.Shared.Shuttles.BUIStates;
using Robust.Shared.Map;
using Robust.Shared.Serialization;

namespace Content.Shared.Theta.ShipEvent.UI;

[Serializable, NetSerializable]
public sealed class ShieldConsoleBoundsUserInterfaceState : BoundUserInterfaceState
{
    public NavInterfaceState NavState;
    public ShieldInterfaceState Shield;

    public ShieldConsoleBoundsUserInterfaceState( NavInterfaceState navState, ShieldInterfaceState shield)
    {
        NavState = navState;
        Shield = shield;
    }
}

[Serializable, NetSerializable]
public enum CircularShieldConsoleUiKey
{
    Key
}

