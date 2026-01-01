using Content.Server._Mono.FireControl;
using Content.Server.Weapons.Ranged.Systems;
using Content.Shared.Physics;
using Content.Shared.Tag;
using Robust.Shared.Physics;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using System.Linq;
using System.Numerics;

namespace Content.Server._Mono.NPC.HTN;

public sealed partial class ShipTargetingSystem : EntitySystem
{
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedGunSystem _gunSystem = default!;
    [Dependency] private readonly TagSystem _tagSystem = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;

    private EntityQuery<GunComponent> _gunQuery;
    private EntityQuery<PhysicsComponent> _physQuery;

    public override void Initialize()
    {
        base.Initialize();

        _gunQuery = GetEntityQuery<GunComponent>();
        _physQuery = GetEntityQuery<PhysicsComponent>();
    }

    // have to use this because RT's is broken and unusable for navigation
    // another algorithm stolen from myself from orbitfight
    public Angle ShortestAngleDistance(Angle from, Angle to)
    {
        var diff = (to - from) % Math.Tau;
        return diff + Math.Tau * (diff < -Math.PI ? 1 : diff > Math.PI ? -1 : 0);
    }

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<ShipTargetingComponent>();

        while (query.MoveNext(out var uid, out var comp))
        {
            var pilotXform = Transform(uid);

            var shipUid = pilotXform.GridUid;

            var target = comp.Target;
            var targetUid = target.EntityId; // if we have a target try to lead it
            var targetGrid = Transform(targetUid).GridUid;

            if (shipUid == null
                || TerminatingOrDeleted(targetUid)
                || !_physQuery.TryComp(shipUid, out var shipBody))
            {
                RemComp<ShipTargetingComponent>(uid);
                continue;
            }

            var shipXform = Transform(shipUid.Value);

            var mapTarget = _transform.ToMapCoordinates(target);
            var shipPos = _transform.GetMapCoordinates(shipXform);

            // we or target might just be in FTL so don't count us as finished
            if (mapTarget.MapId != shipPos.MapId)
                continue;

            var linVel = shipBody.LinearVelocity;
            var targetVel = targetGrid == null ? Vector2.Zero : _physics.GetMapLinearVelocity(targetGrid.Value);
            var leadBy = 1f - MathF.Pow(1f - comp.LeadingAccuracy, frameTime);
            comp.CurrentLeadingVelocity = Vector2.Lerp(comp.CurrentLeadingVelocity, targetVel, leadBy);
            var relVel = comp.CurrentLeadingVelocity - linVel;

            FireWeapons(shipUid.Value, comp.Cannons, mapTarget, relVel);
        }
    }

    private void FireWeapons(EntityUid shipUid, List<EntityUid> cannons, MapCoordinates destMapPos, Vector2 leadBy)
    {
        foreach (var uid in cannons)
        {
            if (TerminatingOrDeleted(uid))
                continue;

            var gXform = Transform(uid);
            if (!gXform.Anchored || !_gunQuery.TryComp(uid, out var gun))
                continue;

            var targetPos = destMapPos.Position;

            var gunToDestVec = destMapPos.Position - _transform.GetWorldPosition(gXform);
            var gunToDestDir = NormalizedOrZero(gunToDestVec);
            var projVel = gun.ProjectileSpeedModified;
            var normVel = gunToDestDir * Vector2.Dot(leadBy, gunToDestDir);
            var tgVel = leadBy - normVel;
            // going too fast to the side, we can't possibly hit it
            if (tgVel.Length() > projVel)
                continue;

            var normTarget = gunToDestDir * MathF.Sqrt(projVel * projVel - tgVel.LengthSquared());
            // going too fast away, we can't hit it
            if (Vector2.Dot(normTarget, normVel) > 0f && normVel.Length() > normTarget.Length())
                continue;

            var approachVel = (normTarget - normVel).Length();
            var hitTime = gunToDestVec.Length() / approachVel;

            targetPos += leadBy * hitTime;

            var gunXform = Transform(uid);
            if (gunXform.MapID != destMapPos.MapId)
                continue;

            var gunWorldPos = _transform.GetWorldPosition(gunXform);
            var diff = targetPos - gunWorldPos;
            if (diff.LengthSquared() <= 0.01f)
                continue;

            var direction = diff.Normalized();
            if (!CanFireInDirection(uid, gunWorldPos, direction, targetPos, gunXform.MapID))
                continue;

            // Rotate weapon towards target if applicable.
            if (TryComp<FireControlRotateComponent>(uid, out _))
            {
                var goalAngle = Angle.FromWorldVec(diff);
                var parentRotation = _transform.GetWorldRotation(gunXform.ParentUid);
                var localRotation = goalAngle - parentRotation;
                _transform.SetLocalRotation(uid, localRotation, gunXform);
            }

            var targetCoords = new EntityCoordinates(_mapManager.GetMapEntityId(destMapPos.MapId), targetPos);
            _gunSystem.AttemptShoot(uid, uid, gun, targetCoords);
        }
    }

    private bool CanFireInDirection(EntityUid weapon, Vector2 weaponPos, Vector2 direction, Vector2 targetPos, MapId mapId, float maxDistance = 1000f)
    {
        var weaponTransform = Transform(weapon);
        var weaponGridUid = weaponTransform.GridUid;

        var targetDistance = Vector2.Distance(weaponPos, targetPos);
        var distance = Math.Min(targetDistance, maxDistance);

        var ray = new CollisionRay(weaponPos, direction, collisionMask: (int)(CollisionGroup.Opaque | CollisionGroup.Impassable));

        bool CheckOnlyEntitiesOnSameGrid(EntityUid entity, EntityUid sourceWeapon)
        {
            if (entity == sourceWeapon)
                return true;

            if (weaponGridUid == null)
                return false;

            var entityTransform = Transform(entity);
            var entityGridUid = entityTransform.GridUid;
            return entityGridUid != weaponGridUid;
        }

        var raycastResults = _physics.IntersectRayWithPredicate(
            mapId,
            ray,
            weapon,
            CheckOnlyEntitiesOnSameGrid,
            distance,
            returnOnFirstHit: true
        ).ToList();

        return raycastResults.Count == 0;
    }

    public Vector2 NormalizedOrZero(Vector2 vec)
    {
        return vec.LengthSquared() == 0 ? Vector2.Zero : vec.Normalized();
    }

    /// <summary>
    /// Adds the AI to the steering system to move towards a specific target.
    /// Returns null on failure.
    /// </summary>
    public ShipTargetingComponent? Target(Entity<ShipTargetingComponent?> ent, EntityCoordinates coordinates, bool checkGuns = true)
    {
        var xform = Transform(ent);
        var shipUid = xform.GridUid;
        if (!TryComp<MapGridComponent>(shipUid, out var grid))
            return null;

        if (!Resolve(ent, ref ent.Comp, false))
            ent.Comp = AddComp<ShipTargetingComponent>(ent);

        ent.Comp.Target = coordinates;

        if (checkGuns)
        {   //Find all guns on the ship grid with the AIShipWeapon tag
            ent.Comp.Cannons.Clear();
            var guns = new HashSet<Entity<GunComponent>>();
            _lookup.GetGridEntities(shipUid.Value, guns);
            foreach (var gun in guns)
            {
                // Only add guns with the AIShipWeapon tag
                if (_tagSystem.HasTag(gun, "AIShipWeapon"))
                {
                    ent.Comp.Cannons.Add(gun);
                }
            }
        }

        return ent.Comp;
    }

    /// <summary>
    /// Stops the steering behavior for the AI and cleans up.
    /// </summary>
    public void Stop(Entity<ShipTargetingComponent?> ent)
    {
        if (!Resolve(ent, ref ent.Comp, false))
            return;

        RemComp<ShipTargetingComponent>(ent);
    }
}
