using System.Numerics;
using Content.Shared.Theta.ShipEvent.Components;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.Enums;
using Content.Shared.Theta.ShipEvent.CircularShield;
using Robust.Shared.Physics.Components;
using Robust.Shared.Prototypes;
using Vector3 = Robust.Shared.Maths.Vector3;

namespace Content.Client.Theta.ShipEvent.Systems;

public sealed class CircularShieldOverlay : Overlay
{
    private IEntityManager _entMan = default!;
    private TransformSystem _formSys = default!;
    private SharedCircularShieldSystem _shieldSys = default!;
    private IEyeManager _eyeMan = default!;
    private ShaderInstance _shader;

    // Shield visual settings
    private const float FixedThicknessInPixels = 10.0f;
    private const float EdgeSmoothness = 0.03f;
    private const float Brightness = 0.8f;

    public override OverlaySpace Space => OverlaySpace.WorldSpaceBelowEntities;

    public CircularShieldOverlay()
    {
        _entMan = IoCManager.Resolve<IEntityManager>();
        _formSys = _entMan.System<TransformSystem>();
        _shieldSys = _entMan.System<SharedCircularShieldSystem>();
        _eyeMan = IoCManager.Resolve<IEyeManager>();
        _shader = IoCManager.Resolve<IPrototypeManager>().Index<ShaderPrototype>("ShieldOverlay").InstanceUnique();

        // Initialize shader with default values
        _shader.SetParameter("BRIGHTNESS", Brightness);
        // RING_WIDTH will be calculated dynamically based on radius
        _shader.SetParameter("EDGE_SMOOTHNESS", EdgeSmoothness);
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        var query = _entMan.EntityQuery<TransformComponent, CircularShieldComponent>();
        foreach ((var form, var shield) in query)
        {
            if (!shield.CanWork || form.MapID != args.MapId)
                continue;

            // Get the shield's parent entity (grid)
            var gridUid = form.ParentUid;
            if (!_entMan.EntityExists(gridUid))
                continue;

            // Make sure the grid has a transform component
            if (!_entMan.TryGetComponent(gridUid, out TransformComponent? gridTransform))
                continue;

            // Get the center of mass if the grid has physics
            Vector2 centerOffset = Vector2.Zero;
            if (_entMan.TryGetComponent(gridUid, out PhysicsComponent? physics))
            {
                centerOffset = physics.LocalCenter;
            }

            // Generate cone vertices based on the shield's parameters
            Vector2[] verts = _shieldSys.GenerateConeVertices(
                shield.Radius,
                shield.Angle,
                shield.Width,
                (int) (shield.Width / Math.Tau * 20));

            // Get grid position and transform
            var gridWorldPos = _formSys.GetWorldPosition(gridTransform);
            var gridMatrix = _formSys.GetWorldMatrix(gridTransform);

            // Apply center of mass offset to the world position
            if (centerOffset != Vector2.Zero)
            {
                var worldOffset = gridTransform.WorldRotation.RotateVec(centerOffset);
                gridWorldPos += worldOffset;
            }

            // Transform vertices to world space using grid's matrix, centering them at the center of mass
            for (int i = 0; i < verts.Length; i++)
            {
                // Add center of mass offset to each vertex before transformation
                verts[i] = Vector2.Transform(verts[i] + centerOffset, gridMatrix);
            }

            // Convert grid position to screen space
            Vector2 shieldPos = args.Viewport.WorldToLocal(gridWorldPos);
            shieldPos.Y = args.ViewportBounds.Size.Y - shieldPos.Y;

            // Calculate screen radius based on world radius
            var worldPoint = gridWorldPos;
            var worldPointPlusRadius = worldPoint + new Vector2(shield.Radius, 0);
            var screenPoint = args.Viewport.WorldToLocal(worldPoint);
            var screenPointPlusRadius = args.Viewport.WorldToLocal(worldPointPlusRadius);
            var screenRadius = Vector2.Distance(screenPoint, screenPointPlusRadius);

            // Calculate the ring width as a proportion of radius to maintain fixed visual thickness
            // The shader uses normalized coordinates where 1.0 = full radius,
            // so we need to convert our fixed pixel thickness to a proportion
            float ringWidth = screenRadius > 0 ? FixedThicknessInPixels / screenRadius : 0.1f;

            // Apply shader parameters
            _shader.SetParameter("BASE_COLOR", new Vector3(shield.Color.R, shield.Color.G, shield.Color.B));
            _shader.SetParameter("CENTER", shieldPos);
            _shader.SetParameter("SCREEN_RADIUS", screenRadius);
            _shader.SetParameter("RING_WIDTH", ringWidth);
            args.WorldHandle.UseShader(_shader);

            // Always use TriangleFan for now - it's the most reliable method
            args.WorldHandle.DrawPrimitives(DrawPrimitiveTopology.TriangleFan, verts, Color.White);

            args.WorldHandle.UseShader(null);
        }
    }
}
