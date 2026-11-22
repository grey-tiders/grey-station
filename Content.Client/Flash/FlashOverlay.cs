using Content.Shared.CCVar;
using Content.Shared.Flash;
using Content.Shared.Flash.Components;
using Content.Shared.StatusEffect;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Client.Flash
{
    public sealed partial class FlashOverlay : Overlay
    {
        private static readonly ProtoId<ShaderPrototype> FlashedEffectShader = "FlashedEffect";

        [Dependency] private IPrototypeManager _prototypeManager = default!;
        [Dependency] private IEntityManager _entityManager = default!;
        [Dependency] private IPlayerManager _playerManager = default!;
        [Dependency] private IGameTiming _timing = default!;
        [Dependency] private IConfigurationManager _configManager = default!;

        private readonly SharedFlashSystem _flash;
        private readonly StatusEffectsSystem _statusSys;

        public override OverlaySpace Space => OverlaySpace.WorldSpace;
        private readonly ShaderInstance _shader;
        private bool _reducedMotion;
        private bool _useFade;
        public float PercentComplete;
        public Texture? ScreenshotTexture;

        public FlashOverlay()
        {
            IoCManager.InjectDependencies(this);
            ZIndex = 2; // Let the flash cover the tile FoV mask and damage vignette.
            _shader = _prototypeManager.Index(FlashedEffectShader).InstanceUnique();
            _flash = _entityManager.System<SharedFlashSystem>();
            _statusSys = _entityManager.System<StatusEffectsSystem>();

            _configManager.OnValueChanged(CCVars.ReducedMotion, (b) => { _reducedMotion = b; }, invokeImmediately: true);
        }

        protected override void FrameUpdate(FrameEventArgs args)
        {
            var playerEntity = _playerManager.LocalEntity;

            if (playerEntity == null)
                return;

            if (!_entityManager.HasComponent<FlashedComponent>(playerEntity)
                || !_entityManager.TryGetComponent<StatusEffectsComponent>(playerEntity, out var status))
                return;

            if (!_statusSys.TryGetTime(playerEntity.Value, _flash.FlashedKey, out var time, status))
                return;

            var curTime = _timing.CurTime;
            var lastsFor = (float)(time.Value.Item2 - time.Value.Item1).TotalSeconds;
            var timeDone = (float)(curTime - time.Value.Item1).TotalSeconds;

            PercentComplete = timeDone / lastsFor;
        }

        protected override bool BeforeDraw(in OverlayDrawArgs args)
        {
            if (!_entityManager.TryGetComponent(_playerManager.LocalEntity, out EyeComponent? eyeComp))
                return false;
            if (args.Viewport.Eye != eyeComp.Eye)
                return false;

            if (PercentComplete >= 1.0f)
                return false;

            // Keep the fade when stepping off-grid, where the tile mask is temporarily inactive.
            _useFade = _reducedMotion || _configManager.GetCVar(CCVars.TileVisionOverlay);

            if (_useFade)
                ScreenshotTexture = null;

            RequestScreenTexture = !_useFade && ScreenshotTexture == null;
            return true;
        }

        protected override void Draw(in OverlayDrawArgs args)
        {
            var worldHandle = args.WorldHandle;
            if (_useFade)
            {
                var progress = _reducedMotion ? PercentComplete : MathF.Min(PercentComplete * 2f, 1f);
                var alpha = 1 - MathF.Pow(progress, 8f);
                var color = _reducedMotion ? Color.Black : Color.White;
                worldHandle.DrawRect(args.WorldBounds, color.WithAlpha(alpha));
                return;
            }

            if (RequestScreenTexture && ScreenTexture != null)
            {
                ScreenshotTexture = ScreenTexture;
                RequestScreenTexture = false; // we only need the first frame, so we can stop the request now for performance reasons
            }
            if (ScreenshotTexture == null)
                return;

            _shader.SetParameter("percentComplete", PercentComplete);
            worldHandle.UseShader(_shader);
            worldHandle.DrawTextureRectRegion(ScreenshotTexture, args.WorldBounds);
            worldHandle.UseShader(null);
        }

        protected override void DisposeBehavior()
        {
            base.DisposeBehavior();
            ScreenshotTexture = null;
        }
    }
}
