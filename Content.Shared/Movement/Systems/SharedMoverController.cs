using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Content.Shared._DV.NodeCrawl;
using Content.Shared.ActionBlocker;
using Content.Shared.CCVar;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Content.Shared.Friction;
using Content.Shared.Gravity;
using Content.Shared.Movement.Components;
using Content.Shared.Inventory;
using Content.Shared.Maps;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Events;
using Content.Shared.Physics;
using Content.Shared.Shuttles.Components;
using Content.Shared.Tag;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Controllers;
using Robust.Shared.Physics.Events;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using PullableComponent = Content.Shared.Movement.Pulling.Components.PullableComponent;
// ES START
using Content.Shared._ES.Audio.Components;
// ES END

namespace Content.Shared.Movement.Systems;

/// <summary>
///     Handles player and NPC mob movement.
///     NPCs are handled server-side only.
/// </summary>
public abstract partial class SharedMoverController : VirtualController
{
    [Dependency] private IConfigurationManager _configManager = default!;
    [Dependency] protected IGameTiming Timing = default!;
    [Dependency] private ITileDefinitionManager _tileDefinitionManager = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private SharedContainerSystem _container = default!;
    [Dependency] private SharedMapSystem _mapSystem = default!;
    [Dependency] private TurfSystem _turf = default!;
    [Dependency] private SharedGravitySystem _gravity = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private TagSystem _tags = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;
    [Dependency] private NodeCrawlerMovementSystem _nodeCrawlerMovement = default!; // DeltaV - node crawling

    protected EntityQuery<CanMoveInAirComponent> CanMoveInAirQuery;
    protected EntityQuery<FootstepModifierComponent> FootstepModifierQuery;
    protected EntityQuery<FTLComponent> FTLQuery;
    protected EntityQuery<InputMoverComponent> MoverQuery;
    protected EntityQuery<MapComponent> MapQuery;
    protected EntityQuery<MapGridComponent> MapGridQuery;
    protected EntityQuery<MobMoverComponent> MobMoverQuery;
    protected EntityQuery<MovementRelayTargetComponent> RelayTargetQuery;
    protected EntityQuery<MovementSpeedModifierComponent> ModifierQuery;
    protected EntityQuery<NoRotateOnMoveComponent> NoRotateQuery;
    protected EntityQuery<TileMovementComponent> TileMovementQuery;
    protected EntityQuery<DoorComponent> DoorQuery;
    protected EntityQuery<PhysicsComponent> PhysicsQuery;
    protected EntityQuery<PilotComponent> PilotQuery;
    protected EntityQuery<PreventPilotComponent> PreventPilotQuery;
    protected EntityQuery<RelayInputMoverComponent> RelayQuery;
    protected EntityQuery<PullableComponent> PullableQuery;
    protected EntityQuery<TransformComponent> XformQuery;
    // ES START
    protected EntityQuery<ESIgnoreBarefootSoundsComponent> ESIgnoreBarefootSoundsQuery;
    // ES END

    private static readonly ProtoId<TagPrototype> FootstepSoundTag = "FootstepSound";

    private bool _relativeMovement;
    private float _minDamping;
    private float _airDamping;
    private float _offGridDamping;

    /// <summary>
    /// Cache the mob movement calculation to re-use elsewhere.
    /// </summary>
    public Dictionary<EntityUid, bool> UsedMobMovement = new();

    private readonly HashSet<EntityUid> _aroundColliderSet = [];

    public override void Initialize()
    {
        UpdatesBefore.Add(typeof(TileFrictionController));
        base.Initialize();

        MoverQuery = GetEntityQuery<InputMoverComponent>();
        MobMoverQuery = GetEntityQuery<MobMoverComponent>();
        ModifierQuery = GetEntityQuery<MovementSpeedModifierComponent>();
        RelayTargetQuery = GetEntityQuery<MovementRelayTargetComponent>();
        PhysicsQuery = GetEntityQuery<PhysicsComponent>();
        RelayQuery = GetEntityQuery<RelayInputMoverComponent>();
        PullableQuery = GetEntityQuery<PullableComponent>();
        XformQuery = GetEntityQuery<TransformComponent>();
        NoRotateQuery = GetEntityQuery<NoRotateOnMoveComponent>();
        CanMoveInAirQuery = GetEntityQuery<CanMoveInAirComponent>();
        TileMovementQuery = GetEntityQuery<TileMovementComponent>();
        DoorQuery = GetEntityQuery<DoorComponent>();
        FootstepModifierQuery = GetEntityQuery<FootstepModifierComponent>();
        MapGridQuery = GetEntityQuery<MapGridComponent>();
        MapQuery = GetEntityQuery<MapComponent>();
        // ES START
        ESIgnoreBarefootSoundsQuery = GetEntityQuery<ESIgnoreBarefootSoundsComponent>();
        // ES END
        FTLQuery = GetEntityQuery<FTLComponent>();
        PilotQuery = GetEntityQuery<PilotComponent>();
        PreventPilotQuery = GetEntityQuery<PreventPilotComponent>();

        SubscribeLocalEvent<MovementSpeedModifierComponent, TileFrictionEvent>(OnTileFriction);
        SubscribeLocalEvent<InputMoverComponent, ComponentStartup>(OnMoverStartup);
        SubscribeLocalEvent<InputMoverComponent, PhysicsBodyTypeChangedEvent>(OnPhysicsBodyChanged);
        SubscribeLocalEvent<InputMoverComponent, UpdateCanMoveEvent>(OnCanMove);

        InitializeInput();
        InitializeRelay();
        Subs.CVar(_configManager, CCVars.RelativeMovement, value => _relativeMovement = value, true);
        Subs.CVar(_configManager, CCVars.MinFriction, value => _minDamping = value, true);
        Subs.CVar(_configManager, CCVars.AirFriction, value => _airDamping = value, true);
        Subs.CVar(_configManager, CCVars.OffgridFriction, value => _offGridDamping = value, true);
    }

