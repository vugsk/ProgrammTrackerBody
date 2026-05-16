using System;
using System.Numerics;

namespace ProgrammTrackerBody.Domain;

public static class ResetMath
{
    // Compose order in ApplyOffsets:
    //   display = yawOffset * fullOffset * (mounting * raw * mounting⁻¹)
    //
    // The mounting term is a CHANGE OF BASIS (conjugation), not a left/right
    // multiplication. SlimeVR firmware (BNO085) reports rotation in the SENSOR'S
    // own start frame — its axes don't coincide with the body's axes when the
    // sensor is physically mounted at an angle. To express the same rotation in
    // body frame: R_body = M · R_sensor · M⁻¹, where M is the constant rotation
    // that takes sensor-frame axes onto body-frame axes.
    //
    // Why the previous attempts failed:
    //   - RIGHT-multiplication (display = raw * mount * full): only correct
    //     when M is identity; otherwise it rotates the MOTION AXES by facing,
    //     producing "lateral becomes frontal".
    //   - Pure LEFT (display = yaw * full * mount * raw): rotates the result
    //     in world frame but doesn't re-express the rotation's axis — the same
    //     axis-swap bug remains.
    //   - Only conjugation actually changes which axis a sensor rotation is
    //     interpreted around.
    //
    // Mounting is a DISCRETE Y-only rotation derived from the MountingOrientation
    // enum (Front/Back/Left/Right) — see ComputeMountingOffset below. It covers
    // the common case where the tracker is rotated by 0/90/180/-90° around the
    // user's vertical axis but otherwise lies flat against the body.
    //
    // Reference offsets (full, yaw) are computed and applied in BODY frame:
    //   fullOffset = Inverse(mounting * rawAtFullReset * mounting⁻¹)
    //   yawOffset  = CreateFromAxisAngle(Y, -ExtractYaw(full · mounting · raw · mounting⁻¹))

    public static Quaternion ApplyOffsets(
        Quaternion raw,
        Quaternion mountingOffset,
        Quaternion yawOffset,
        Quaternion fullOffset)
    {
        // Change of basis: re-express raw (in sensor frame) as a rotation in body frame.
        var m = Normalize(mountingOffset);
        var mInv = Quaternion.Conjugate(m); // unit quat → conjugate == inverse
        var bodyRot = m * Normalize(raw) * mInv;
        // Reference offsets now operate in body/world frame (LEFT-multiplied).
        bodyRot = fullOffset * bodyRot;
        bodyRot = yawOffset * bodyRot;
        return Normalize(bodyRot);
    }

    // Mounting offset is a DISCRETE rotation around world Y, derived from the
    // user's mounting choice (auto-detected via DetectMountingOrientation, or
    // selected manually from the dropdown). Reuses the enum's existing yaw
    // mapping in MountingOrientations.ToYawQuaternion.
    public static Quaternion ComputeMountingOffset(MountingOrientation orientation)
        => orientation.ToYawQuaternion();

    // Inspects the sensor's world yaw at rest to label the physical mounting
    // direction. Purely informational — purely so the user can confirm what
    // was detected. Doesn't affect ComputeMountingOffset's math.
    //
    // Yaw partitioning (sensor's +Z direction in world frame at rest):
    //   |yaw| < π/4    → +Z front-ish  → Front
    //    yaw  >  π/4   → +Z left-ish   → Left
    //    yaw  < -π/4   → +Z right-ish  → Right
    //   |yaw| > 3π/4   → +Z back-ish   → Back
    public static MountingOrientation DetectMountingOrientation(Quaternion currentRaw)
    {
        var yaw = ExtractYaw(currentRaw);
        if (yaw < -3f * MathF.PI / 4f || yaw > 3f * MathF.PI / 4f) return MountingOrientation.Back;
        if (yaw > MathF.PI / 4f) return MountingOrientation.Left;
        if (yaw < -MathF.PI / 4f) return MountingOrientation.Right;
        return MountingOrientation.Front;
    }

    // Snaps the *current body-frame rotation* to identity. Caller must compute
    // currentBodyRotation = mounting · raw · mounting⁻¹ before calling.
    public static Quaternion ComputeFullOffset(Quaternion currentBodyRotation)
    {
        return Quaternion.Inverse(Normalize(currentBodyRotation));
    }

    // Cancels world-frame yaw of the *current body-frame rotation*. Caller must
    // pass the body-frame rotation: full · (mounting · raw · mounting⁻¹).
    public static Quaternion ComputeYawOffset(Quaternion currentBodyRotation)
    {
        var yaw = ExtractYaw(currentBodyRotation);
        return Quaternion.CreateFromAxisAngle(Vector3.UnitY, -yaw);
    }

    // Yaw is rotation around world Y. Extracted via the standard ZYX Euler
    // decomposition formula.
    public static float ExtractYaw(Quaternion q)
    {
        var n = Normalize(q);
        var siny_cosp = 2f * (n.W * n.Y + n.X * n.Z);
        var cosy_cosp = 1f - 2f * (n.Y * n.Y + n.X * n.X);
        return MathF.Atan2(siny_cosp, cosy_cosp);
    }

    public static Quaternion Normalize(Quaternion q)
    {
        var len = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
        if (len < 1e-6f)
        {
            return Quaternion.Identity;
        }

        var inv = 1f / len;
        return new Quaternion(q.X * inv, q.Y * inv, q.Z * inv, q.W * inv);
    }
}
