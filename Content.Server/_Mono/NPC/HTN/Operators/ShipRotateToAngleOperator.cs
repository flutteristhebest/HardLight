using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server.NPC.HTN.PrimitiveTasks;
using Robust.Shared.Map;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace Content.Server._Mono.NPC.HTN.Operators;

/// <summary>
/// Rotates parent shuttle to face a specific angle using ShipSteeringSystem.
/// </summary>
public sealed partial class ShipRotateToAngleOperator : HTNOperator, IHtnConditionalShutdown
{
    [Dependency] private readonly IEntityManager _entManager = default!;
    private ShipSteeringSystem _steering = default!;

    [DataField]
    public HTNPlanState ShutdownState { get; private set; } = HTNPlanState.PlanFinished;

    [DataField]
    public bool RemoveKeyOnFinish = false;

    /// <summary>
    /// The key containing the target angle (Angle object)
    /// </summary>
    [DataField]
    public string AngleKey = "RotateTarget";

    /// <summary>
    /// How far from the ship to create a fake target point for rotation
    /// </summary>
    [DataField]
    public float TargetDistance = 1000f;

    public override void Initialize(IEntitySystemManager sysManager)
    {
        base.Initialize(sysManager);
        _steering = sysManager.GetEntitySystem<ShipSteeringSystem>();
    }

    public override void Startup(NPCBlackboard blackboard)
    {
        base.Startup(blackboard);
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!blackboard.TryGetValue<Angle>(AngleKey, out var targetAngle, _entManager))
            return;

        var xform = _entManager.GetComponent<TransformComponent>(owner);
        var gridUid = xform.GridUid;

        if (!_entManager.HasComponent<ShuttleComponent>(gridUid))
            return;

        // Create a fake coordinate in the direction of the target angle
        var direction = targetAngle.ToWorldVec();
        var targetPos = xform.Coordinates.Offset(direction * TargetDistance);

        _steering.Register(owner, new ShipSteerRequest
        {
            Coordinates = targetPos,
            AlwaysFaceTarget = true,
            InRangeMaxSpeed = 0f, // Don't move, just rotate
            Range = TargetDistance,
            RangeTolerance = TargetDistance - 10f,
        });
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        base.Update(blackboard, frameTime);
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        var status = _steering.GetStatus(owner);

        return status switch
        {
            ShipSteerStatus.Continuing => HTNOperatorStatus.Continuing,
            ShipSteerStatus.NoGrid => HTNOperatorStatus.Failed,
            _ => HTNOperatorStatus.Finished
        };
    }

    public override void Shutdown(NPCBlackboard blackboard, HTNOperatorStatus status)
    {
        base.Shutdown(blackboard, status);
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        _steering.Unregister(owner);

        if (RemoveKeyOnFinish)
            blackboard.Remove<Angle>(AngleKey);
    }

    public HTNPlanState ConditionalShutdown(NPCBlackboard blackboard)
    {
        var status = Update(blackboard, 0f);

        if (status == HTNOperatorStatus.Continuing)
            return HTNPlanState.Continue;

        return ShutdownState;
    }
}
