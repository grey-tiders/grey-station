using System.Numerics;
using Robust.Shared.GameStates;
using Robust.Shared.Map;

namespace Content.Shared.Movement.Components;

/// <summary>
/// Makes grounded movement advance one tile at a time through the shared mover controller.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class TileMovementComponent : Component
{
    /// <summary>Whether the entity is currently moving toward a tile center.</summary>
    [AutoNetworkedField]
    public bool SlideActive;

    /// <summary>Destination in the local coordinate frame of the entity's parent.</summary>
    [AutoNetworkedField]
    public EntityCoordinates Destination;

    /// <summary>Position at the previous mover tick, used to detect a blocked step.</summary>
    [AutoNetworkedField]
    public Vector2 LastTickPosition;

    /// <summary>Consecutive mover ticks with no progress toward the destination.</summary>
    [AutoNetworkedField]
    public int NoProgressTicks;

    /// <summary>Direction that was blocked by collision; same-direction input won't restart the step.</summary>
    [AutoNetworkedField]
    public Vector2 BlockedDirection;
}
