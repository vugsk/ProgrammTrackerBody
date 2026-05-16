using System;
using System.Collections.Generic;
using System.Numerics;

namespace ProgrammTrackerBody.Domain;

public enum MountingOrientation : byte
{
    Front = 0,
    Back = 1,
    Left = 2,
    Right = 3,
}

public static class MountingOrientations
{
    public static IReadOnlyList<MountingOrientation> All { get; } = new[]
    {
        MountingOrientation.Front,
        MountingOrientation.Back,
        MountingOrientation.Left,
        MountingOrientation.Right,
    };

    public static string GetResourceKey(this MountingOrientation o) => $"Mounting.{o}";

    // Yaw rotation (around world Y) that brings the sensor's "forward" axis
    // back to the user's "forward" direction. Used as the base of mounting reset.
    public static Quaternion ToYawQuaternion(this MountingOrientation o)
    {
        var radians = o switch
        {
            MountingOrientation.Front => 0f,
            MountingOrientation.Back => MathF.PI,
            MountingOrientation.Left => MathF.PI / 2f,
            MountingOrientation.Right => -MathF.PI / 2f,
            _ => 0f,
        };

        return Quaternion.CreateFromAxisAngle(Vector3.UnitY, radians);
    }
}