    protected virtual void OnMoverStartup(Entity<InputMoverComponent> ent, ref ComponentStartup args)
    {
       _blocker.UpdateCanMove(ent, ent.Comp);
    }

    public override void Shutdown()
    {
        base.Shutdown();
        ShutdownInput();
    }

    public override void UpdateAfterSolve(bool prediction, float frameTime)
    {
        base.UpdateAfterSolve(prediction, frameTime);
        UsedMobMovement.Clear();
    }

    /// <summary>
    ///     Movement while considering actionblockers, weightlessness, etc.
    /// </summary>
    protected void HandleMobMovement(
        Entity<InputMoverComponent> entity,
        float frameTime)
    {
        var uid = entity.Owner;
        var mover = entity.Comp;

        // If we're a relay then apply all of our data to the parent instead and go next.
        if (RelayQuery.TryComp(uid, out var relay))
        {
            if (!MoverQuery.TryComp(relay.RelayEntity, out var relayTargetMover))
                return;

            // Always lerp rotation so relay entities aren't cooked.
            LerpRotation(uid, mover, frameTime);
            var dirtied = false;

            if (relayTargetMover.RelativeEntity != mover.RelativeEntity)
            {
                relayTargetMover.RelativeEntity = mover.RelativeEntity;
                dirtied = true;
            }

            if (relayTargetMover.RelativeRotation != mover.RelativeRotation)
            {
                relayTargetMover.RelativeRotation = mover.RelativeRotation;
                dirtied = true;
            }

            if (relayTargetMover.TargetRelativeRotation != mover.TargetRelativeRotation)
            {
                relayTargetMover.TargetRelativeRotation = mover.TargetRelativeRotation;
                dirtied = true;
            }

            if (relayTargetMover.CanMove != mover.CanMove)
            {
                relayTargetMover.CanMove = mover.CanMove;
                dirtied = true;
            }

            if (dirtied)
            {
                Dirty(relay.RelayEntity, relayTargetMover);
            }

            return;
        }

        if (!XformQuery.TryComp(entity.Owner, out var xform))
            return;

        RelayTargetQuery.TryComp(uid, out var relayTarget);
        var relaySource = relayTarget?.Source;

        // If we're not the target of a relay then handle lerp data.
        if (relaySource == null)
        {
            // Update relative movement
            if (mover.LerpTarget < Timing.CurTime)
            {
                TryUpdateRelative(uid, mover, xform);
            }

            LerpRotation(uid, mover, frameTime);
        }

        // If we can't move then just use tile-friction / no movement handling.
        if (!PhysicsQuery.TryComp(uid, out var physicsComponent))
        {
            UsedMobMovement[uid] = false;
            return;
        }

        if (!mover.CanMove || PullableQuery.TryGetComponent(uid, out var pullable) && pullable.BeingPulled)
        {
            if (TileMovementQuery.TryComp(uid, out var interruptedTileMovement))
                CancelTileMovement(uid, physicsComponent, interruptedTileMovement);

            UsedMobMovement[uid] = false;
            return;
        }

        /*
         * This assert is here because any entity using inputs to move should be a Kinematic Controller.
         * Kinematic Controllers are not built to use the entirety of the Physics engine by intention and
         * setting an input mover to Dynamic will cause the Physics engine to occasionally throw asserts.
         * In addition, SharedMoverController applies its own forms of fake impulses and friction outside
         * Physics simulation, which will cause issues for Dynamic bodies (Such as Friction being applied twice).
         * Kinematic bodies have even less Physics options and as such aren't suitable for a player, especially
         * when we move to Box2D v3 where there will be more support for players updating outside of simulation.
         * However, Kinematic bodies are useful currently for mobs which explicitly ignore physics, you should use
         * this type extremely sparingly and only for mobs which *explicitly* disobey the laws of physics (A-Ghosts).
         * Lastly, static bodies can't move so they shouldn't be updated. If a static body makes it here we're
         * doing unnecessary calculations.
         * Only a Kinematic Controller should be making it to this point.
         */
        DebugTools.Assert(physicsComponent.BodyType == BodyType.KinematicController || physicsComponent.BodyType == BodyType.Kinematic,
            $"Input mover: {ToPrettyString(uid)} in HandleMobMovement is not the correct BodyType, BodyType found: {physicsComponent.BodyType}, expected: KinematicController.");

        // If the body is in air but isn't weightless then it can't move
        var weightless = _gravity.IsWeightless(uid);
        var inAirHelpless = false;

        if (physicsComponent.BodyStatus != BodyStatus.OnGround && !CanMoveInAirQuery.HasComponent(uid))
        {
            if (!weightless)
            {
                UsedMobMovement[uid] = false;
                return;
            }
            inAirHelpless = true;
        }

        if ((weightless || physicsComponent.BodyStatus != BodyStatus.OnGround) &&
            TileMovementQuery.TryComp(uid, out var airborneTileMovement) &&
            (airborneTileMovement.SlideActive || airborneTileMovement.NoProgressTicks != 0 ||
             airborneTileMovement.BlockedDirection != Vector2.Zero))
        {
            airborneTileMovement.SlideActive = false;
            airborneTileMovement.NoProgressTicks = 0;
            airborneTileMovement.BlockedDirection = Vector2.Zero;
            Dirty(uid, airborneTileMovement);
        }

        UsedMobMovement[uid] = true;

        var moveSpeedComponent = ModifierQuery.CompOrNull(uid);

        // ES START
        // jank to get around movebuttons, passed to functions below
        bool forceWalk = false;
        // ES END
        float friction;
        float accel;
        Vector2 wishDir;
        var velocity = physicsComponent.LinearVelocity;
        var worldRot = _transform.GetWorldRotation(xform);

        // Get current tile def for things like speed/friction mods
        ContentTileDefinition? tileDef = null;

        // Begin DeltaV Additions - node crawling
        if (_nodeCrawlerMovement.TryTick((uid, mover, physicsComponent, xform)))
            return;
        // End DeltaV Additions

        var touching = false;
        // Whether we use tilefriction or not
        if (weightless || inAirHelpless)
        {
            // Find the speed we should be moving at and make sure we're not trying to move faster than that
            var walkSpeed = moveSpeedComponent?.WeightlessWalkSpeed ?? MovementSpeedModifierComponent.DefaultBaseWalkSpeed;
            var sprintSpeed = moveSpeedComponent?.WeightlessSprintSpeed ?? MovementSpeedModifierComponent.DefaultBaseSprintSpeed;

            wishDir = AssertValidWish(mover, walkSpeed, sprintSpeed);

            var ev = new CanWeightlessMoveEvent(uid);
            RaiseLocalEvent(uid, ref ev, true);

            touching = ev.CanMove;
            var onGrid = xform.GridUid != null || MapGridQuery.HasComp(xform.GridUid);

            // Check if we're close enough to a wall to count as touching
            if (!touching && MobMoverQuery.TryComp(uid, out var mobMover))
                touching |= IsAroundCollider((uid, physicsComponent, mobMover, xform), onGrid);

            // If we're touching then use the weightless values
            if (touching)
            {
                touching = true;
                if (wishDir != Vector2.Zero)
                    friction = moveSpeedComponent?.WeightlessFriction ?? _airDamping;
                else
                    friction = moveSpeedComponent?.WeightlessFrictionNoInput ?? _airDamping;
            }
            // Otherwise use the off-grid values.
            else
            {
                friction = moveSpeedComponent?.OffGridFriction ?? _offGridDamping;
            }

            accel = moveSpeedComponent?.WeightlessAcceleration ?? MovementSpeedModifierComponent.DefaultWeightlessAcceleration;
        }
        else
        {
            if (MapGridQuery.TryComp(xform.GridUid, out var gridComp)
                && _mapSystem.TryGetTileRef(xform.GridUid.Value, gridComp, xform.Coordinates, out var tile)
                && physicsComponent.BodyStatus == BodyStatus.OnGround)
                tileDef = (ContentTileDefinition)_tileDefinitionManager[tile.Tile.TypeId];

            var walkSpeed = moveSpeedComponent?.CurrentWalkSpeed ?? MovementSpeedModifierComponent.DefaultBaseWalkSpeed;
            var sprintSpeed = moveSpeedComponent?.CurrentSprintSpeed ?? MovementSpeedModifierComponent.DefaultBaseSprintSpeed;

            // ES START
            // if facing backwards, start walking
            // only applies in the non-weightless path
            {
                var backwardsAngle = moveSpeedComponent?.BackwardsAngle ??
                                     MovementSpeedModifierComponent.ESDefaultBackwardsAngle;
                var rotNorm = worldRot.ToWorldVec().Normalized();
                var velNorm = velocity.Normalized();
                var cosAngle = Vector2.Dot(velNorm, rotNorm);
                var threshold = new Angle(MathF.PI) - (backwardsAngle / 2);

                forceWalk = cosAngle < Math.Cos(threshold.Theta);
                wishDir = AssertValidWish(mover, walkSpeed, sprintSpeed, forceWalk);
            }
            // ES END

            if (wishDir != Vector2.Zero)
            {
                friction = moveSpeedComponent?.Friction ?? MovementSpeedModifierComponent.DefaultFriction;
                friction *= tileDef?.MobFriction ?? tileDef?.Friction ?? 1f;
            }
            else
            {
                friction = moveSpeedComponent?.FrictionNoInput ?? MovementSpeedModifierComponent.DefaultFrictionNoInput;
                friction *= tileDef?.Friction ?? 1f;
            }

            accel = moveSpeedComponent?.Acceleration ?? MovementSpeedModifierComponent.DefaultAcceleration;
            accel *= tileDef?.MobAcceleration ?? 1f;
        }

        // Tile movers use the same shared client/server simulation as ordinary movement.
        // Weightless and airborne movement remains on the existing physics path.
        if (!weightless && physicsComponent.BodyStatus == BodyStatus.OnGround &&
            TileMovementQuery.TryComp(uid, out var tileMovement))
        {
            var speed = mover.Sprinting
                ? moveSpeedComponent?.CurrentSprintSpeed ?? MovementSpeedModifierComponent.DefaultBaseSprintSpeed
                : moveSpeedComponent?.CurrentWalkSpeed ?? MovementSpeedModifierComponent.DefaultBaseWalkSpeed;

            if (wishDir != Vector2.Zero && MobMoverQuery.TryGetComponent(uid, out var tileMobMover) &&
                TryGetSound(false, uid, mover, tileMobMover, xform, out var tileSound, tileDef: tileDef))
            {
                var soundModifier = mover.Sprinting ? InputMoverComponent.SprintingSoundModifier
                    : InputMoverComponent.WalkingSoundModifier;
                var audioParams = tileSound.Params
                    .WithVolume(tileSound.Params.Volume + soundModifier)
                    .WithVariation(tileSound.Params.Variation ?? tileMobMover.FootstepVariation);
                _audio.PlayPredicted(tileSound, uid, relaySource ?? uid, audioParams);
            }

            SetWishDir((uid, mover), wishDir);
            HandleTileMovement(uid, tileMovement, mover, xform, physicsComponent, wishDir, speed, frameTime, tileDef);
            UsedMobMovement[uid] = true;
            return;
        }

        // This way friction never exceeds acceleration when you're trying to move.
        // If you want to slow down an entity with "friction" you shouldn't be using this system.
        if (wishDir != Vector2.Zero)
            friction = Math.Min(friction, accel);
        friction = Math.Max(friction, _minDamping);
        var minimumFrictionSpeed = moveSpeedComponent?.MinimumFrictionSpeed ?? MovementSpeedModifierComponent.DefaultMinimumFrictionSpeed;
        Friction(minimumFrictionSpeed, frameTime, friction, ref velocity);

        if (!weightless || touching)
            Accelerate(ref velocity, in wishDir, accel, frameTime);

        SetWishDir((uid, mover), wishDir);

        /*
         * SNAKING!!! >-( 0 ================>
         * Snaking is a feature where you can move faster by strafing in a direction perpendicular to the
         * direction you intend to move while still holding the movement key for the direction you're trying to move.
         * Snaking only works if acceleration exceeds friction, and it's effectiveness scales as acceleration continues
         * to exceed friction.
         * Snaking works because friction is applied first in the direction of our current velocity, while acceleration
         * is applied after in our "Wish Direction" and is capped by the dot of our wish direction and current direction.
         * This means when you change direction, you're technically able to accelerate more than what the velocity cap
         * allows, but friction normally eats up the extra movement you gain.
         * By strafing as stated above you can increase your speed by about 1.4 (square root of 2).
         * This only works if friction is low enough so be sure that anytime you are letting a mob move in a low friction
         * environment you take into account the fact they can snake! Also be sure to lower acceleration as well to
         * prevent jerky movement!
         */
        PhysicsSystem.SetLinearVelocity(uid, velocity, body: physicsComponent);

        // Ensures that players do not spiiiiiiin
        PhysicsSystem.SetAngularVelocity(uid, 0, body: physicsComponent);

        // Handle footsteps at the end
        if (wishDir != Vector2.Zero)
        {
            if (!NoRotateQuery.HasComponent(uid))
            {
                // TODO apparently this results in a duplicate move event because "This should have its event run during
                // island solver"??. So maybe SetRotation needs an argument to avoid raising an event?
                _transform.SetLocalRotation(uid, xform.LocalRotation + wishDir.ToWorldAngle() - worldRot, xform);
            }

            if (!weightless && MobMoverQuery.TryGetComponent(uid, out var mobMover) &&
                TryGetSound(weightless, uid, mover, mobMover, xform, out var sound, tileDef: tileDef))
            {
                var soundModifier = mover.Sprinting ? InputMoverComponent.SprintingSoundModifier
                    : InputMoverComponent.WalkingSoundModifier;

                var audioParams = sound.Params
                    .WithVolume(sound.Params.Volume + soundModifier)
                    .WithVariation(sound.Params.Variation ?? mobMover.FootstepVariation);

                // If we're a relay target then predict the sound for all relays.
                if (relaySource != null)
                {
                    _audio.PlayPredicted(sound, uid, relaySource.Value, audioParams);
                }
                else
                {
                    _audio.PlayPredicted(sound, uid, uid, audioParams);
                }
            }
        }
    }

