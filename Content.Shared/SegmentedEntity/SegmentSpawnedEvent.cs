namespace Content.Shared.SegmentedEntity;

public sealed class SegmentSpawnedEvent : EntityEventArgs
{
    public EntityUid BlueRainLizard = default!;

    public SegmentSpawnedEvent(EntityUid blueRainLizard)
    {
        BlueRainLizard = blueRainLizard;
    }
}
