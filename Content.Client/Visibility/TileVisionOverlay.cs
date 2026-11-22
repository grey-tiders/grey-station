using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Timing;

namespace Content.Client.Visibility;

/// <summary>
/// Masks unseen map tiles with black, producing grid-aligned FOV shadows.
/// </summary>
public sealed partial class TileVisionOverlay : Overlay
{
    [Dependency] private IEntityManager _entManager = default!;
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private IGameTiming _timing = default!;

    private readonly HashSet<Vector2i> _tiles = new();
    private readonly HashSet<Vector2i> _opaque = new();
    private readonly HashSet<Vector2i> _visible = new();
    private readonly HashSet<EntityUid> _intersecting = new();

    private readonly EntityQuery<OccluderComponent> _occluderQuery;
    private readonly EntityQuery<TransformComponent> _transformQuery;

    private float _refreshTimer;
    private const float RefreshInterval = 1f / 30f;
    private EntityUid _cachedGrid = EntityUid.Invalid;
    private Vector2i _cachedViewerTile;
    private Box2i _cachedTileBounds;
    private bool _hasCache;

    public override OverlaySpace Space => OverlaySpace.WorldSpace;
    public IClydeViewport? ActiveViewport { get; set; }

    public TileVisionOverlay()
    {
        IoCManager.InjectDependencies(this);
        _occluderQuery = _entManager.GetEntityQuery<OccluderComponent>();
        _transformQuery = _entManager.GetEntityQuery<TransformComponent>();
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (args.Viewport != ActiveViewport ||
            _player.LocalEntity is not { } viewer ||
            !_transformQuery.TryGetComponent(viewer, out var viewerXform) ||
            viewerXform.GridUid is not { } gridUid ||
            !_entManager.TryGetComponent(gridUid, out MapGridComponent? grid))
            return;

        var maps = _entManager.System<SharedMapSystem>();
        var lookups = _entManager.System<EntityLookupSystem>();
        var transforms = _entManager.System<SharedTransformSystem>();
        var viewerTile = maps.LocalToTile(gridUid, grid, viewerXform.Coordinates);
        var gridMatrix = transforms.GetWorldMatrix(gridUid);
        var inverseGridMatrix = transforms.GetInvWorldMatrix(gridUid);
        var localBounds = inverseGridMatrix.TransformBox(args.WorldBounds);
        var tileSize = grid.TileSize;
        var tileBounds = new Box2i(
            (int) MathF.Floor(localBounds.Left / tileSize),
            (int) MathF.Floor(localBounds.Bottom / tileSize),
            (int) MathF.Ceiling(localBounds.Right / tileSize),
            (int) MathF.Ceiling(localBounds.Top / tileSize));

        _refreshTimer -= (float) _timing.FrameTime.TotalSeconds;
        if (!_hasCache || _cachedGrid != gridUid || _cachedViewerTile != viewerTile ||
            !_cachedTileBounds.Equals(tileBounds) || _refreshTimer <= 0f)
        {
            _refreshTimer = RefreshInterval;
            _cachedGrid = gridUid;
            _cachedViewerTile = viewerTile;
            _cachedTileBounds = tileBounds;
            _hasCache = true;

            // Space outside the grid's stored chunks still needs a visibility check.
            _tiles.Clear();
            for (var x = tileBounds.Left; x < tileBounds.Right; x++)
            {
                for (var y = tileBounds.Bottom; y < tileBounds.Top; y++)
                    _tiles.Add(new Vector2i(x, y));
            }

            _opaque.Clear();
            foreach (var tile in _tiles)
            {
                _intersecting.Clear();
                lookups.GetLocalEntitiesIntersecting(
                    gridUid,
                    tile,
                    _intersecting,
                    flags: LookupFlags.All | LookupFlags.Approximate,
                    gridComp: grid);

                foreach (var entity in _intersecting)
                {
                    if (_occluderQuery.TryGetComponent(entity, out var occluder) && occluder.Enabled)
                    {
                        _opaque.Add(tile);
                        break;
                    }
                }
            }

            _visible.Clear();
            foreach (var tile in _tiles)
            {
                if (HasLineOfSight(viewerTile, tile))
                    _visible.Add(tile);
            }
        }

        if (_tiles.Count == 0)
            return;

        var tileToScreen = Matrix3x2.Multiply(gridMatrix, args.Viewport.GetWorldToLocalMatrix());
        var pixelsPerTileUnit = MathF.Sqrt(tileToScreen.M11 * tileToScreen.M11 + tileToScreen.M12 * tileToScreen.M12);
        var overlap = pixelsPerTileUnit > 0f ? 0.5f / pixelsPerTileUnit : 0f;

        var handle = args.WorldHandle;
        handle.SetTransform(gridMatrix);
        foreach (var tile in _tiles)
        {
            if (_visible.Contains(tile))
                continue;

            var bounds = lookups.GetLocalBounds(tile, tileSize).Enlarged(overlap);
            handle.DrawRect(bounds, Color.Black);
        }

        handle.SetTransform(Matrix3x2.Identity);
    }

    private bool HasLineOfSight(Vector2i source, Vector2i target)
    {
        var x = source.X;
        var y = source.Y;
        var dx = Math.Abs(target.X - source.X);
        var dy = Math.Abs(target.Y - source.Y);
        var stepX = source.X < target.X ? 1 : -1;
        var stepY = source.Y < target.Y ? 1 : -1;
        var error = dx - dy;

        while (x != target.X || y != target.Y)
        {
            var twiceError = error * 2;
            var moveX = twiceError > -dy;
            var moveY = twiceError < dx;
            var oldX = x;
            var oldY = y;

            if (moveX)
            {
                error -= dy;
                x += stepX;
            }

            if (moveY)
            {
                error += dx;
                y += stepY;
            }

            // Do not let sight pass diagonally through a pair of touching walls.
            if (moveX && moveY &&
                _opaque.Contains(new Vector2i(x, oldY)) &&
                _opaque.Contains(new Vector2i(oldX, y)))
                return false;

            var current = new Vector2i(x, y);
            if (!_opaque.Contains(current))
                continue;

            // The wall itself remains visible; tiles beyond it do not.
            return current == target;
        }

        return true;
    }
}
