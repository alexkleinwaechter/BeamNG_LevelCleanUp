using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Utils
{
    public static class Calculations
    {
        public static decimal[] GetRotationMatrix(float x, float y, float z)
        {
            // Euler angles in radians
            float roll = x;
            float pitch = y;
            float yaw = z;

            // Calculate the rotation matrices for each axis
            Matrix4x4 Rx = Matrix4x4.CreateRotationX(roll);
            Matrix4x4 Ry = Matrix4x4.CreateRotationY(pitch);
            Matrix4x4 Rz = Matrix4x4.CreateRotationZ(yaw);

            // Calculate the combined rotation matrix
            Matrix4x4 R = Rz * Ry * Rx;
            // Extract a 3x3 rotation matrix as an array of decimals
            decimal[] rotationArray = new decimal[9];
            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 3; col++)
                {
                    rotationArray[row * 3 + col] = (decimal)R[row, col];
                }
            }

            return rotationArray;
        }
    }
}
