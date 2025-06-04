using Content.Server.StationEvents.Events;

namespace Content.Server._Hardlight.StationEvents.Events;

[RegisterComponent, Access(typeof(MeteorSwarmSystem))]
public sealed partial class ValidMeteorSwarmComponent : Component;
