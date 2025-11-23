using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Grille.BeamNG.Numerics;

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 36)]
public struct RotationMatrix3x3
{
    public Vector3 X;
    public Vector3 Y;
    public Vector3 Z;

    public RotationMatrix3x3(ReadOnlySpan<float> values)
    {
        if (values.Length != 9)
            throw new ArgumentException();

        Unsafe.SkipInit(out X);
        Unsafe.SkipInit(out Y);
        Unsafe.SkipInit(out Z);

        values.CopyTo(AsSpan());
    }

    public RotationMatrix3x3(Vector3 euler)
    {
        Unsafe.SkipInit(out X);
        Unsafe.SkipInit(out Y);
        Unsafe.SkipInit(out Z);

        ApplyEuler(euler);
    }

    public void CopyTo(Span<float> dst)
    {
        AsSpan().CopyTo(dst);
    }

    public unsafe Span<float> AsSpan()
    {
        return MemoryMarshal.Cast<Vector3, float>(MemoryMarshal.CreateSpan(ref X, 3));
    }

    public Vector3 ToEuler()
    {
        // Row-major interpretation
        float m00 = X.X, m01 = X.Y, m02 = X.Z;
        float m10 = Y.X, m11 = Y.Y, m12 = Y.Z;
        float m20 = Z.X, m21 = Z.Y, m22 = Z.Z;

        float x_pitch, y_roll, z_roll;

        if (MathF.Abs(m20) < 1.0f)
        {
            y_roll = -MathF.Asin(m20);
            x_pitch = MathF.Atan2(m21, m22);
            z_roll = MathF.Atan2(m10, m00);
        }
        else
        {
            // Gimbal lock
            const float HalfPI = MathF.PI / 2f;
            y_roll = m20 < 0 ? HalfPI : -HalfPI;
            x_pitch = MathF.Atan2(-m01, m11);
            z_roll = 0;
        }

        return new Vector3( x_pitch,  y_roll, z_roll);
    }

    public void ApplyEuler(Vector3 euler)
    {
        // Convert degrees to radians
        float x_pitch = euler.X; // Pitch
        float y_roll = euler.Y; // Yaw
        float z_yaw = euler.Z; // Roll

        float cx = MathF.Cos(x_pitch), sx = MathF.Sin(x_pitch);
        float cy = MathF.Cos(y_roll), sy = MathF.Sin(y_roll);
        float cz = MathF.Cos(z_yaw), sz = MathF.Sin(z_yaw);

        // Rotation matrix in ZYX order:
        // R = Rz * Ry * Rx
        float m00 = cz * cy;
        float m01 = cz * sy * sx - sz * cx;
        float m02 = cz * sy * cx + sz * sx;

        float m10 = sz * cy;
        float m11 = sz * sy * sx + cz * cx;
        float m12 = sz * sy * cx - cz * sx;

        float m20 = -sy;
        float m21 = cy * sx;
        float m22 = cy * cx;

        X.X = m00; X.Y = m01; X.Z = m02;
        Y.X = m10; Y.Y = m11; Y.Z = m12;
        Z.X = m20; Z.Y = m21; Z.Z = m22;
    }
}