    private void HandleTileMovement(
        EntityUid uid,
        TileMovementComponent tileMovement,
        InputMoverComponent mover,
        TransformComponent xform,
        PhysicsComponent physics,
        Vector2 wishDir,
        float speed,
        float frameTime,
        ContentTileDefinition? tileDef)
    {
        if (!float.IsFinite(speed) || speed <= 0f)
        {
            CancelTileMovement(uid, physics, tileMovement);
            return;
        }

        var inputDirection = wishDir.LengthSquared() > 0.0001f ? Vector2.Normalize(wishDir) : Vector2.Zero;
        if (tileMovement.BlockedDirection != Vector2.Zero)
        {
            if (inputDirection != Vector2.Zero && Vector2.Dot(tileMovement.BlockedDirection, inputDirection) > 0.95f)
            {
                // Retry held input as soon as the path clears.
                if (IsTileStepBlocked(uid, xform.Coordinates, tileMovement.Destination))
                {
                    PhysicsSystem.SetLinearVelocity(uid, Vector2.Zero, body: physics);
                    PhysicsSystem.SetAngularVelocity(uid, 0f, body: physics);
                    return;
                }
            }

            tileMovement.BlockedDirection = Vector2.Zero;
        }

        if (!tileMovement.SlideActive && inputDirection != Vector2.Zero)
        {
            var parentRotation = Angle.Zero;
            if (XformQuery.TryComp(xform.ParentUid, out var parent))
                parentRotation = _transform.GetWorldRotation(parent);

            var localDirection = (-parentRotation).RotateVec(inputDirection);
            var target = xform.LocalPosition + localDirection;
            tileMovement.Destination = new EntityCoordinates(xform.ParentUid, SnapCoordinatesToTile(target));

            if (IsTileStepBlocked(uid, xform.Coordinates, tileMovement.Destination))
            {
                tileMovement.BlockedDirection = inputDirection;
                PhysicsSystem.SetLinearVelocity(uid, Vector2.Zero, body: physics);
                PhysicsSystem.SetAngularVelocity(uid, 0f, body: physics);
                Dirty(uid, tileMovement);
                return;
            }

            tileMovement.SlideActive = true;
            tileMovement.NoProgressTicks = 0;
            tileMovement.LastTickPosition = xform.LocalPosition;
        }

        // Grid traversal may change the transform parent during a step. Keep the same
        // map-space destination by expressing it in the new parent's local coordinates.
        if (tileMovement.SlideActive && tileMovement.Destination.EntityId != xform.ParentUid)
        {
            var mapDestination = _transform.ToMapCoordinates(tileMovement.Destination, logError: false);
            tileMovement.Destination = _transform.ToCoordinates(xform.ParentUid, mapDestination);
        }

        if (!tileMovement.SlideActive)
        {
            var idleVelocity = physics.LinearVelocity;
            var movementSpeed = ModifierQuery.CompOrNull(uid);
            var friction = movementSpeed?.FrictionNoInput ?? MovementSpeedModifierComponent.DefaultFrictionNoInput;
            friction *= tileDef?.Friction ?? 1f;
            Friction(movementSpeed?.MinimumFrictionSpeed ?? MovementSpeedModifierComponent.DefaultMinimumFrictionSpeed,
                frameTime, friction, ref idleVelocity);
            PhysicsSystem.SetLinearVelocity(uid, idleVelocity, body: physics);
            PhysicsSystem.SetAngularVelocity(uid, 0f, body: physics);
            Dirty(uid, tileMovement);
            return;
        }

        var localDelta = tileMovement.Destination.Position - xform.LocalPosition;
        var remaining = localDelta.Length();
        if (remaining <= Math.Max(0.01f, speed * frameTime))
        {
            _transform.SetLocalPosition(uid, tileMovement.Destination.Position, xform);
            tileMovement.SlideActive = false;
            PhysicsSystem.SetLinearVelocity(uid, Vector2.Zero, body: physics);
            PhysicsSystem.SetAngularVelocity(uid, 0f, body: physics);
            tileMovement.LastTickPosition = tileMovement.Destination.Position;
            Dirty(uid, tileMovement);
            return;
        }

        var parentWorldRotation = Angle.Zero;
        if (XformQuery.TryComp(xform.ParentUid, out var parentTransform))
            parentWorldRotation = _transform.GetWorldRotation(parentTransform);

        var worldDirection = parentWorldRotation.RotateVec(localDelta / remaining);
        PhysicsSystem.SetLinearVelocity(uid, worldDirection * speed, body: physics);
        PhysicsSystem.SetAngularVelocity(uid, 0f, body: physics);

        if (Vector2.DistanceSquared(tileMovement.LastTickPosition, xform.LocalPosition) < 0.0001f)
        {
            tileMovement.NoProgressTicks++;
            if (tileMovement.NoProgressTicks >= 2)
            {
                tileMovement.SlideActive = false;
                tileMovement.BlockedDirection = worldDirection;
                tileMovement.NoProgressTicks = 0;
                PhysicsSystem.SetLinearVelocity(uid, Vector2.Zero, body: physics);
            }
        }
        else
            tileMovement.NoProgressTicks = 0;

        tileMovement.LastTickPosition = xform.LocalPosition;
        if (!NoRotateQuery.HasComponent(uid) && localDelta != Vector2.Zero)
            _transform.SetWorldRotation(xform, worldDirection.ToWorldAngle());

        Dirty(uid, tileMovement);
    }

