using System.Numerics;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests.Movement;

public sealed class DiagonalMovementTest : MovementTest
{
    [TestCase(1, 0)]
    [TestCase(0, 1)]
    public async Task SolidCornerBlocksDiagonalStep(int obstacleX, int obstacleY)
    {
        var start = SEntMan.GetCoordinates(PlayerCoords);
        var north = start.Offset(Vector2.UnitY);
        var destination = start.Offset(Vector2.One);
        await SetTile(Plating, SEntMan.GetNetCoordinates(north), MapData.Grid);
        await SetTile(Plating, SEntMan.GetNetCoordinates(destination), MapData.Grid);
        var obstacle = await SpawnEntity("WallSolid", start.Offset(new Vector2(obstacleX, obstacleY)));

        var initialPosition = Transform.GetWorldPosition(SPlayer);
        await Move(DirectionFlag.NorthEast, 0.5f);
        Assert.That(Vector2.Distance(Transform.GetWorldPosition(SPlayer), initialPosition), Is.LessThan(0.01f));

        await Delete(obstacle);
        await Move(DirectionFlag.NorthEast, 1f);
        Assert.That(Vector2.Distance(Transform.GetWorldPosition(SPlayer), initialPosition + Vector2.One),
            Is.LessThan(0.01f));
    }
}
