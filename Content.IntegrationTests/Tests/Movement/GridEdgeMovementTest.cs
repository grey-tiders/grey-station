using Robust.Shared.GameObjects;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests.Movement;

public sealed class GridEdgeMovementTest : MovementTest
{
    [TestCase(DirectionFlag.North)]
    [TestCase(DirectionFlag.NorthEast)]
    public async Task StepOffGridIntoSpace(DirectionFlag direction)
    {
        var initialY = Transform.GetWorldPosition(SPlayer).Y;
        await Move(direction, 0.1f);
        await RunSeconds(0.5f);

        Assert.That(Comp<TransformComponent>(Player).GridUid, Is.Null);
        var departureY = Transform.GetWorldPosition(SPlayer).Y;
        Assert.That(departureY, Is.GreaterThan(initialY + 0.5f));

        await RunSeconds(0.5f);
        Assert.That(Transform.GetWorldPosition(SPlayer).Y, Is.GreaterThan(departureY));
    }
}