    private void CancelTileMovement(EntityUid uid, PhysicsComponent? physicsComponent, TileMovementComponent tileMovement)
    {
        var wasSliding = tileMovement.SlideActive;
        var changed = wasSliding || tileMovement.NoProgressTicks != 0 || tileMovement.BlockedDirection != Vector2.Zero;
        tileMovement.SlideActive = false;
        tileMovement.NoProgressTicks = 0;
        tileMovement.BlockedDirection = Vector2.Zero;
        if (wasSliding && physicsComponent != null)
            PhysicsSystem.SetLinearVelocity(uid, Vector2.Zero, body: physicsComponent);
        if (changed)
            Dirty(uid, tileMovement);
    }

    private bool IsTileStepBlocked(EntityUid uid, EntityCoordinates origin, EntityCoordinates destination)
    {
        // Climbing temporarily removes table collision from the mover's solid fixtures.
        var mask = CollisionGroup.None;
        if (TryComp<FixturesComponent>(uid, out var fixtures))
        {
            foreach (var fixture in fixtures.Fixtures.Values)
            {
                if (fixture.Hard)
                    mask |= (CollisionGroup) fixture.CollisionMask;
            }
        }

        if (origin.EntityId != destination.EntityId)
            origin = _transform.ToCoordinates(destination.EntityId, _transform.ToMapCoordinates(origin, logError: false));

        var start = SnapCoordinatesToTile(origin.Position);
        var end = destination.Position;
        if (start.X != end.X && start.Y != end.Y)
        {
            // A diagonal step passes between both neighboring tiles. Doors there
            // count as obstacles because the mover is passing their corners.
            var sideX = _turf.GetTileRef(new EntityCoordinates(destination.EntityId, new Vector2(end.X, start.Y)));
            var sideY = _turf.GetTileRef(new EntityCoordinates(destination.EntityId, new Vector2(start.X, end.Y)));
            if ((sideX is { } x && _turf.IsTileBlocked(x, mask)) ||
                (sideY is { } y && _turf.IsTileBlocked(y, mask)))
                return true;
        }

        // An open grid edge has no destination tile. Physics carries the mover
        // into space and hands off to weightless movement when gravity drops.
        return _turf.GetTileRef(destination) is { } tile &&
               _turf.IsTileBlocked(tile, mask) && !HasBumpOpenDoor(uid, tile);
    }

