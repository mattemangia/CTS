using System;
using Vector4 = SharpDX.Vector4;

namespace CTSegmenter
{
    // Simple Matrix4x4 implementation for transformations in TransformDatasetForm
    public struct Matrix4x4
    {
        public float M11, M12, M13, M14;
        public float M21, M22, M23, M24;
        public float M31, M32, M33, M34;
        public float M41, M42, M43, M44;

        public static Matrix4x4 Identity => new Matrix4x4
        {
            M11 = 1,
            M12 = 0,
            M13 = 0,
            M14 = 0,
            M21 = 0,
            M22 = 1,
            M23 = 0,
            M24 = 0,
            M31 = 0,
            M32 = 0,
            M33 = 1,
            M34 = 0,
            M41 = 0,
            M42 = 0,
            M43 = 0,
            M44 = 1
        };

        public static Matrix4x4 CreateTranslation(float x, float y, float z)
        {
            Matrix4x4 result = Identity;
            result.M41 = x;
            result.M42 = y;
            result.M43 = z;
            return result;
        }

        public static Matrix4x4 CreateScale(float x, float y, float z)
        {
            Matrix4x4 result = Identity;
            result.M11 = x;
            result.M22 = y;
            result.M33 = z;
            return result;
        }

        public static Matrix4x4 CreateRotationX(float radians)
        {
            float cos = (float)Math.Cos(radians);
            float sin = (float)Math.Sin(radians);

            Matrix4x4 result = Identity;
            result.M22 = cos;
            result.M23 = sin;
            result.M32 = -sin;
            result.M33 = cos;
            return result;
        }

        public static Matrix4x4 CreateRotationY(float radians)
        {
            float cos = (float)Math.Cos(radians);
            float sin = (float)Math.Sin(radians);

            Matrix4x4 result = Identity;
            result.M11 = cos;
            result.M13 = -sin;
            result.M31 = sin;
            result.M33 = cos;
            return result;
        }

        public static Matrix4x4 CreateRotationZ(float radians)
        {
            float cos = (float)Math.Cos(radians);
            float sin = (float)Math.Sin(radians);

            Matrix4x4 result = Identity;
            result.M11 = cos;
            result.M12 = sin;
            result.M21 = -sin;
            result.M22 = cos;
            return result;
        }

