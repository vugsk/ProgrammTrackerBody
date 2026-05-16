using System.Collections.Generic;
using System.Numerics;

namespace ProgrammTrackerBody.Domain;

// Result of a single FK pass over the skeleton.
//   HeadPositions[bone] — joint where the bone starts (parent's tip).
//   TipPositions[bone]  — joint where the bone ends.
//   WorldRots[bone]     — bone's world rotation, used to orient its visual segment.
public sealed class SkeletonPose
{
    public Dictionary<SkeletonBone, Vector3> HeadPositions { get; } = new();
    public Dictionary<SkeletonBone, Vector3> TipPositions { get; } = new();
    public Dictionary<SkeletonBone, Quaternion> WorldRots { get; } = new();

    // Walk Bones in topological order; parent precedes children, so a single pass
    // is enough. Each bone's world rotation is its tracker's rotation when assigned,
    // otherwise the parent's world rotation (inherit). Tip is computed from head
    // by rotating the rest direction and scaling to bone length.
    public static SkeletonPose Compute(
        IReadOnlyDictionary<BodyPart, Quaternion> rotations,
        double heightMeters)
    {
        var pose = new SkeletonPose();
        var h = (float)heightMeters;
        var hipAnchor = new Vector3(0f, (float)(SkeletonDefinition.HipHeightFraction * heightMeters), 0f);

        foreach (var bone in SkeletonDefinition.Bones)
        {
            // 1) world rotation: tracker if assigned, else inherit from parent, else identity (root fallback).
            Quaternion worldRot;
            if (bone.Controller is { } ctrl && rotations.TryGetValue(ctrl, out var trackerRot))
            {
                worldRot = trackerRot;
            }
            else if (bone.Parent is { } parent && pose.WorldRots.TryGetValue(parent, out var parentRot))
            {
                worldRot = parentRot;
            }
            else
            {
                worldRot = Quaternion.Identity;
            }
            pose.WorldRots[bone.Bone] = worldRot;

            // 2) head: parent's tip, or the hip anchor for the root.
            Vector3 head = bone.Parent is { } p && pose.TipPositions.TryGetValue(p, out var parentTip)
                ? parentTip
                : hipAnchor;
            pose.HeadPositions[bone.Bone] = head;

            // 3) tip: head + length * (worldRot * restDir).
            float length = (float)(bone.LengthFraction * heightMeters);
            Vector3 tip = head + Vector3.Transform(bone.RestDir, worldRot) * length;
            pose.TipPositions[bone.Bone] = tip;
        }

        return pose;
    }
}