    private bool HasBumpOpenDoor(EntityUid uid, TileRef tile)
    {
        if (!_tags.HasTag(uid, SharedDoorSystem.DoorBumpTag) || !MapGridQuery.TryComp(tile.GridUid, out var grid))
            return false;

        var anchored = _mapSystem.GetAnchoredEntitiesEnumerator(tile.GridUid, grid, tile.GridIndices);
        while (anchored.MoveNext(out var entity))
        {
            if (!DoorQuery.TryComp(entity, out var door) || !door.BumpOpen ||
                door.State is not (DoorState.Closed or DoorState.Denying))
                continue;

            return true;
        }

        return false;
    }

    private static Vector2 SnapCoordinatesToTile(Vector2 position)
    {
        return new Vector2(MathF.Floor(position.X) + 0.5f, MathF.Floor(position.Y) + 0.5f);
    }

    public Vector2 GetWishDir(Entity<InputMoverComponent?> mover)
    {
        if (!MoverQuery.Resolve(mover.Owner, ref mover.Comp, false))
            return Vector2.Zero;

        return mover.Comp.WishDir;
    }

    public void SetWishDir(Entity<InputMoverComponent> mover, Vector2 wishDir)
    {
        if (mover.Comp.WishDir.Equals(wishDir))
            return;

        mover.Comp.WishDir = wishDir;
        Dirty(mover);
    }

