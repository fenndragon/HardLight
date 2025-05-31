using System.Numerics;
using Content.Shared.Theta.ShipEvent.Components;

namespace Content.Shared.Theta.ShipEvent.CircularShield;

public class SharedCircularShieldSystem : EntitySystem
{
    [Dependency] private readonly SharedTransformSystem _transformSystem = default!;

    /// <summary>
    /// Generates vertices for a circular shield with origin at (0,0)
    /// </summary>
    public Vector2[] GenerateConeVertices(int radius, Angle angle, Angle width, int extraArcPoints = 0)
    {
        // Check if this is a full or almost full circle
        var isFullCircle = width.Degrees >= 359.0f;

        Vector2[] vertices;

        if (isFullCircle)
        {
            // For full circles, we'll still use a triangle fan but with a center point
            // and carefully placed vertices to avoid the visual artifacts
            var totalPoints = Math.Max(30, extraArcPoints);
            vertices = new Vector2[totalPoints + 2]; // +2 for center and closing the loop

            // First vertex is center point
            vertices[0] = Vector2.Zero;

            Angle step = Angle.FromDegrees(360.0f / totalPoints);

            for (var i = 0; i < totalPoints; i++)
            {
                Angle currentAngle = step * i;
                vertices[i + 1] = currentAngle.ToVec() * radius;
            }

            // Close the loop by duplicating the first edge vertex, but with a tiny offset
            // to prevent perfect overlap and shader artifacts
            vertices[totalPoints + 1] = vertices[1] * 0.999f;
        }
        else
        {
            // Original partial cone/arc implementation
            //central point + two edge points + extra arc points + central point again since this is used for drawing and input must be looped
            vertices = new Vector2[4 + extraArcPoints];
            vertices[0] = new Vector2(0, 0);

            var start = angle - width / 2;
            Angle step = width / (2 + extraArcPoints);

            for (var i = 1; i < 3 + extraArcPoints; i++)
                vertices[i] = (start + step * (i - 1)).ToVec() * radius;

            vertices[^1] = vertices[0];
        }

        return vertices;
    }

    /// <summary>
    /// Generates vertices for a cone shape with origin at the specified offset
    /// </summary>
    public Vector2[] GenerateConeVerticesWithOffset(int radius, Angle angle, Angle width, Vector2 centerOffset, int extraArcPoints = 0)
    {
        var vertices = GenerateConeVertices(radius, angle, width, extraArcPoints);

        // Apply offset to all vertices
        for (var i = 0; i < vertices.Length; i++)
            vertices[i] += centerOffset;

        return vertices;
    }

    public bool EntityInShield(Entity<CircularShieldComponent> shield, EntityUid otherUid, SharedTransformSystem? transformSystem = null)
    {
        // Get the shield's parent entity (grid)
        if (!TryComp(shield, out TransformComponent? transform))
            return false;

        var gridUid = transform.ParentUid;

        // If no valid grid, fall back to the shield's position
        if (!TerminatingOrDeleted(gridUid))
        {
            var fallbackDelta = _transformSystem.GetWorldPosition(otherUid) - _transformSystem.GetWorldPosition(shield);
            var fallbackAngle = ThetaHelpers.AngNormal(new Angle(fallbackDelta) - _transformSystem.GetWorldRotation(shield));
            var fallbackStart = ThetaHelpers.AngNormal(shield.Comp.Angle - shield.Comp.Width / 2);
            return ThetaHelpers.AngInSector(fallbackAngle, fallbackStart, shield.Comp.Width) &&
                fallbackDelta.Length() < shield.Comp.Radius + 0.1; //+0.1 to avoid being screwed over by rounding errors
        }

        // Use grid position and rotation for calculations
        if (!TryComp(gridUid, out TransformComponent? gridTransform))
            return false;

        var gridPos = _transformSystem.GetWorldPosition(gridTransform);
        var gridRot = _transformSystem.GetWorldRotation(gridTransform);

        var otherPos = _transformSystem.GetWorldPosition(otherUid);

        var delta = otherPos - gridPos;
        var relativeAngle = ThetaHelpers.AngNormal(new Angle(delta) - gridRot);

        // Calculate shield start angle, accounting for center offset if needed
        var shieldStart = ThetaHelpers.AngNormal(shield.Comp.Angle - shield.Comp.Width / 2);

        return ThetaHelpers.AngInSector(relativeAngle, shieldStart, shield.Comp.Width) &&
               delta.Length() < shield.Comp.Radius + 0.1;
    }
    public void DoShutdownEffects(Entity<CircularShieldComponent> shield)
    {
        foreach (var effect in shield.Comp.Effects)
            effect.OnShieldShutdown(shield);

        Dirty(shield);
    }

    public void DoEnterEffects(Entity<CircularShieldComponent> shield, EntityUid otherEntity)
    {
        foreach (var effect in shield.Comp.Effects)
            effect.OnShieldEnter(otherEntity, shield);

        Dirty(shield);
    }

    public void DoInitEffects(Entity<CircularShieldComponent> shield)
    {
        foreach (var effect in shield.Comp.Effects)
            effect.OnShieldInit(shield);

        Dirty(shield);
    }

    public void DoShieldUpdateEffects(Entity<CircularShieldComponent> shield, float time)
    {
        foreach (var effect in shield.Comp.Effects)
            effect.OnShieldUpdate(shield, time);

        Dirty(shield);
    }
}
