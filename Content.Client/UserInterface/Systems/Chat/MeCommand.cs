using Content.Client._ES.Chat;
using Content.Shared._ES.Chat;
using Robust.Client.Player;
using Robust.Shared.Console;

namespace Content.Client.UserInterface.Systems.Chat;

public sealed partial class MeCommand : LocalizedCommands
{
    [Dependency] private IESChatManager _chat = default!;
    [Dependency] private IPlayerManager _player = default!;

    public override string Command => "me";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var message = string.Join(" ", args).Trim();
        if (message.Length == 0)
        {
            shell.WriteLine(Help);
            return;
        }

        if (_player.LocalSession?.AttachedEntity == null)
        {
            shell.WriteError(Loc.GetString("shell-must-be-attached-to-entity"));
            return;
        }

        _chat.RequestSendChatMessage(message, ESSharedChatSystem.EmoteChannel);
    }
}