    public void LerpRotation(EntityUid uid, InputMoverComponent mover, float frameTime)
    {
        var angleDiff = Angle.ShortestDistance(mover.RelativeRotation, mover.TargetRelativeRotation);

        // if we've just traversed then lerp to our target rotation.
        if (!angleDiff.EqualsApprox(Angle.Zero, 0.001))
        {
            var adjustment = angleDiff * 5f * frameTime;
            var minAdjustment = 0.01 * frameTime;

            if (angleDiff < 0)
            {
                adjustment = Math.Min(adjustment, -minAdjustment);
                adjustment = Math.Clamp(adjustment, angleDiff, -angleDiff);
            }
            else
            {
                adjustment = Math.Max(adjustment, minAdjustment);
                adjustment = Math.Clamp(adjustment, -angleDiff, angleDiff);
            }

            mover.RelativeRotation = (mover.RelativeRotation + adjustment).FlipPositive();
            Dirty(uid, mover);
        }
        else if (!angleDiff.Equals(Angle.Zero))
        {
            mover.RelativeRotation = mover.TargetRelativeRotation.FlipPositive();
            Dirty(uid, mover);
        }
    }

    public void Friction(float minimumFrictionSpeed, float frameTime, float friction, ref Vector2 velocity)
    {
        var speed = velocity.Length();

        if (speed < minimumFrictionSpeed)
            return;

        // This equation is lifted from the Physics Island solver.
        // We re-use it here because Kinematic Controllers can't/shouldn't use the Physics Friction
        velocity *= Math.Clamp(1.0f - frameTime * friction, 0.0f, 1.0f);

    }

