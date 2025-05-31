using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Manager.Definition;

namespace Content.Shared.Theta;

//todo: stuff here should be ported to RT someday
public static class ThetaHelpers
{
    #region Angles

    /// <summary>
    /// Returns value of angle in 0~2pi range
    /// </summary>
    public static Angle AngNormal(Angle x)
    {
        x = x.Reduced();
        if (x < 0)
            x += 2 * Math.PI;
        return x;
    }

    public static bool AngInSector(Angle x, Angle start, Angle width)
    {
        Angle dist = Angle.ShortestDistance(start, x);
        return Math.Sign(width) == Math.Sign(dist) ? Math.Abs(width) >= Math.Abs(dist) : Math.Abs(width) >= Math.Tau - Math.Abs(dist);
    }

    public static bool AngSectorsOverlap(Angle start0, Angle width0, Angle start1, Angle width1)
    {
        Angle end0 = AngNormal(start0 + width0);
        Angle end1 = AngNormal(start1 + width1);
        return AngInSector(start0, start1, width1) || AngInSector(start1, start0, width0) ||
               AngInSector(end0, start1, width1) || AngInSector(end1, start0, width0);
    }

    /// <summary>
    /// Sectors must have positive widths
    /// </summary>
    public static (Angle, Angle) AngCombinedSector(Angle start0, Angle width0, Angle start1, Angle width1)
    {
        Angle startlow, widthlow, starthigh, widthhigh, disthigh;
        (startlow, widthlow, starthigh, widthhigh) = AngInSector(start1, start0, width0) ? (start0, width0, start1, width1) : (start1, width1, start0, width0);
        disthigh = AngNormal(starthigh + widthhigh) > startlow ? starthigh + widthhigh - startlow : Math.Tau - startlow + AngNormal(starthigh + widthhigh);

        return (startlow, Math.Min(Math.Max(widthlow, disthigh), Math.Tau));
    }

    #endregion

    //todo: this is a copypaste from AddComponentSpecial, all concerns from there apply here too
    public static void AddComponentsFromRegistry(EntityUid uid, ComponentRegistry registry)
    {
        var factory = IoCManager.Resolve<IComponentFactory>();
        var entityManager = IoCManager.Resolve<IEntityManager>();
        var serializationManager = IoCManager.Resolve<ISerializationManager>();

        foreach (var (name, data) in registry)
        {
            var component = (Component) factory.GetComponent(name);
            component.Owner = uid;

            var temp = (object) component;
            serializationManager.CopyTo(data.Component, ref temp);
            entityManager.RemoveComponent(uid, temp!.GetType());
            entityManager.AddComponent(uid, (Component) temp);
        }
    }

    public static void RemoveComponentsFromRegistry(EntityUid uid, ComponentRegistry registry)
    {
        var factory = IoCManager.Resolve<IComponentFactory>();
        var entityManager = IoCManager.Resolve<IEntityManager>();

        foreach (var (name, _) in registry)
        {
            entityManager.RemoveComponent(uid, factory.GetRegistration(name).Type);
        }
    }
}