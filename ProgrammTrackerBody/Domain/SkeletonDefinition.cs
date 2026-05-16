using System.Collections.Generic;
using System.Numerics;

namespace ProgrammTrackerBody.Domain;

public enum SkeletonBone
{
    Hip,
    Waist,
    Chest,
    UpperChest,
    Neck,
    Head,
    LeftClavicle,
    RightClavicle,
    LeftUpperArm,
    RightUpperArm,
    LeftLowerArm,
    RightLowerArm,
    LeftHand,
    RightHand,
    LeftHipPad,
    RightHipPad,
    LeftUpperLeg,
    RightUpperLeg,
    LeftLowerLeg,
    RightLowerLeg,
    LeftFoot,
    RightFoot,
}

// One bone in the skeleton tree.
//   Parent     — null only for the Hip root.
//   Controller — BodyPart whose tracker drives this bone's world rotation.
//                When null, the bone inherits its parent's world rotation
//                (so untracked limbs hang naturally instead of staying frozen at T-pose).
//   RestDir    — unit direction from parent joint to this bone's tail at I-pose.
//   LengthFraction — bone length as a fraction of total body height. 0 means the
//                    bone is a hub (Hip) with no rendered segment.
public sealed record BoneDefinition(
    SkeletonBone Bone,
    SkeletonBone? Parent,
    BodyPart? Controller,
    Vector3 RestDir,
    double LengthFraction);

public static class SkeletonDefinition
{
    // Hip joint anchor as a fraction of total body height (Y up).
    // Real human hip is at ~50% of height, so keep this as the rendering anchor.
    public const double HipHeightFraction = 0.50;

    private static readonly Vector3 FootDir = Vector3.Normalize(new Vector3(0f, -0.4f, 1f));

    // Bones in topological order: parent always precedes children for one-pass FK.
    // Length fractions are tuned to a normal adult skeleton:
    //   total spine + head segments = 0.50 (puts crown at y=1.0 from foot)
    //   hip-to-shoulder = 0.32 (waist 0.07 + chest 0.10 + upperChest 0.15)
    //   head segment alone = 0.13 (matches ~13% head-to-body ratio)
    //   arm = 0.18 + 0.16 + 0.09 = 0.43 (typical shoulder-to-finger ratio)
    //   leg = 0.25 + 0.23 = 0.48 (hip-to-ankle), foot keeps the figure above the floor
    public static readonly IReadOnlyList<BoneDefinition> Bones = new BoneDefinition[]
    {
        new(SkeletonBone.Hip,           null,                       BodyPart.Hip,           new Vector3(0,  1, 0), 0.00),
        new(SkeletonBone.Waist,         SkeletonBone.Hip,           BodyPart.Waist,         new Vector3(0,  1, 0), 0.07),
        new(SkeletonBone.Chest,         SkeletonBone.Waist,         BodyPart.Chest,         new Vector3(0,  1, 0), 0.10),
        new(SkeletonBone.UpperChest,    SkeletonBone.Chest,         BodyPart.UpperChest,    new Vector3(0,  1, 0), 0.15),
        new(SkeletonBone.Neck,          SkeletonBone.UpperChest,    BodyPart.Neck,          new Vector3(0,  1, 0), 0.05),
        new(SkeletonBone.Head,          SkeletonBone.Neck,          BodyPart.Head,          new Vector3(0,  1, 0), 0.13),
        new(SkeletonBone.LeftClavicle,  SkeletonBone.UpperChest,    null,                   new Vector3(-1, 0, 0), 0.13),
        new(SkeletonBone.RightClavicle, SkeletonBone.UpperChest,    null,                   new Vector3( 1, 0, 0), 0.13),
        new(SkeletonBone.LeftUpperArm,  SkeletonBone.LeftClavicle,  BodyPart.LeftUpperArm,  new Vector3(0, -1, 0), 0.18),
        new(SkeletonBone.RightUpperArm, SkeletonBone.RightClavicle, BodyPart.RightUpperArm, new Vector3(0, -1, 0), 0.18),
        new(SkeletonBone.LeftLowerArm,  SkeletonBone.LeftUpperArm,  BodyPart.LeftLowerArm,  new Vector3(0, -1, 0), 0.16),
        new(SkeletonBone.RightLowerArm, SkeletonBone.RightUpperArm, BodyPart.RightLowerArm, new Vector3(0, -1, 0), 0.16),
        new(SkeletonBone.LeftHand,      SkeletonBone.LeftLowerArm,  BodyPart.LeftHand,      new Vector3(0, -1, 0), 0.09),
        new(SkeletonBone.RightHand,     SkeletonBone.RightLowerArm, BodyPart.RightHand,     new Vector3(0, -1, 0), 0.09),
        new(SkeletonBone.LeftHipPad,    SkeletonBone.Hip,           null,                   new Vector3(-1, 0, 0), 0.10),
        new(SkeletonBone.RightHipPad,   SkeletonBone.Hip,           null,                   new Vector3( 1, 0, 0), 0.10),
        new(SkeletonBone.LeftUpperLeg,  SkeletonBone.LeftHipPad,    BodyPart.LeftUpperLeg,  new Vector3(0, -1, 0), 0.25),
        new(SkeletonBone.RightUpperLeg, SkeletonBone.RightHipPad,   BodyPart.RightUpperLeg, new Vector3(0, -1, 0), 0.25),
        new(SkeletonBone.LeftLowerLeg,  SkeletonBone.LeftUpperLeg,  BodyPart.LeftLowerLeg,  new Vector3(0, -1, 0), 0.23),
        new(SkeletonBone.RightLowerLeg, SkeletonBone.RightUpperLeg, BodyPart.RightLowerLeg, new Vector3(0, -1, 0), 0.23),
        new(SkeletonBone.LeftFoot,      SkeletonBone.LeftLowerLeg,  BodyPart.LeftFoot,      FootDir,               0.05),
        new(SkeletonBone.RightFoot,     SkeletonBone.RightLowerLeg, BodyPart.RightFoot,     FootDir,               0.05),
    };
}
