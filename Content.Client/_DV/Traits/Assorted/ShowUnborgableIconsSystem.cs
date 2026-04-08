using Content.Client.Overlays;
using Content.Shared._DV.Traits.Assorted;
using Content.Shared.Inventory.Events;
using Content.Shared.Overlays;
using Content.Shared.Silicons.Borgs.Components;
using Content.Shared.StatusIcon;
using Content.Shared.StatusIcon.Components;
using Robust.Client.Player;
using Robust.Shared.Prototypes;

namespace Content.Client._DV.Traits.Assorted;

/// <summary>
/// Shows the unborgable marker to borgs and viewers with medical HUD access.
/// </summary>
public sealed class ShowUnborgableIconsSystem : EquipmentHudSystem<ShowHealthIconsComponent>
{
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IPrototypeManager _prototype = default!;

    private static readonly ProtoId<HealthIconPrototype> UnborgableIcon = "HealthIconUnborgable";

    private bool _canSeeUnborgableIcons;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<UnborgableComponent, GetStatusIconsEvent>(OnGetStatusIcons);
        SubscribeLocalEvent<BorgChassisComponent, ComponentStartup>(OnBorgViewerChanged);
        SubscribeLocalEvent<BorgChassisComponent, ComponentRemove>(OnBorgViewerChanged);
    }

    protected override void UpdateInternal(RefreshEquipmentHudEvent<ShowHealthIconsComponent> args)
    {
        base.UpdateInternal(args);
        _canSeeUnborgableIcons = true;
    }

    protected override void DeactivateInternal()
    {
        base.DeactivateInternal();
        _canSeeUnborgableIcons = IsLocalViewerBorg();
    }

    private void OnGetStatusIcons(Entity<UnborgableComponent> ent, ref GetStatusIconsEvent args)
    {
        if (!_canSeeUnborgableIcons && !IsLocalViewerBorg())
            return;

        if (_prototype.TryIndex(UnborgableIcon, out var icon))
            args.StatusIcons.Add(icon);
    }

    private void OnBorgViewerChanged(Entity<BorgChassisComponent> ent, ref ComponentStartup args)
    {
        if (_player.LocalSession?.AttachedEntity == ent.Owner)
            RefreshOverlay();
    }

    private void OnBorgViewerChanged(Entity<BorgChassisComponent> ent, ref ComponentRemove args)
    {
        if (_player.LocalSession?.AttachedEntity == ent.Owner)
            RefreshOverlay();
    }

    private bool IsLocalViewerBorg()
    {
        return _player.LocalSession?.AttachedEntity is { } viewer && HasComp<BorgChassisComponent>(viewer);
    }
}