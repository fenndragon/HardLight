using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared.Theta.ShipEvent.Components;


[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class CircularShieldComponent : Component
{
    [AutoNetworkedField, ViewVariables(VVAccess.ReadWrite)]
    public EntityUid? BoundConsole;

    [AutoNetworkedField, ViewVariables(VVAccess.ReadWrite)]
    public bool Enabled;

    [AutoNetworkedField, ViewVariables(VVAccess.ReadWrite)]
    public bool Powered;

    [DataField]
    public string ShieldFixtureId = "ShieldFixture"; // Mono

    [DataField("consumptionPerM2")]
    public float ConsumptionPerSquareMeter;

    //specified in degrees, for prototypes
    [DataField] // Mono
    public int MaxWidth = 360;

    [DataField] // Mono
    public int MaxRadius;

    [AutoNetworkedField, DataField] // Mono
    public Color Color;

    //(datafields are for map serialization, so it's possible for mappers to create shield presets)
    [AutoNetworkedField, DataField] // Mono
    public Angle Angle;

    [AutoNetworkedField, DataField] // Mono
    public Angle Width;

    [AutoNetworkedField, DataField] // Mono
    public int Radius;

    // Power surge mechanics when taking damage
    [DataField] // Mono
    public float ProjectileWattPerImpact = 25f; // Watts per point of projectile damage

    [DataField] // Mono
    public float DamageSurgeDuration = 25f; // Duration in seconds before surge dissipates

    [DataField] // Mono
    public float CurrentSurgePower; // Additional watts of power usage from damage

    [DataField] // Mono
    public float SurgeTimeRemaining; // Time remaining for the current power surge

    [DataField] // Mono
    public float PowerDrawLimit = 1500000f; // Amount of wattage you can draw before the shield system turns off - lol this dont work

    [DataField] // Mono
    public float ResetPower = 200000f; // Power usage has to drop below this to reenable shield - lol this dont work

    [DataField(serverOnly: true)] // Mono
    public List<CircularShieldEffect> Effects = new();

    [ViewVariables(VVAccess.ReadOnly)] // Mono
    public bool CanWork => Enabled && Powered;

    // Calculate power draw including any damage surge
    public int DesiredDraw
    {
        get
        {
            if (!Enabled)
                return 0;

            // Base power draw from shield size
            var baseDraw = Radius * Radius * Width.Degrees * 0.5f * ConsumptionPerSquareMeter;

            // Add surge power
            return (int)(baseDraw + CurrentSurgePower);
        }
    }
}

[ImplicitDataDefinitionForInheritors]
public abstract partial class CircularShieldEffect
{
    public virtual void OnShieldInit(Entity<CircularShieldComponent> shield) { }
    public virtual void OnShieldShutdown(Entity<CircularShieldComponent> shield) { }
    public virtual void OnShieldUpdate(Entity<CircularShieldComponent> shield, float time) { }
    public virtual void OnShieldEnter(EntityUid uid, Entity<CircularShieldComponent> shield) { }
}

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class CircularShieldConsoleComponent : Component
{
    [AutoNetworkedField, ViewVariables(VVAccess.ReadWrite)]
    public EntityUid? BoundShield;
}

[Serializable, NetSerializable]
public sealed class CircularShieldToggleMessage : BoundUserInterfaceMessage; // Mono

[Serializable, NetSerializable]
public sealed class CircularShieldChangeParametersMessage(Angle? angle, Angle? width, int? radius) // Mono
    : BoundUserInterfaceMessage
{
    public Angle? Angle = angle;

    public Angle? Width = width;

    public int? Radius = radius;
}
