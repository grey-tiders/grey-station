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
    private uint _lastUpdateFrame;
    private const float RefreshInterval = 1f / 30f;
    private const float ViewerTileBoundaryMargin = 0.08f;
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
        if (!UpdateVisibility(args.Viewport, args.WorldBounds, out var grid))
            return;

        var lookups = _entManager.System<EntityLookupSystem>();
        var gridMatrix = _entManager.System<SharedTransformSystem>().GetWorldMatrix(grid.Owner);
        var tileToScreen = Matrix3x2.Multiply(gridMatrix, args.Viewport.GetWorldToLocalMatrix());
        var pixelsPerTileUnit = MathF.Sqrt(tileToScreen.M11 * tileToScreen.M11 + tileToScreen.M12 * tileToScreen.M12);
        var overlap = pixelsPerTileUnit > 0f ? 0.5f / pixelsPerTileUnit : 0f;

        var handle = args.WorldHandle;
        handle.SetTransform(gridMatrix);
        foreach (var tile in _tiles)
        {
            if (_visible.Contains(tile))
                continue;

            var bounds = lookups.GetLocalBounds(tile, grid.Comp.TileSize).Enlarged(overlap);
            handle.DrawRect(bounds, Color.Black);
        }

        handle.SetTransform(Matrix3x2.Identity);
    }

    internal void MaskExteriorAmbientLight(
        in OverlayDrawArgs args,
        IRenderTexture target,
        Box2Rotated bounds)
    {
        var viewport = args.Viewport;
        if (viewport.Eye is not { } eye || !UpdateVisibility(viewport, bounds, out var grid))
            return;

        var maps = _entManager.System<SharedMapSystem>();
        var lookups = _entManager.System<EntityLookupSystem>();
        var gridMatrix = _entManager.System<SharedTransformSystem>().GetWorldMatrix(grid.Owner);
        var scale = viewport.RenderScale * (viewport.LightRenderTarget.Size / (Vector2) viewport.Size);
        var gridToTarget = Matrix3x2.Multiply(gridMatrix, target.GetWorldToLocalMatrix(eye, scale));

        var handle = args.WorldHandle;
        handle.RenderInRenderTarget(target, () =>
        {
            handle.SetTransform(gridToTarget);
            foreach (var tile in _tiles)
            {
                // Remove only ambient light from unseen space before it can blur onto the hull.
                if (!_visible.Contains(tile) && maps.GetTileRef(grid.Owner, grid.Comp, tile).Tile.IsEmpty)
                    handle.DrawRect(lookups.GetLocalBounds(tile, grid.Comp.TileSize), Color.Black);
            }
        }, null);
        handle.SetTransform(Matrix3x2.Identity);
    }

    private bool UpdateVisibility(IClydeViewport viewport, Box2Rotated worldBounds, out Entity<MapGridComponent> viewerGrid)
    {
        viewerGrid = default;
        if (viewport != ActiveViewport ||
            _player.LocalEntity is not { } viewer ||
            !_transformQuery.TryGetComponent(viewer, out var viewerXform) ||
            viewerXform.GridUid is not { } gridUid ||
            !_entManager.TryGetComponent(gridUid, out MapGridComponent? grid))
            return false;

        viewerGrid = (gridUid, grid);

        var maps = _entManager.System<SharedMapSystem>();
        var lookups = _entManager.System<EntityLookupSystem>();
        var transforms = _entManager.System<SharedTransformSystem>();
        var viewerPosition = maps.LocalToGrid(gridUid, grid, viewerXform.Coordinates);
        var tileSize = grid.TileSize;
        var viewerTile = GetViewerTile(viewerGrid, viewerPosition, lookups);

        var inverseGridMatrix = transforms.GetInvWorldMatrix(gridUid);
        var localBounds = inverseGridMatrix.TransformBox(worldBounds);
        var tileBounds = new Box2i(
            (int) MathF.Floor(localBounds.Left / tileSize),
            (int) MathF.Floor(localBounds.Bottom / tileSize),
            (int) MathF.Ceiling(localBounds.Right / tileSize),
            (int) MathF.Ceiling(localBounds.Top / tileSize));

        if (_lastUpdateFrame != _timing.CurFrame)
        {
            _lastUpdateFrame = _timing.CurFrame;
            _refreshTimer -= (float) _timing.FrameTime.TotalSeconds;
        }

        if (!_hasCache || _cachedGrid != gridUid || _cachedViewerTile != viewerTile ||
            !_cachedTileBounds.Contains(tileBounds) || _refreshTimer <= 0f)
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

        return _tiles.Count != 0;
    }

    internal Vector2i GetViewerTile(Entity<MapGridComponent> grid, Vector2 position, EntityLookupSystem lookups)
    {
        var tileSize = grid.Comp.TileSize;
        var tile = new Vector2i(
            (int) MathF.Floor(position.X / tileSize),
            (int) MathF.Floor(position.Y / tileSize));

        // Collision prediction can move the player back and forth across a tile edge.
        if (_hasCache && _cachedGrid == grid.Owner && _cachedViewerTile != tile &&
            lookups.GetLocalBounds(_cachedViewerTile, tileSize)
                .Enlarged(ViewerTileBoundaryMargin * tileSize).Contains(position))
        {
            return _cachedViewerTile;
        }

        return tile;
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
