using System.Collections.Generic;

namespace ProgrammTrackerBody.Domain;

public enum BodyPart : byte
{
    None = 0,
    Head = 1,
    Neck = 2,
    UpperChest = 3,
    Chest = 4,
    Waist = 5,
    Hip = 6,
    LeftUpperLeg = 7,
    RightUpperLeg = 8,
    LeftLowerLeg = 9,
    RightLowerLeg = 10,
    LeftFoot = 11,
    RightFoot = 12,
    LeftUpperArm = 13,
    RightUpperArm = 14,
    LeftLowerArm = 15,
    RightLowerArm = 16,
    LeftHand = 17,
    RightHand = 18,
}

public static class BodyParts
{
    public static IReadOnlyList<BodyPart> All { get; } = new[]
    {
        BodyPart.None,
        BodyPart.Head,
        BodyPart.Neck,
        BodyPart.UpperChest,
        BodyPart.Chest,
        BodyPart.Waist,
        BodyPart.Hip,
        BodyPart.LeftUpperLeg,
        BodyPart.RightUpperLeg,
        BodyPart.LeftLowerLeg,
        BodyPart.RightLowerLeg,
        BodyPart.LeftFoot,
        BodyPart.RightFoot,
        BodyPart.LeftUpperArm,
        BodyPart.RightUpperArm,
        BodyPart.LeftLowerArm,
        BodyPart.RightLowerArm,
        BodyPart.LeftHand,
        BodyPart.RightHand,
    };

    // Resource key used in Strings.*.xaml — the LocalizationService resolves it.
    public static string GetResourceKey(this BodyPart part) => $"BodyPart.{part}";
}
