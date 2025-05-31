using System.Numerics;
using Content.Server.Chat.Managers;
using Content.Shared.Chat;
using Content.Shared.Localizations;
using Robust.Server.Player;

namespace Content.Server._Mono.Shipyard;

/// <summary>
/// A system that tells players which direction their newly purchased ship is located
/// </summary>
public sealed class ShipyardDirectionSystem : EntitySystem
{
    [Dependency] private readonly IChatManager _chatManager = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;

    /// <summary>
    /// Sends a message to the player indicating the compass direction of their newly purchased ship
    /// </summary>
    public void SendShipDirectionMessage(EntityUid player, EntityUid ship)
    {
        if (!TryComp<TransformComponent>(player, out var playerTransform) ||
            !TryComp<TransformComponent>(ship, out var shipTransform))
            return;

        // Make sure both entities are on the same map
        if (playerTransform.MapID != shipTransform.MapID)
            return;

        // Get positions of both entities
        var playerPos = Transform(player).WorldPosition;
        var shipPos = Transform(ship).WorldPosition;

        // Calculate direction vector
        var direction = shipPos - playerPos;

        // Skip if they're at the same position (very unlikely but just in case)
        if (direction.LengthSquared() < 0.01f)
            return;

        // Get compass direction
        var directionName = ContentLocalizationManager.FormatDirection(direction.GetDir()).ToLower(); //lua localization
        var distance = Math.Round(direction.Length(), 1);

        // Send message to player
        var message = Loc.GetString("shipyard-direction-message",
            ("direction", directionName),
            ("distance", distance));

        if (_playerManager.TryGetSessionByEntity(player, out var session))
        {
            _chatManager.ChatMessageToOne(ChatChannel.Server, message, message, EntityUid.Invalid, false, session.Channel);
        }
    }

    //lua start
    ///// <summary>
    ///// Converts a direction vector to a compass direction
    ///// </summary>
    //private string GetCompassDirection(Vector2 direction)
    //{
    //    var angle = new Angle(direction);
    //    var dir = angle.GetDir();

    //    return dir switch
    //    {
    //        Direction.North => "North",
    //        Direction.NorthEast => "North East",
    //        Direction.East => "East",
    //        Direction.SouthEast => "South East",
    //        Direction.South => "South",
    //        Direction.SouthWest => "South West",
    //        Direction.West => "West",
    //        Direction.NorthWest => "North West",
    //        _ => "Unknown"
    //    };
    //}
    //lua end
}
