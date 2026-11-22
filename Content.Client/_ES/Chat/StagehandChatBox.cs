using Content.Client.UserInterface.RichText;
using Content.Client.UserInterface.Systems.Chat.Widgets;
using Content.Shared._ES.Chat;
using Content.Shared.Chat;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Utility;

namespace Content.Client._ES.Chat;

/// <summary>
///     A chat box with two separate output panels for deadchat messages and all other chat messages.
/// </summary>
public sealed partial class StagehandChatBox : ChatBox, IEntityLinkClickHandler
{
    public PanelContainer StagehandChatWindowPanel;
    public OutputPanel StagehandContents;

    public StagehandChatBox()
    {
        Orientation = LayoutOrientation.Vertical;

        StagehandContents = new OutputPanel()
        {
            HorizontalExpand = true,
            VerticalExpand = true,
            Margin = new(8, 8, 8, 4),
            ShowScrollDownButton = true,
        };

        // this should be done with xaml but uhh i was having extremely strange issues with that
        StagehandChatWindowPanel = new PanelContainer()
        {
            HorizontalExpand = true,
            VerticalExpand = true,
            SizeFlagsStretchRatio = 0.7f,
            Margin = new(0,0,0, 10)
        };
        StagehandChatWindowPanel.AddStyleClass("StyleNano.StyleClassChatPanel");
        AddChild(StagehandChatWindowPanel);
        StagehandChatWindowPanel.SetPositionFirst();
        StagehandChatWindowPanel.AddChild(StagehandContents);
    }

    protected override void OnMessageAdded(ESChatMessage msg)
    {
        Sawmill.Debug($"{msg.Channel}: {msg.Content}");
        if (!VisibleInTree)
            return;

        if (!ChatInput.FilterButton.Popup.IsActive(msg.Channel))
        {
            return;
        }

        msg.Read = true;

        var channel = PrototypeManager.Index(msg.Channel);
        switch (channel.ChatBoxLocation)
        {
            case ESChatBoxLocation.Primary:
                AddChatMessage(msg, channel);
                break;
            case ESChatBoxLocation.Stagehand:
                AddStagehandLine(msg.FormattedMessage, msg.Color);
                break;
            default:
                Log.Error($"Unknown box location: {channel.ChatBoxLocation}");
                break;
        }

    }

    public override void Repopulate()
    {
        StagehandContents.Clear();
        base.Repopulate();
    }

    public void AddStagehandLine(string message, Color color)
    {
        var formatted = new FormattedMessage(3);
        formatted.PushColor(color);
        formatted.AddMarkupOrThrow(message);
        formatted.Pop();
        StagehandContents.AddMessage(formatted, tagsAllowed: null);
    }

    /// <summary>
    /// Sends a message to the server when this chat message is clicked.
    /// </summary>
    /// <param name="netEntity">Target entity of our event</param>
    public void HandleClick(NetEntity netEntity)
    {
        EntManager.RaisePredictiveEvent(new ChatLinkClickedRequestEvent(netEntity));
    }
}
