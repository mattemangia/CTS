// Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
namespace CTS.D3D11
{
    public static class MaterialExtensions
    {
        // Store opacity values separately if Material class doesn't have an opacity property
        private static System.Collections.Generic.Dictionary<Material, float> materialOpacities =
            new System.Collections.Generic.Dictionary<Material, float>();

        public static float GetOpacity(this Material material)
        {
            if (materialOpacities.TryGetValue(material, out float opacity))
                return opacity;

            // Default opacity
            return 1.0f;
        }

        public static void SetOpacity(this Material material, float opacity)
        {
            materialOpacities[material] = System.Math.Max(0.0f, System.Math.Min(1.0f, opacity));
        }

        // Convert material for GPU usage
        public static MaterialGPU ToGPU(this Material material)
        {
            float opacity = material.GetOpacity();

            // Use linear opacity mapping for predictable behavior
            // The shader will handle the actual blending

            return new MaterialGPU
            {
                Color = new System.Numerics.Vector4(
                    material.Color.R / 255.0f,
                    material.Color.G / 255.0f,
                    material.Color.B / 255.0f,
                    1.0f
                ),
                Settings = new System.Numerics.Vector4(
                    opacity,
                    material.IsVisible ? 1.0f : 0.0f,
                    material.Min,
                    material.Max
                )
            };
        }
    }
}