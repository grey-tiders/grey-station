using System.Linq;
using System.Numerics;
using Content.Shared._ES.Viewcone;
using Content.Shared.Administration.Logs;
using Content.Shared.Database;
using Content.Shared.Gravity;
using Content.Shared.Physics;
using Content.Shared.Movement.Pulling.Events;
using Robust.Shared.Network;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Shared.Throwing
{
    /// <summary>
    ///     Handles throwing landing and collisions.
    /// </summary>
    public sealed partial class ThrownItemSystem : EntitySystem
    {
        [Dependency] private IGameTiming _gameTiming = default!;
        [Dependency] private INetManager _netMan = default!;
        [Dependency] private ISharedAdminLogManager _adminLogger = default!;
        [Dependency] private FixtureSystem _fixtures = default!;
        [Dependency] private SharedBroadphaseSystem _broadphase = default!;
        [Dependency] private SharedPhysicsSystem _physics = default!;
        [Dependency] private SharedGravitySystem _gravity = default!;
        // ES START
        [Dependency] private SharedAppearanceSystem _appearance = default!;
        [Dependency] private SharedTransformSystem _transform = default!;
        [Dependency] private ESViewconeEffectSystem _effect = default!;

        public static EntProtoId LandViewconeEffect = "ESViewconeEffectAttack";
        // ES END

        private const string ThrowingFixture = "throw-fixture";

        public override void Initialize()
        {
            base.Initialize();
            SubscribeLocalEvent<ThrownItemComponent, MapInitEvent>(OnMapInit);
            SubscribeLocalEvent<ThrownItemComponent, PhysicsSleepEvent>(OnSleep);
            SubscribeLocalEvent<ThrownItemComponent, StartCollideEvent>(HandleCollision);
            SubscribeLocalEvent<ThrownItemComponent, PreventCollideEvent>(PreventCollision);
            SubscribeLocalEvent<ThrownItemComponent, ThrownEvent>(ThrowItem);

            SubscribeLocalEvent<PullStartedMessage>(HandlePullStarted);
        }

        private void OnMapInit(EntityUid uid, ThrownItemComponent component, MapInitEvent args)
        {
            component.ThrownTime ??= _gameTiming.CurTime;
        }

        private void ThrowItem(EntityUid uid, ThrownItemComponent component, ref ThrownEvent @event)
        {
            if (!TryComp(uid, out FixturesComponent? fixturesComponent) ||
                fixturesComponent.Fixtures.Count != 1 ||
                !TryComp<PhysicsComponent>(uid, out var body))
            {
                return;
            }

            var fixture = fixturesComponent.Fixtures.Values.First();
            var shape = fixture.Shape;
            _fixtures.TryCreateFixture(uid, shape, ThrowingFixture, hard: false, collisionMask: (int) CollisionGroup.ThrownItem, manager: fixturesComponent, body: body);
        }

        private void HandleCollision(EntityUid uid, ThrownItemComponent component, ref StartCollideEvent args)
        {
            if (!args.OtherFixture.Hard)
                return;

            if (args.OtherEntity == component.Thrower)
                return;

            ThrowCollideInteraction(component, args.OurEntity, args.OtherEntity);
        }

        private void PreventCollision(EntityUid uid, ThrownItemComponent component, ref PreventCollideEvent args)
        {
            if (args.OtherEntity == component.Thrower)
            {
                args.Cancelled = true;
            }
        }

        private void OnSleep(EntityUid uid, ThrownItemComponent thrownItem, ref PhysicsSleepEvent @event)
        {
            // Contact callbacks can run while the physics solver is removing a
            // contact. Defer landing and contact regeneration until the next update.
            thrownItem.PendingLand = true;
        }

        private void HandlePullStarted(PullStartedMessage message)
        {
            // TODO: this isn't directed so things have to be done the bad way
            if (TryComp(message.PulledUid, out ThrownItemComponent? thrownItemComponent))
                StopThrow(message.PulledUid, thrownItemComponent);
        }

        public void StopThrow(EntityUid uid, ThrownItemComponent thrownItemComponent)
        {
            _appearance.SetData(uid, ESThrowVisuals.InAir, false);

            if (TryComp<PhysicsComponent>(uid, out var physics))
            {
                _physics.SetBodyStatus(uid, physics, BodyStatus.OnGround);

                if (physics.Awake)
                    _broadphase.RegenerateContacts((uid, physics));
            }

            if (TryComp(uid, out FixturesComponent? manager))
            {
                var fixture = _fixtures.GetFixtureOrNull(uid, ThrowingFixture, manager: manager);

                if (fixture != null)
                {
                    _fixtures.DestroyFixture(uid, ThrowingFixture, fixture, manager: manager);
                }
            }

            var ev = new StopThrowEvent(thrownItemComponent.Thrower);
            RaiseLocalEvent(uid, ref ev);
            RemComp<ThrownItemComponent>(uid);
        }

        public void LandComponent(EntityUid uid, ThrownItemComponent thrownItem, PhysicsComponent physics, bool playSound)
        {
            if (thrownItem.Landed || thrownItem.Deleted || _gravity.IsWeightless(uid) || Deleted(uid))
                return;

            thrownItem.Landed = true;

            // Assume it's uninteresting if it has no thrower. For now anyway.
            if (thrownItem.Thrower is not null)
                _adminLogger.Add(LogType.Landed, LogImpact.Low, $"{ToPrettyString(uid):entity} thrown by {ToPrettyString(thrownItem.Thrower.Value):thrower} landed.");

            // ES START
            _transform.SetLocalRotation(uid, Angle.Zero);
            // Stop residual floor sliding before centering; otherwise the physics
            // solver moves the item away from the tile center later in this tick.
            _physics.SetLinearVelocity(uid, Vector2.Zero, body: physics);
            _physics.SetAngularVelocity(uid, 0f, body: physics);
            CenterOnLandingTile(uid);

            // play effect if there was a thrower
            if (thrownItem.Thrower != null)
                _effect.SpawnEffect(uid, LandViewconeEffect);
            // ES END

            _broadphase.RegenerateContacts((uid, physics));
            var landEvent = new LandEvent(thrownItem.Thrower, playSound);
            RaiseLocalEvent(uid, ref landEvent);
        }

        private void CenterOnLandingTile(EntityUid uid)
        {
            var xform = Transform(uid);
            if (xform.GridUid is not { } gridUid || xform.ParentUid != gridUid ||
                !TryComp<MapGridComponent>(gridUid, out var grid))
                return;

            var tileSize = grid.TileSize;
            var local = xform.LocalPosition;
            var centered = new Vector2(
                (MathF.Floor(local.X / tileSize) + 0.5f) * tileSize,
                (MathF.Floor(local.Y / tileSize) + 0.5f) * tileSize);
            _transform.SetLocalPosition(uid, centered, xform);
        }

        /// <summary>
        ///     Raises collision events on the thrown and target entities.
        /// </summary>
        public void ThrowCollideInteraction(ThrownItemComponent component, EntityUid thrown, EntityUid target)
        {
            if (component.Thrower is not null)
                _adminLogger.Add(LogType.ThrowHit, LogImpact.Low,
                    $"{ToPrettyString(thrown):thrown} thrown by {ToPrettyString(component.Thrower.Value):thrower} hit {ToPrettyString(target):target}.");

            var hitByEv = new ThrowHitByEvent(thrown, target, component);
            var doHitEv = new ThrowDoHitEvent(thrown, target, component);
            RaiseLocalEvent(target, ref hitByEv, true);
            RaiseLocalEvent(thrown, ref doHitEv, true);
        }

        public override void Update(float frameTime)
        {
            base.Update(frameTime);

            var query = EntityQueryEnumerator<ThrownItemComponent, PhysicsComponent>();
            while (query.MoveNext(out var uid, out var thrown, out var physics))
            {
                // If you remove this check verify slipping for other entities is networked properly.
                if (_netMan.IsClient && !physics.Predict)
                    continue;

                if (thrown.PendingLand)
                {
                    thrown.PendingLand = false;
                    LandComponent(uid, thrown, physics, playSound: false);
                    StopThrow(uid, thrown);
                    continue;
                }

                if (thrown.LandTime <= _gameTiming.CurTime)
                {
                    LandComponent(uid, thrown, physics, thrown.PlayLandSound);
                }

                var stopThrowTime = thrown.LandTime ?? thrown.ThrownTime;
                if (stopThrowTime <= _gameTiming.CurTime)
                {
                    StopThrow(uid, thrown);
                }
            }
        }
    }
}
