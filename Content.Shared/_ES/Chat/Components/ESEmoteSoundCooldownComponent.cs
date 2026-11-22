namespace Content.Shared._ES.Chat.Components;

[RegisterComponent]
[Access(typeof(ESEmoteSystem))]
public sealed partial class ESEmoteSoundCooldownComponent : Component
{
    public TimeSpan NextSoundTime;
}
