// CTViewerNative.cs
using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace CTSegmenter
{
    internal static class CTViewerNative
    {
        // Define delegate that matches the C++ callback signature
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void LogCallbackDelegate(IntPtr message, int severity);

        // Preserve the delegate from garbage collection
        private static LogCallbackDelegate _logCallback;

        [DllImport("CTViewer.dll")]
        public static extern void SetLogCallback(LogCallbackDelegate callback);

     
        // DLL imports
        [DllImport("CTViewer.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern bool Initialize(IntPtr hwnd, int width, int height);

        [DllImport("CTViewer.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void Shutdown();

        [DllImport("CTViewer.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern bool LoadVolumeData(IntPtr data, int width, int height, int depth, float voxelSize);

        [DllImport("CTViewer.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern bool LoadLabelData(IntPtr data, int width, int height, int depth);

        [DllImport("CTViewer.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void UpdateMaterials(IntPtr colors, int count);

        [DllImport("CTViewer.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void Render();

        [DllImport("CTViewer.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void Resize(int width, int height);

        [DllImport("CTViewer.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void RotateCamera(float deltaX, float deltaY);

        [DllImport("CTViewer.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void ZoomCamera(float delta);

        [DllImport("CTViewer.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void ResetCamera();

        [DllImport("CTViewer.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void SetOpacity(float opacity);

        [DllImport("CTViewer.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void SetBrightness(float brightness);

        [DllImport("CTViewer.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void SetContrast(float contrast);

        [DllImport("CTViewer.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void SetRenderMode(int mode);

        [DllImport("CTViewer.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void ShowLabels(bool show);
        
        public static void InitializeLogging()
        {
            // Create callback and keep reference to prevent garbage collection
            _logCallback = LogFromNative;
            SetLogCallback(_logCallback);
        }
        
        // Callback method that will be called from native code
        private static void LogFromNative(IntPtr messagePtr, int severity)
        {
            string message = Marshal.PtrToStringAnsi(messagePtr);

            // Map severity to your log levels
            switch (severity)
            {
                case 0: // Info
                    Logger.Log(message, LogLevel.Information);
                    break;
                case 1: // Warning
                    Logger.Log(message, LogLevel.Warning);
                    break;
                case 2: // Error
                    Logger.Log(message, LogLevel.Error);
                    break;
                default:
                    Logger.Log(message, LogLevel.Debug);
                    break;
            }

        }
    }
}