    public void Friction(float minimumFrictionSpeed, float frameTime, float friction, ref float velocity)
    {
        if (Math.Abs(velocity) < minimumFrictionSpeed)
            return;

        // This equation is lifted from the Physics Island solver.
        // We re-use it here because Kinematic Controllers can't/shouldn't use the Physics Friction
        velocity *= Math.Clamp(1.0f - frameTime * friction, 0.0f, 1.0f);

    }

    /// <summary>
    /// Adjusts the current velocity to the target velocity based on the specified acceleration.
    /// </summary>
    public static void Accelerate(ref Vector2 currentVelocity, in Vector2 velocity, float accel, float frameTime)
    {
        var wishDir = velocity != Vector2.Zero ? velocity.Normalized() : Vector2.Zero;
        var wishSpeed = velocity.Length();

        var currentSpeed = Vector2.Dot(currentVelocity, wishDir);
        var addSpeed = wishSpeed - currentSpeed;

        if (addSpeed <= 0f)
            return;

        var accelSpeed = accel * frameTime * wishSpeed;
        accelSpeed = MathF.Min(accelSpeed, addSpeed);

        currentVelocity += wishDir * accelSpeed;
    }

    public bool UseMobMovement(EntityUid uid)
    {
        return UsedMobMovement.TryGetValue(uid, out var used) && used;
    }

