//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D11;
using Buffer = SharpDX.Direct3D11.Buffer;
using System.Threading;

namespace CTS.Modules.Simulation.NMR
{
    /// <summary>
    /// GPU-accelerated NMR simulation using DirectCompute
    /// </summary>
    public class NMRGPUDirectCompute : IDisposable
    {
        private Device _device;
        private DeviceContext _context;
        private ComputeShader _computeShader;
        private bool _disposed = false;

        // Shader constants
        private const string COMPUTE_SHADER_SOURCE = @"
        #pragma kernel CS_ComputeDecay
        
        // Input buffers
        StructuredBuffer<float> g_T2Values;
        StructuredBuffer<float> g_Amplitudes;
        StructuredBuffer<float> g_TimePoints;
        
        // Output buffer
        RWStructuredBuffer<float> g_Magnetization;
        
        // Constants
        cbuffer Constants : register(b0)
        {
            uint g_numComponents;
            uint g_numTimePoints;
            uint g_padding1;
            uint g_padding2;
        };
        
        groupshared float s_componentData[1024]; // Shared memory for component data
        
        [numthreads(256, 1, 1)]
        void CS_ComputeDecay(uint3 dtid : SV_DispatchThreadID, 
                           uint3 gtid : SV_GroupThreadID,
                           uint3 gid : SV_GroupID)
        {
            uint timeIndex = dtid.x;
            
            if (timeIndex >= g_numTimePoints)
                return;
            
            float t = g_TimePoints[timeIndex];
            float magnetization = 0.0f;
            
            // Process components in chunks to use shared memory efficiently
            for (uint componentStart = 0; componentStart < g_numComponents; componentStart += 256)
            {
                // Load component data into shared memory
                uint localIdx = gtid.x;
                uint globalIdx = componentStart + localIdx;
                
                if (globalIdx < g_numComponents)
                {
                    s_componentData[localIdx * 2] = g_T2Values[globalIdx];
                    s_componentData[localIdx * 2 + 1] = g_Amplitudes[globalIdx];
                }
                
                // Synchronize threads within the group
                GroupMemoryBarrierWithGroupSync();
                
                // Compute contributions from loaded components
                for (uint i = 0; i < 256 && (componentStart + i) < g_numComponents; i++)
                {
                    float t2 = s_componentData[i * 2];
                    float amplitude = s_componentData[i * 2 + 1];
                    
                    // Apply exponential decay: M(t) = A * exp(-t/T2)
                    magnetization += amplitude * exp(-t / t2);
                }
                
                // Synchronize before loading next chunk
                GroupMemoryBarrierWithGroupSync();
            }
            
            // Store result
            g_Magnetization[timeIndex] = magnetization;
        }";

        // Alternative HLSL shader for older DirectX versions
        private const string HLSL_COMPUTE_SHADER = @"
        // Input buffers
        StructuredBuffer<float> g_T2Values : register(t0);
        StructuredBuffer<float> g_Amplitudes : register(t1);
        StructuredBuffer<float> g_TimePoints : register(t2);
        
        // Output buffer
        RWStructuredBuffer<float> g_Magnetization : register(u0);
        
        // Constants
        cbuffer Constants : register(b0)
        {
            uint g_numComponents;
            uint g_numTimePoints;
            uint g_padding1;
            uint g_padding2;
        };
        
        [numthreads(256, 1, 1)]
        void CSMain(uint3 dtid : SV_DispatchThreadID)
        {
            uint timeIndex = dtid.x;
            
            if (timeIndex >= g_numTimePoints)
                return;
            
            float t = g_TimePoints[timeIndex];
            float magnetization = 0.0f;
            
            // Sum contributions from all relaxation components
            for (uint i = 0; i < g_numComponents; i++)
            {
                float t2 = g_T2Values[i];
                float amplitude = g_Amplitudes[i];
                
                // Apply exponential decay: M(t) = A * exp(-t/T2)
                magnetization += amplitude * exp(-t / t2);
            }
            
            // Store result
            g_Magnetization[timeIndex] = magnetization;
        }";

        public static bool IsGPUAvailable()
        {
            try
            {
                using (var device = new Device(SharpDX.Direct3D.DriverType.Hardware, DeviceCreationFlags.None))
                {
                    // Try to check if the device supports compute shaders
                    var featureLevel = device.FeatureLevel;

                    // DirectX 11 Feature Level 10.0 and above support compute shaders
                    return featureLevel >= SharpDX.Direct3D.FeatureLevel.Level_10_0;
                }
            }
            catch
            {
                return false;
            }
        }

        public NMRGPUDirectCompute()
        {
            Initialize();
        }

