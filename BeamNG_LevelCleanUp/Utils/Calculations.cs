using System.Numerics;

namespace BeamNG_LevelCleanUp.Utils;

public static class Calculations
{
    public static decimal[] GetRotationMatrix(float x, float y, float z)
    {
        // Euler angles in radians
        var roll = x;
        var pitch = y;
        var yaw = z;

        // Calculate the rotation matrices for each axis
        var Rx = Matrix4x4.CreateRotationX(roll);
        var Ry = Matrix4x4.CreateRotationY(pitch);
        var Rz = Matrix4x4.CreateRotationZ(yaw);

        // Calculate the combined rotation matrix
        var R = Rz * Ry * Rx;
        // Extract a 3x3 rotation matrix as an array of decimals
        var rotationArray = new decimal[9];
        for (var row = 0; row < 3; row++)
        for (var col = 0; col < 3; col++)
            rotationArray[row * 3 + col] = (decimal)R[row, col];

        return rotationArray;
    }
}