    /// <summary>
    /// Used for weightlessness to determine if we are near a wall.
    /// </summary>
    private bool IsAroundCollider(Entity<PhysicsComponent, MobMoverComponent, TransformComponent> entity, bool onGrid)
    {
        var (uid, collider, mover, transform) = entity;
        // enlarge more off grid, on grid its fine to require being kinda close to stuff
        var enlargedAABB = _lookup.GetWorldAABB(entity.Owner, transform).Enlarged(mover.GrabRange);

        _aroundColliderSet.Clear();
        _lookup.GetEntitiesIntersecting(transform.MapID, enlargedAABB, _aroundColliderSet, LookupFlags.Static);
        foreach (var otherEntity in _aroundColliderSet)
        {
            if (otherEntity == uid || _transform.IsParentOf(transform, otherEntity))
                continue; // Don't try to push off of yourself or your children!

            if (!PhysicsQuery.TryComp(otherEntity, out var otherCollider))
                continue;

            // Only allow pushing off of anchored things that have collision.
            // NOTE: collision is one-way - we want the mover to push off (collide with) walls, not vice versa (consider bullets and lasers)
            if (otherCollider.BodyType != BodyType.Static ||
                !otherCollider.CanCollide ||
                (collider.CollisionMask & otherCollider.CollisionLayer) == 0 ||
                PullableQuery.TryComp(otherEntity, out var pullable) && pullable.BeingPulled)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    protected abstract bool CanSound();

    private bool TryGetSound(
        bool weightless,
        EntityUid uid,
        InputMoverComponent mover,
        MobMoverComponent mobMover,
        TransformComponent xform,
        [NotNullWhen(true)] out SoundSpecifier? sound,
        ContentTileDefinition? tileDef = null)
    {
        sound = null;

        if (!CanSound() || !_tags.HasTag(uid, FootstepSoundTag))
            return false;

        var coordinates = xform.Coordinates;
        var distanceNeeded = mover.Sprinting
            ? mobMover.StepSoundMoveDistanceRunning
            : mobMover.StepSoundMoveDistanceWalking;

        // Handle footsteps.
        if (!weightless)
        {
            // Can happen when teleporting between grids.
            if (!coordinates.TryDistance(EntityManager, mobMover.LastPosition, out var distance) ||
                distance > distanceNeeded)
            {
                mobMover.StepSoundDistance = distanceNeeded;
            }
            else
            {
                mobMover.StepSoundDistance += distance;
            }
        }
        else
        {
            // In space no one can hear you squeak
            return false;
        }

        mobMover.LastPosition = coordinates;

        if (mobMover.StepSoundDistance < distanceNeeded)
            return false;

        mobMover.StepSoundDistance -= distanceNeeded;

        if (FootstepModifierQuery.TryComp(uid, out var moverModifier))
        {
            sound = moverModifier.FootstepSoundCollection;
            return sound != null;
        }

        if (_inventory.TryGetSlotEntity(uid, "shoes", out var shoes) &&
            FootstepModifierQuery.TryComp(shoes, out var modifier))
        {
            sound = modifier.FootstepSoundCollection;
            return sound != null;
        }
        // ES START
        // Disable the foot sounds here by setting shoes to non null.
        if (ESIgnoreBarefootSoundsQuery.HasComp(uid))
            shoes = EntityUid.Invalid;
        // ES END

        return TryGetFootstepSound(uid, xform, shoes != null, out sound, tileDef: tileDef);
    }

    private bool TryGetFootstepSound(
        EntityUid uid,
        TransformComponent xform,
        bool haveShoes,
        [NotNullWhen(true)] out SoundSpecifier? sound,
        ContentTileDefinition? tileDef = null)
    {
        sound = null;

        // Fallback to the map?
        if (!MapGridQuery.TryComp(xform.GridUid, out var grid))
        {
            if (FootstepModifierQuery.TryComp(xform.MapUid, out var modifier))
            {
                sound = modifier.FootstepSoundCollection;
            }

            return sound != null;
        }

        var position = _mapSystem.LocalToTile(xform.GridUid.Value, grid, xform.Coordinates);
        var soundEv = new GetFootstepSoundEvent(uid);

        // If the coordinates have a FootstepModifier component
        // i.e. component that emit sound on footsteps emit that sound
        var anchored = _mapSystem.GetAnchoredEntitiesEnumerator(xform.GridUid.Value, grid, position);

        while (anchored.MoveNext(out var maybeFootstep))
        {
            RaiseLocalEvent(maybeFootstep.Value, ref soundEv);

            if (soundEv.Sound != null)
            {
                sound = soundEv.Sound;
                return true;
            }

            if (FootstepModifierQuery.TryComp(maybeFootstep, out var footstep))
            {
                sound = footstep.FootstepSoundCollection;
                return sound != null;
            }
        }

        // Walking on a tile.
        // Tile def might have been passed in already from previous methods, so use that
        // if we have it
        if (tileDef == null && _mapSystem.TryGetTileRef(xform.GridUid.Value, grid, position, out var tileRef))
        {
            tileDef = (ContentTileDefinition)_tileDefinitionManager[tileRef.Tile.TypeId];
        }

        if (tileDef == null)
            return false;

        sound = haveShoes ? tileDef.FootstepSounds : tileDef.BarestepSounds;
        return sound != null;
    }

    // ES START
    // forceWalk in args to pass to GetVelocityInput
    private Vector2 AssertValidWish(InputMoverComponent mover, float walkSpeed, float sprintSpeed, bool forceWalk=false)
    {
        var (walkDir, sprintDir) = GetVelocityInput(mover, forceWalk);
        // ES END
        var total = walkDir * walkSpeed + sprintDir * sprintSpeed;

        var parentRotation = GetParentGridAngle(mover);
        var wishDir = _relativeMovement ? parentRotation.RotateVec(total) : total;

        DebugTools.Assert(MathHelper.CloseToPercent(total.Length(), wishDir.Length()));

        return wishDir;
    }

    private void OnTileFriction(Entity<MovementSpeedModifierComponent> ent, ref TileFrictionEvent args)
    {
        if (!PhysicsQuery.TryComp(ent, out var physicsComponent))
            return;

        if (physicsComponent.BodyStatus != BodyStatus.OnGround || _gravity.IsWeightless(ent.Owner))
            args.Modifier *= ent.Comp.BaseWeightlessFriction;
        else
            args.Modifier *= ent.Comp.BaseFriction;
    }

    private void OnPhysicsBodyChanged(Entity<InputMoverComponent> entity, ref PhysicsBodyTypeChangedEvent args)
    {
        _blocker.UpdateCanMove(entity);
    }

    private void OnCanMove(Entity<InputMoverComponent> entity, ref UpdateCanMoveEvent args)
    {
        // If we don't have a physics component, or have a static body type then we can't move.
        if (!PhysicsQuery.TryComp(entity, out var body) || body.BodyType == BodyType.Static)
            args.Cancel();
    }
}
