using System.Numerics;
using Content.Shared._Mono.Radar;
using NFRadarBlipShape = Content.Shared._NF.Radar.RadarBlipShape;
using Content.Shared.Projectiles;
using Content.Shared.Shuttles.Components;
using RadarBlipComponent = Content.Server._NF.Radar.RadarBlipComponent;
using Robust.Shared.Map;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;

namespace Content.Server._Mono.Radar;

public sealed partial class RadarBlipSystem : EntitySystem
{
    [Dependency] private readonly SharedTransformSystem _xform = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;

    // Pooled collections to avoid per-request heap churn
    private readonly List<BlipNetData> _tempBlipsCache = new();
    private readonly List<(NetEntity? Grid, Vector2 Start, Vector2 End, float Thickness, Color Color)> _tempHitscansCache = new();
    private readonly List<EntityUid> _tempSourcesCache = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<RequestBlipsEvent>(OnBlipsRequested);
        SubscribeLocalEvent<RadarBlipComponent, ComponentShutdown>(OnBlipShutdown);
    }

    private void OnBlipsRequested(RequestBlipsEvent ev, EntitySessionEventArgs args)
    {
        if (!TryGetEntity(ev.Radar, out var radarUid))
            return;

        if (!TryComp<RadarConsoleComponent>(radarUid, out var radar))
            return;

        var sourcesEv = new GetRadarSourcesEvent();
        RaiseLocalEvent(radarUid.Value, ref sourcesEv);

        // Reuse pooled sources list
        _tempSourcesCache.Clear();
        if (sourcesEv.Sources != null)
            _tempSourcesCache.AddRange(sourcesEv.Sources);
        else
            _tempSourcesCache.Add(radarUid.Value);

        AssembleBlipsReport((EntityUid)radarUid, _tempSourcesCache, radar);
        AssembleHitscanReport((EntityUid)radarUid, radar);
        // Combine the blips and hitscan lines
        var giveEv = new GiveBlipsEvent(_tempBlipsCache, _tempHitscansCache);
        RaiseNetworkEvent(giveEv, args.SenderSession);

        _tempBlipsCache.Clear();
        _tempHitscansCache.Clear();
        _tempSourcesCache.Clear();
    }

    private void OnBlipShutdown(EntityUid blipUid, RadarBlipComponent component, ComponentShutdown args)
    {
        var netBlipUid = GetNetEntity(blipUid);
        var removalEv = new BlipRemovalEvent(netBlipUid);
        RaiseNetworkEvent(removalEv);
    }

    private void AssembleBlipsReport(EntityUid uid, List<EntityUid> sources, RadarConsoleComponent? component = null)
    {
        _tempBlipsCache.Clear();

        if (Resolve(uid, ref component))
        {
            var radarXform = Transform(uid);
            var radarGrid = radarXform.GridUid;
            var radarMapId = radarXform.MapID;

            var blipQuery = EntityQueryEnumerator<RadarBlipComponent, TransformComponent, PhysicsComponent>();

            while (blipQuery.MoveNext(out var blipUid, out var blip, out var blipXform, out var blipPhysics))
            {
                if (!blip.Enabled)
                    continue;

                // This prevents blips from showing on radars that are on different maps
                if (blipXform.MapID != radarMapId)
                    continue;

                if (!NearAnySources(_xform.GetWorldPosition(blipXform), sources, component.MaxRange))
                    continue;

                var blipGrid = blipXform.GridUid;

                if (blip.RequireNoGrid && blipGrid != null // if we want no grid but we are on a grid
                    || !blip.VisibleFromOtherGrids && blipGrid != radarGrid) // or if we don't want to be visible from other grids but we're on another grid
                    continue; // don't show this blip

                var netBlipUid = GetNetEntity(blipUid);

                var blipVelocity = _physics.GetMapLinearVelocity(blipUid, blipPhysics, blipXform);

                // due to PVS being a thing, things will break if we try to parent to not the map or a grid
                var coord = blipXform.Coordinates;
                if (blipXform.ParentUid != blipXform.MapUid && blipXform.ParentUid != blipGrid)
                    coord = _xform.WithEntityId(coord, blipGrid ?? blipXform.MapUid!.Value);

                var gridCfg = (BlipConfig?)null;
                var rotation = _xform.GetWorldRotation(blipXform);

                var shape = blip.Shape switch
                {
                    NFRadarBlipShape.Circle => RadarBlipShape.Circle,
                    NFRadarBlipShape.Square => RadarBlipShape.Square,
                    NFRadarBlipShape.Triangle => RadarBlipShape.Triangle,
                    NFRadarBlipShape.Star => RadarBlipShape.Star,
                    NFRadarBlipShape.Diamond => RadarBlipShape.Diamond,
                    NFRadarBlipShape.Hexagon => RadarBlipShape.Hexagon,
                    NFRadarBlipShape.Arrow => RadarBlipShape.Arrow,
                    _ => RadarBlipShape.Circle
                };

                var config = new BlipConfig
                {
                    Color = blip.RadarColor,
                    Shape = shape,
                    Bounds = new Box2(-blip.Scale * 1.5f, -blip.Scale * 1.5f, blip.Scale * 1.5f, blip.Scale * 1.5f)
                };

                // we're parented to either the map or a grid and this is relative velocity so account for grid movement
                if (blipGrid != null)
                {
                    blipVelocity -= _physics.GetLinearVelocity(blipGrid.Value, coord.Position);

                    var gridXform = Transform(blipGrid.Value);
                    // it's local-frame velocity so rotate it too
                    blipVelocity = (-gridXform.LocalRotation).RotateVec(blipVelocity);
                }

                // ideally we would handle blips being culled by detection on server but detection grid culling is already clientside so might as well
                _tempBlipsCache.Add(new(netBlipUid,
                              GetNetCoordinates(coord),
                              blipVelocity,
                              rotation,
                              config,
                              gridCfg));
            }
        }
    }

    /// <summary>
    /// Assembles trajectory information for hitscan projectiles to be displayed on radar
    /// </summary>
    private void AssembleHitscanReport(EntityUid uid, RadarConsoleComponent? component = null)
    {
        _tempHitscansCache.Clear();

        if (!Resolve(uid, ref component))
            return;

        var radarPosition = _xform.GetWorldPosition(uid);

        var hitscanQuery = EntityQueryEnumerator<HitscanRadarComponent>();

        while (hitscanQuery.MoveNext(out _, out var hitscan))
        {
            if (!hitscan.Enabled)
                continue;

            // Check if either the start or end point is within radar range
            var startDistance = (hitscan.StartPosition - radarPosition).Length();
            var endDistance = (hitscan.EndPosition - radarPosition).Length();

            if (startDistance > component.MaxRange && endDistance > component.MaxRange)
                continue;

            // If there's an origin grid, use that for coordinate system
            if (hitscan.OriginGrid != null && hitscan.OriginGrid.Value.IsValid())
            {
                var gridUid = hitscan.OriginGrid.Value;

                // Convert world positions to grid-local coordinates
                var gridMatrix = _xform.GetWorldMatrix(gridUid);
                Matrix3x2.Invert(gridMatrix, out var invGridMatrix);

                var localStart = Vector2.Transform(hitscan.StartPosition, invGridMatrix);
                var localEnd = Vector2.Transform(hitscan.EndPosition, invGridMatrix);

                _tempHitscansCache.Add((GetNetEntity(gridUid), localStart, localEnd, hitscan.LineThickness, hitscan.RadarColor));
            }
            else
            {
                // Use world coordinates with null grid
                _tempHitscansCache.Add((null, hitscan.StartPosition, hitscan.EndPosition, hitscan.LineThickness, hitscan.RadarColor));
            }
        }
    }

    private bool NearAnySources(Vector2 coord, List<EntityUid> sources, float range)
    {
        var rsqr = range * range;
        foreach (var source in sources)
        {
            var pos = _xform.GetWorldPosition(source);
            if ((pos - coord).LengthSquared() < rsqr)
                return true;
        }

        return false;
    }
}
