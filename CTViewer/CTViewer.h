// CTViewer.h
#pragma once

#define CTVIEWER_API __declspec(dllexport)


// Define logging callback type
typedef void (*LogCallback)(const char* message, int severity);

// Add a function to set the callback
extern "C" CTVIEWER_API void SetLogCallback(LogCallback callback);

// Rest of your existing declarations
extern "C" CTVIEWER_API bool Initialize(void* hwnd, int width, int height);
extern "C" CTVIEWER_API void Shutdown();



extern "C" {
    // Initialize the DirectX viewer
    CTVIEWER_API bool Initialize(void* hwnd, int width, int height);

    // Clean up resources
    CTVIEWER_API void Shutdown();

    // Load volume data
    CTVIEWER_API bool LoadVolumeData(const unsigned char* data, int width, int height, int depth, float voxelSize);

    // Load label volume data
    CTVIEWER_API bool LoadLabelData(const unsigned char* data, int width, int height, int depth);

    // Update material colors for labels
    CTVIEWER_API void UpdateMaterials(const int* colors, int count);

    // Render the current frame
    CTVIEWER_API void Render();

    // Resize the viewport
    CTVIEWER_API void Resize(int width, int height);

    // Camera controls
    CTVIEWER_API void RotateCamera(float deltaX, float deltaY);
    CTVIEWER_API void ZoomCamera(float delta);
    CTVIEWER_API void ResetCamera();

    // Rendering parameters
    CTVIEWER_API void SetOpacity(float opacity);
    CTVIEWER_API void SetBrightness(float brightness);
    CTVIEWER_API void SetContrast(float contrast);
    CTVIEWER_API void SetRenderMode(int mode); // 0=Volume, 1=MIP, 2=Isosurface
    CTVIEWER_API void ShowLabels(bool show);
}
