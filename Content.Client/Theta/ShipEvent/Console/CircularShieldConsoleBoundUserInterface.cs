using Content.Shared.Theta.ShipEvent.Components;
using Content.Shared.Theta.ShipEvent.UI;
using JetBrains.Annotations;
using Robust.Shared.Timing;

namespace Content.Client.Theta.ShipEvent.Console;


[UsedImplicitly]
public sealed class CircularShieldConsoleBoundUserInterface(EntityUid owner, Enum uiKey)
    : BoundUserInterface(owner, uiKey) // Mono - Primary constructor
{
    [Dependency] private readonly IGameTiming _gameTiming = default!;

    private CircularShieldConsoleWindow? _window;

    // Smooth changing the shield parameters causes a spam to server
    private readonly TimeSpan _updateCd = TimeSpan.FromMilliseconds(1);
    private TimeSpan _nextUpdate;

    protected override void Open()
    {
        base.Open();

        _window = new CircularShieldConsoleWindow();
        _window.OpenCentered();
        _window.OnClose += Close;
        _window.OnEnableButtonPressed += () => SendMessage(new CircularShieldToggleMessage());
        _window.OnShieldParametersChanged += UpdateShieldParameters;

        // Set the console entity for the radar display
        _window.SetConsole(Owner);
    }

    private void UpdateShieldParameters(Angle? angle, Angle? width, int? radius)
    {
        if (_nextUpdate > _gameTiming.RealTime)
            return;

        _nextUpdate = _gameTiming.RealTime + _updateCd;

        // We still send width in case other parts of the system use it,
        // but the UI no longer provides a way to change it
        SendMessage(new CircularShieldChangeParametersMessage(angle, width, radius));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        if (state is not ShieldConsoleBoundsUserInterfaceState shieldState)
            return;

        _window?.UpdateState(shieldState);
    }

    // Mono
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
            _window?.Close();
    }

}