        public static bool Invert(Matrix4x4 matrix, out Matrix4x4 result)
        {
            // Calculate the determinant
            float det = matrix.GetDeterminant();

            if (Math.Abs(det) < float.Epsilon)
            {
                result = Identity;
                return false;
            }

            float invDet = 1.0f / det;

            // Calculate the adjoint matrix
            result = new Matrix4x4();

            // First row
            result.M11 = invDet * (matrix.M22 * (matrix.M33 * matrix.M44 - matrix.M34 * matrix.M43) -
                                  matrix.M23 * (matrix.M32 * matrix.M44 - matrix.M34 * matrix.M42) +
                                  matrix.M24 * (matrix.M32 * matrix.M43 - matrix.M33 * matrix.M42));

            result.M12 = -invDet * (matrix.M12 * (matrix.M33 * matrix.M44 - matrix.M34 * matrix.M43) -
                                   matrix.M13 * (matrix.M32 * matrix.M44 - matrix.M34 * matrix.M42) +
                                   matrix.M14 * (matrix.M32 * matrix.M43 - matrix.M33 * matrix.M42));

            result.M13 = invDet * (matrix.M12 * (matrix.M23 * matrix.M44 - matrix.M24 * matrix.M43) -
                                  matrix.M13 * (matrix.M22 * matrix.M44 - matrix.M24 * matrix.M42) +
                                  matrix.M14 * (matrix.M22 * matrix.M43 - matrix.M23 * matrix.M42));

            result.M14 = -invDet * (matrix.M12 * (matrix.M23 * matrix.M34 - matrix.M24 * matrix.M33) -
                                   matrix.M13 * (matrix.M22 * matrix.M34 - matrix.M24 * matrix.M32) +
                                   matrix.M14 * (matrix.M22 * matrix.M33 - matrix.M23 * matrix.M32));

            // Second row
            result.M21 = -invDet * (matrix.M21 * (matrix.M33 * matrix.M44 - matrix.M34 * matrix.M43) -
                                   matrix.M23 * (matrix.M31 * matrix.M44 - matrix.M34 * matrix.M41) +
                                   matrix.M24 * (matrix.M31 * matrix.M43 - matrix.M33 * matrix.M41));

            result.M22 = invDet * (matrix.M11 * (matrix.M33 * matrix.M44 - matrix.M34 * matrix.M43) -
                                  matrix.M13 * (matrix.M31 * matrix.M44 - matrix.M34 * matrix.M41) +
                                  matrix.M14 * (matrix.M31 * matrix.M43 - matrix.M33 * matrix.M41));

            result.M23 = -invDet * (matrix.M11 * (matrix.M23 * matrix.M44 - matrix.M24 * matrix.M43) -
                                   matrix.M13 * (matrix.M21 * matrix.M44 - matrix.M24 * matrix.M41) +
                                   matrix.M14 * (matrix.M21 * matrix.M43 - matrix.M23 * matrix.M41));

            result.M24 = invDet * (matrix.M11 * (matrix.M23 * matrix.M34 - matrix.M24 * matrix.M33) -
                                  matrix.M13 * (matrix.M21 * matrix.M34 - matrix.M24 * matrix.M31) +
                                  matrix.M14 * (matrix.M21 * matrix.M33 - matrix.M23 * matrix.M31));

            // Third row
            result.M31 = invDet * (matrix.M21 * (matrix.M32 * matrix.M44 - matrix.M34 * matrix.M42) -
                                  matrix.M22 * (matrix.M31 * matrix.M44 - matrix.M34 * matrix.M41) +
                                  matrix.M24 * (matrix.M31 * matrix.M42 - matrix.M32 * matrix.M41));

            result.M32 = -invDet * (matrix.M11 * (matrix.M32 * matrix.M44 - matrix.M34 * matrix.M42) -
                                   matrix.M12 * (matrix.M31 * matrix.M44 - matrix.M34 * matrix.M41) +
                                   matrix.M14 * (matrix.M31 * matrix.M42 - matrix.M32 * matrix.M41));

            result.M33 = invDet * (matrix.M11 * (matrix.M22 * matrix.M44 - matrix.M24 * matrix.M42) -
                                  matrix.M12 * (matrix.M21 * matrix.M44 - matrix.M24 * matrix.M41) +
                                  matrix.M14 * (matrix.M21 * matrix.M42 - matrix.M22 * matrix.M41));

            result.M34 = -invDet * (matrix.M11 * (matrix.M22 * matrix.M34 - matrix.M24 * matrix.M32) -
                                   matrix.M12 * (matrix.M21 * matrix.M34 - matrix.M24 * matrix.M31) +
                                   matrix.M14 * (matrix.M21 * matrix.M32 - matrix.M22 * matrix.M31));

            // Fourth row
            result.M41 = -invDet * (matrix.M21 * (matrix.M32 * matrix.M43 - matrix.M33 * matrix.M42) -
                                   matrix.M22 * (matrix.M31 * matrix.M43 - matrix.M33 * matrix.M41) +
                                   matrix.M23 * (matrix.M31 * matrix.M42 - matrix.M32 * matrix.M41));

            result.M42 = invDet * (matrix.M11 * (matrix.M32 * matrix.M43 - matrix.M33 * matrix.M42) -
                                  matrix.M12 * (matrix.M31 * matrix.M43 - matrix.M33 * matrix.M41) +
                                  matrix.M13 * (matrix.M31 * matrix.M42 - matrix.M32 * matrix.M41));

            result.M43 = -invDet * (matrix.M11 * (matrix.M22 * matrix.M43 - matrix.M23 * matrix.M42) -
                                   matrix.M12 * (matrix.M21 * matrix.M43 - matrix.M23 * matrix.M41) +
                                   matrix.M13 * (matrix.M21 * matrix.M42 - matrix.M22 * matrix.M41));

            result.M44 = invDet * (matrix.M11 * (matrix.M22 * matrix.M33 - matrix.M23 * matrix.M32) -
                                  matrix.M12 * (matrix.M21 * matrix.M33 - matrix.M23 * matrix.M31) +
                                  matrix.M13 * (matrix.M21 * matrix.M32 - matrix.M22 * matrix.M31));

            return true;
        }

        private float GetDeterminant()
        {
            // Calculate determinant of 4x4 matrix
            float m11 = M11, m12 = M12, m13 = M13, m14 = M14;
            float m21 = M21, m22 = M22, m23 = M23, m24 = M24;
            float m31 = M31, m32 = M32, m33 = M33, m34 = M34;
            float m41 = M41, m42 = M42, m43 = M43, m44 = M44;

            float det1 = m11 * m22 - m12 * m21;
            float det2 = m11 * m23 - m13 * m21;
            float det3 = m11 * m24 - m14 * m21;
            float det4 = m12 * m23 - m13 * m22;
            float det5 = m12 * m24 - m14 * m22;
            float det6 = m13 * m24 - m14 * m23;
            float det7 = m31 * m42 - m32 * m41;
            float det8 = m31 * m43 - m33 * m41;
            float det9 = m31 * m44 - m34 * m41;
            float det10 = m32 * m43 - m33 * m42;
            float det11 = m32 * m44 - m34 * m42;
            float det12 = m33 * m44 - m34 * m43;

            return (det1 * det12 - det2 * det11 + det3 * det10 + det4 * det9 - det5 * det8 + det6 * det7);
        }

