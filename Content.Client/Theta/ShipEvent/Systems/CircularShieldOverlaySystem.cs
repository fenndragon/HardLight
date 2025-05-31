using Robust.Client.Graphics;

namespace Content.Client.Theta.ShipEvent.Systems;

public sealed class CircularShieldOverlaySystem : EntitySystem
{
    [Dependency] private readonly IOverlayManager _overlayMan = default!;

    public override void Initialize()
    {
        base.Initialize();
        _overlayMan.AddOverlay(new CircularShieldOverlay());
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _overlayMan.RemoveOverlay<CircularShieldOverlay>();
    }
}