        private void Initialize()
        {
            try
            {
                // Create device and context
                _device = new Device(SharpDX.Direct3D.DriverType.Hardware, DeviceCreationFlags.None);
                _context = _device.ImmediateContext;

                // Compile compute shader
                CompilationResult shaderResult;
                try
                {
                    // Try to compile the optimized shader first
                    shaderResult = ShaderBytecode.Compile(COMPUTE_SHADER_SOURCE, "CS_ComputeDecay", "cs_5_0", ShaderFlags.None, EffectFlags.None);
                }
                catch
                {
                    // Fall back to basic HLSL shader
                    shaderResult = ShaderBytecode.Compile(HLSL_COMPUTE_SHADER, "CSMain", "cs_5_0", ShaderFlags.None, EffectFlags.None);
                }

                // Create compute shader
                _computeShader = new ComputeShader(_device, shaderResult.Bytecode);
                shaderResult.Dispose();

                Logger.Log("[NMRGPUCompute] GPU compute shader initialized successfully");
            }
            catch (Exception ex)
            {
                Logger.Log($"[NMRGPUCompute] Failed to initialize GPU compute: {ex.Message}");
                throw;
            }
        }

        public async Task<float[]> ComputeDecayAsync(float[] t2Values, float[] amplitudes, float[] timePoints, CancellationToken cancellationToken = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(NMRGPUDirectCompute));

            // Prepare result array
            var result = new float[timePoints.Length];

            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Create input buffers
                using (var t2Buffer = CreateStructuredBuffer(t2Values))
                using (var amplitudeBuffer = CreateStructuredBuffer(amplitudes))
                using (var timeBuffer = CreateStructuredBuffer(timePoints))

                // Create output buffer
                using (var outputBuffer = CreateStructuredBuffer(result, true))

                // Create constants buffer
                using (var constantsBuffer = CreateConstantsBuffer(t2Values.Length, timePoints.Length))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Set up compute shader
                    _context.ComputeShader.Set(_computeShader);

                    // Bind buffers
                    _context.ComputeShader.SetShaderResource(0, new ShaderResourceView(_device, t2Buffer));
                    _context.ComputeShader.SetShaderResource(1, new ShaderResourceView(_device, amplitudeBuffer));
                    _context.ComputeShader.SetShaderResource(2, new ShaderResourceView(_device, timeBuffer));
                    _context.ComputeShader.SetUnorderedAccessView(0, new UnorderedAccessView(_device, outputBuffer));
                    _context.ComputeShader.SetConstantBuffer(0, constantsBuffer);

                    cancellationToken.ThrowIfCancellationRequested();

                    // Dispatch compute shader
                    int threadGroups = (timePoints.Length + 255) / 256;
                    _context.Dispatch(threadGroups, 1, 1);

                    // Wait for GPU to finish
                    _context.Flush();

                    cancellationToken.ThrowIfCancellationRequested();

                    // Copy result back to CPU
                    CopyFromBuffer(outputBuffer, result);
                }
            }, cancellationToken);

            return result;
        }

        private Buffer CreateStructuredBuffer<T>(T[] data, bool isOutput = false) where T : struct
        {
            var elementSize = Marshal.SizeOf<T>();
            var bufferDesc = new BufferDescription
            {
                SizeInBytes = data.Length * elementSize,
                Usage = ResourceUsage.Default,
                BindFlags = isOutput ? BindFlags.UnorderedAccess : BindFlags.ShaderResource,
                StructureByteStride = elementSize,
                CpuAccessFlags = isOutput ? CpuAccessFlags.Read : CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.BufferStructured
            };

            if (!isOutput)
            {
                return Buffer.Create(_device, data, bufferDesc);
            }
            else
            {
                return new Buffer(_device, bufferDesc);
            }
        }

        private Buffer CreateConstantsBuffer(int numComponents, int numTimePoints)
        {
            var constants = new uint[] { (uint)numComponents, (uint)numTimePoints, 0, 0 };
            var constantsDesc = new BufferDescription
            {
                SizeInBytes = 16,
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ConstantBuffer,
                CpuAccessFlags = CpuAccessFlags.None
            };

            return Buffer.Create(_device, constants, constantsDesc);
        }

        private void CopyFromBuffer<T>(Buffer buffer, T[] result) where T : struct
        {
            // Create staging buffer for readback
            var stagingDesc = buffer.Description;
            stagingDesc.Usage = ResourceUsage.Staging;
            stagingDesc.BindFlags = BindFlags.None;
            stagingDesc.CpuAccessFlags = CpuAccessFlags.Read;

            using (var stagingBuffer = new Buffer(_device, stagingDesc))
            {
                // Copy from GPU to staging buffer
                _context.CopyResource(buffer, stagingBuffer);

                // Map and read data
                var dataBox = _context.MapSubresource(stagingBuffer, 0, MapMode.Read, MapFlags.None);
                try
                {
                    var elementSize = Marshal.SizeOf<T>();
                    var dataPointer = dataBox.DataPointer;

                    for (int i = 0; i < result.Length; i++)
                    {
                        result[i] = Marshal.PtrToStructure<T>(IntPtr.Add(dataPointer, i * elementSize));
                    }
                }
                finally
                {
                    _context.UnmapSubresource(stagingBuffer, 0);
                }
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _computeShader?.Dispose();
                _context?.Dispose();
                _device?.Dispose();

                _disposed = true;
            }
        }
    }
}