        public static Matrix4x4 operator *(Matrix4x4 a, Matrix4x4 b)
        {
            Matrix4x4 result = new Matrix4x4();

            // First row
            result.M11 = a.M11 * b.M11 + a.M12 * b.M21 + a.M13 * b.M31 + a.M14 * b.M41;
            result.M12 = a.M11 * b.M12 + a.M12 * b.M22 + a.M13 * b.M32 + a.M14 * b.M42;
            result.M13 = a.M11 * b.M13 + a.M12 * b.M23 + a.M13 * b.M33 + a.M14 * b.M43;
            result.M14 = a.M11 * b.M14 + a.M12 * b.M24 + a.M13 * b.M34 + a.M14 * b.M44;

            // Second row
            result.M21 = a.M21 * b.M11 + a.M22 * b.M21 + a.M23 * b.M31 + a.M24 * b.M41;
            result.M22 = a.M21 * b.M12 + a.M22 * b.M22 + a.M23 * b.M32 + a.M24 * b.M42;
            result.M23 = a.M21 * b.M13 + a.M22 * b.M23 + a.M23 * b.M33 + a.M24 * b.M43;
            result.M24 = a.M21 * b.M14 + a.M22 * b.M24 + a.M23 * b.M34 + a.M24 * b.M44;

            // Third row
            result.M31 = a.M31 * b.M11 + a.M32 * b.M21 + a.M33 * b.M31 + a.M34 * b.M41;
            result.M32 = a.M31 * b.M12 + a.M32 * b.M22 + a.M33 * b.M32 + a.M34 * b.M42;
            result.M33 = a.M31 * b.M13 + a.M32 * b.M23 + a.M33 * b.M33 + a.M34 * b.M43;
            result.M34 = a.M31 * b.M14 + a.M32 * b.M24 + a.M33 * b.M34 + a.M34 * b.M44;

            // Fourth row
            result.M41 = a.M41 * b.M11 + a.M42 * b.M21 + a.M43 * b.M31 + a.M44 * b.M41;
            result.M42 = a.M41 * b.M12 + a.M42 * b.M22 + a.M43 * b.M32 + a.M44 * b.M42;
            result.M43 = a.M41 * b.M13 + a.M42 * b.M23 + a.M43 * b.M33 + a.M44 * b.M43;
            result.M44 = a.M41 * b.M14 + a.M42 * b.M24 + a.M43 * b.M34 + a.M44 * b.M44;

            return result;
        }
    }

    public static class Vector4Extensions
    {
        public static Vector4 Transform(Vector4 vector, Matrix4x4 matrix)
        {
            return new Vector4(
                vector.X * matrix.M11 + vector.Y * matrix.M21 + vector.Z * matrix.M31 + vector.W * matrix.M41,
                vector.X * matrix.M12 + vector.Y * matrix.M22 + vector.Z * matrix.M32 + vector.W * matrix.M42,
                vector.X * matrix.M13 + vector.Y * matrix.M23 + vector.Z * matrix.M33 + vector.W * matrix.M43,
                vector.X * matrix.M14 + vector.Y * matrix.M24 + vector.Z * matrix.M34 + vector.W * matrix.M44
            );
        }
    }

    public static class Matrix4x4Extensions
    {
        // Convert between Matrix4x4 implementations
        public static SharpDX.Matrix ToSharpDXMatrix(this Matrix4x4 matrix)
        {
            return new SharpDX.Matrix(
                matrix.M11, matrix.M12, matrix.M13, matrix.M14,
                matrix.M21, matrix.M22, matrix.M23, matrix.M24,
                matrix.M31, matrix.M32, matrix.M33, matrix.M34,
                matrix.M41, matrix.M42, matrix.M43, matrix.M44
            );
        }

        // Transform SharpDX.Vector3 with our custom Matrix4x4
        public static SharpDX.Vector3 Transform(SharpDX.Vector3 position, Matrix4x4 matrix)
        {
            return new SharpDX.Vector3(
                position.X * matrix.M11 + position.Y * matrix.M21 + position.Z * matrix.M31 + matrix.M41,
                position.X * matrix.M12 + position.Y * matrix.M22 + position.Z * matrix.M32 + matrix.M42,
                position.X * matrix.M13 + position.Y * matrix.M23 + position.Z * matrix.M33 + matrix.M43
            );
        }
    }
}