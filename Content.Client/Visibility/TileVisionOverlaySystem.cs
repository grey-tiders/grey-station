using System.Numerics;
using Content.Shared.CCVar;
using Content.Shared.Doors.Components;
using Content.Shared.Tag;
using Robust.Client.ComponentTrees;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Client.Visibility;

/// <summary>
/// Adds a tile-aligned visibility mask for the local player's view.
/// </summary>
public sealed partial class TileVisionOverlaySystem : EntitySystem
{
    [Dependency] private IOverlayManager _overlayManager = default!;
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private LightTreeSystem _lights = default!;
    [Dependency] private OccluderSystem _occluders = default!;
    [Dependency] private SharedTransformSystem _transforms = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private TagSystem _tags = default!;

    private static readonly ProtoId<TagPrototype> WindowTag = "Window";
    private readonly List<(SharedPointLightComponent Light, float Energy)> _hiddenLights = new();
    private readonly HashSet<EntityUid> _glassCandidates = new();
    private readonly List<MapCoordinates> _visibleGlass = new();
    private TileVisionOverlay? _overlay;
    private bool _enabled;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<LocalPlayerAttachedEvent>(OnPlayerAttached);
        SubscribeLocalEvent<LocalPlayerDetachedEvent>(OnPlayerDetached);
        _cfg.OnValueChanged(CCVars.TileVisionOverlay, SetEnabled, true);
    }

    private void OnPlayerAttached(LocalPlayerAttachedEvent ev)
    {
        AddOverlay();
    }

    private void OnPlayerDetached(LocalPlayerDetachedEvent ev)
    {
        RemoveOverlay();
    }

    private void AddOverlay()
    {
        if (!_enabled || _overlay != null)
            return;

        _overlay = new TileVisionOverlay();
        _overlayManager.AddOverlay(_overlay);
    }

    private void RemoveOverlay()
    {
        if (_overlay == null)
            return;

        _overlayManager.RemoveOverlay(_overlay);
        _overlay = null;
    }

    public void RenderViewport(IClydeViewport viewport)
    {
        if (_overlay is not { } overlay ||
            viewport.Eye is not { DrawFov: true, DrawLight: true } eye ||
            _player.LocalEntity is not { } viewer ||
            !TryComp<TransformComponent>(viewer, out var transform) ||
            transform.MapID != eye.Position.MapId ||
            transform.GridUid is not { } grid ||
            !HasComp<MapGridComponent>(grid))
        {
            viewport.Render();
            return;
        }

        // Replace both engine FoV passes for this render. Keep point-light occlusion.
        eye.DrawFov = false;
        overlay.ActiveViewport = viewport;
        try
        {
            HideOccludedLights(viewport, _transforms.GetMapCoordinates(transform));
            viewport.Render();
        }
        finally
        {
            foreach (var (light, energy) in _hiddenLights)
                SetRenderEnergy(light, energy);

            _hiddenLights.Clear();
            overlay.ActiveViewport = null;
            eye.DrawFov = true;
        }
    }

    private void HideOccludedLights(IClydeViewport viewport, MapCoordinates origin)
    {
        if (!Matrix3x2.Invert(viewport.GetWorldToLocalMatrix(), out var localToWorld))
            return;

        var pixels = Box2.FromDimensions(Vector2.Zero, (Vector2) viewport.Size);
        var worldBounds = localToWorld.TransformBox(pixels);
        FindVisibleGlass(origin, worldBounds);
        var treeBounds = worldBounds.Enlarged(_cfg.GetCVar(Robust.Shared.CVars.MaxLightRadius));
        var state = (System: this, Origin: origin, Bounds: worldBounds);
        foreach (var (uid, tree) in _lights.GetIntersectingTrees(origin.MapId, treeBounds))
        {
            var bounds = _transforms.GetInvWorldMatrix(uid).TransformBox(worldBounds);
            tree.Tree.QueryAabb(ref state, static (
                ref (TileVisionOverlaySystem System, MapCoordinates Origin, Box2 Bounds) query,
                in ComponentTreeEntry<SharedPointLightComponent> entry) =>
            {
                query.System.HideOccludedLight(entry, query.Origin, query.Bounds);
                return true;
            }, bounds);
        }
    }

    private void FindVisibleGlass(MapCoordinates origin, Box2 bounds)
    {
        _visibleGlass.Clear();
        _glassCandidates.Clear();
        _lookup.GetEntitiesIntersecting(origin.MapId, bounds, _glassCandidates, LookupFlags.StaticSundries | LookupFlags.Approximate);
        foreach (var entity in _glassCandidates)
        {
            var transparentDoor = TryComp<DoorComponent>(entity, out var door) && !door.Occludes;
            if ((!transparentDoor && !_tags.HasTag(entity, WindowTag)) ||
                (TryComp<OccluderComponent>(entity, out var occluder) && occluder.Enabled) ||
                !TryComp<TransformComponent>(entity, out var transform) || !transform.Anchored)
                continue;

            var pane = _transforms.GetMapCoordinates(transform);
            if (_occluders.InRangeUnoccluded(origin, pane, OccluderSystem.MaxRaycastRange, ignoreTouching: false))
                _visibleGlass.Add(pane);
        }
    }

    private void HideOccludedLight(in ComponentTreeEntry<SharedPointLightComponent> entry, MapCoordinates origin, Box2 bounds)
    {
        var light = entry.Component;
        if (light.Energy == 0f)
            return;

        var (position, rotation) = _transforms.GetWorldPositionRotation(entry.Transform);
        position += rotation.RotateVec(light.Offset);
        if (!new Circle(position, light.Radius).Intersects(bounds))
            return;

        var offset = position - origin.Position;
        var maxRange = OccluderSystem.MaxRaycastRange;
        if (offset.LengthSquared() > maxRange * maxRange)
            offset = Vector2.Normalize(offset) * (maxRange - 0.01f);

        if (_occluders.InRangeUnoccluded(origin, new MapCoordinates(origin.Position + offset, origin.MapId), maxRange, ignoreTouching: false))
            return;

        // A wall can hide the source itself while its light reaches a visible pane.
        // Keep that light active and let its normal shadows shape the spill through the glass.
        var source = new MapCoordinates(position, origin.MapId);
        var range = MathF.Min(light.Radius, maxRange);
        foreach (var pane in _visibleGlass)
        {
            if (_occluders.InRangeUnoccluded(pane, source, range, ignoreTouching: false))
                return;
        }

        _hiddenLights.Add((light, light.Energy));
        SetRenderEnergy(light, 0f);
    }

    private static void SetRenderEnergy(SharedPointLightComponent light, float energy)
    {
        // This override lasts one draw and must not dirty the networked light state.
#pragma warning disable RA0002
        light.Energy = energy;
#pragma warning restore RA0002
    }

    private void SetEnabled(bool enabled)
    {
        _enabled = enabled;

        if (enabled && _player.LocalEntity != null)
            AddOverlay();
        else
            RemoveOverlay();
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _cfg.UnsubValueChanged(CCVars.TileVisionOverlay, SetEnabled);
        RemoveOverlay();
    }
}
