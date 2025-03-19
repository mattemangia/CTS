// CTViewer.cpp
#define CTVIEWER_EXPORTS

#include "pch.h"
#include "Logger.h" // Include the shared logging header
#include "CTViewer.h"
#include "VolumeRenderer.h"
#include <memory>
#include <string>

// Global renderer instance
std::unique_ptr<VolumeRenderer> g_renderer;

// Global log callback
LogCallback g_logCallback = nullptr;

// Log severity levels
void Log(const char* message, int severity) {  // Changed to use int
    if (g_logCallback) {
        g_logCallback(message, severity);  // Removed cast
    }
}




// Set the logging callback from C#
CTVIEWER_API void SetLogCallback(LogCallback callback) {
    g_logCallback = callback;
    Log("C++ logging callback initialized", LOG_INFO);
}

// Initialize the DirectX renderer
CTVIEWER_API bool Initialize(void* hwnd, int width, int height) {
    try {
        Log("Initialize DirectX renderer called", LOG_INFO);

        if (!hwnd) {
            Log("Failed to initialize: Invalid window handle (null)", LOG_ERROR);
            return false;
        }

        if (width <= 0 || height <= 0) {
            std::string msg = "Failed to initialize: Invalid dimensions: " + std::to_string(width) + "x" + std::to_string(height);
            Log(msg.c_str(), LOG_ERROR);
            return false;
        }

        // Create the renderer
        Log("Creating VolumeRenderer instance", LOG_INFO);
        g_renderer = std::make_unique<VolumeRenderer>();

        // Initialize the renderer with the window handle and dimensions
        std::string initMsg = "Initializing renderer with dimensions: " + std::to_string(width) + "x" + std::to_string(height);
        Log(initMsg.c_str(), LOG_INFO);

        bool result = g_renderer->Initialize((HWND)hwnd, width, height);

        if (!result) {
            Log("DirectX renderer initialization failed", LOG_ERROR);
        }
        else {
            Log("DirectX renderer initialized successfully", LOG_INFO);
        }

        return result;
    }
    catch (std::exception& e) {
        std::string msg = "Exception during initialization: " + std::string(e.what());
        Log(msg.c_str(), LOG_ERROR);
        return false;
    }
    catch (...) {
        Log("Unknown exception during initialization", LOG_ERROR);
        return false;
    }
}

// Shutdown the renderer
CTVIEWER_API void Shutdown() {
    try {
        Log("Shutting down DirectX renderer", LOG_INFO);
        if (g_renderer) {
            g_renderer->Shutdown();
            g_renderer.reset();
            Log("DirectX renderer shutdown complete", LOG_INFO);
        }
        else {
            Log("Shutdown called but renderer was not initialized", LOG_WARNING);
        }
    }
    catch (std::exception& e) {
        std::string msg = "Exception during shutdown: " + std::string(e.what());
        Log(msg.c_str(), LOG_ERROR);
    }
    catch (...) {
        Log("Unknown exception during shutdown", LOG_ERROR);
    }
}

// Render the volume
CTVIEWER_API void Render() {
    try {
        if (g_renderer) {
            g_renderer->Render();
        }
        else {
            Log("Render called but renderer is not initialized", LOG_ERROR);
        }
    }
    catch (std::exception& e) {
        std::string msg = "Exception during render: " + std::string(e.what());
        Log(msg.c_str(), LOG_ERROR);
    }
    catch (...) {
        Log("Unknown exception during render", LOG_ERROR);
    }
}

// Load volume data
CTVIEWER_API bool LoadVolumeData(const unsigned char* data, int width, int height, int depth, float voxelSize) {
    try {
        if (!g_renderer) {
            Log("LoadVolumeData called but renderer is not initialized", LOG_ERROR);
            return false;
        }

        std::string msg = "Loading volume data: " + std::to_string(width) + "x"
            + std::to_string(height) + "x" + std::to_string(depth)
            + " with voxel size: " + std::to_string(voxelSize);
        Log(msg.c_str(), LOG_INFO);

        if (!data) {
            Log("Failed to load volume data: Data pointer is null", LOG_ERROR);
            return false;
        }

        if (width <= 0 || height <= 0 || depth <= 0) {
            Log("Failed to load volume data: Invalid dimensions", LOG_ERROR);
            return false;
        }

        bool result = g_renderer->LoadVolumeData(data, width, height, depth, voxelSize);

        if (result) {
            Log("Volume data loaded successfully", LOG_INFO);
        }
        else {
            Log("Failed to load volume data", LOG_ERROR);
        }

        return result;
    }
    catch (std::exception& e) {
        std::string msg = "Exception while loading volume data: " + std::string(e.what());
        Log(msg.c_str(), LOG_ERROR);
        return false;
    }
    catch (...) {
        Log("Unknown exception while loading volume data", LOG_ERROR);
        return false;
    }
}

// Load label data
CTVIEWER_API bool LoadLabelData(const unsigned char* data, int width, int height, int depth) {
    try {
        if (!g_renderer) {
            Log("LoadLabelData called but renderer is not initialized", LOG_ERROR);
            return false;
        }

        std::string msg = "Loading label data: " + std::to_string(width) + "x"
            + std::to_string(height) + "x" + std::to_string(depth);
        Log(msg.c_str(), LOG_INFO);

        if (!data) {
            Log("Failed to load label data: Data pointer is null", LOG_ERROR);
            return false;
        }

        if (width <= 0 || height <= 0 || depth <= 0) {
            Log("Failed to load label data: Invalid dimensions", LOG_ERROR);
            return false;
        }

        bool result = g_renderer->LoadLabelData(data, width, height, depth);

        if (result) {
            Log("Label data loaded successfully", LOG_INFO);
        }
        else {
            Log("Failed to load label data", LOG_ERROR);
        }

        return result;
    }
    catch (std::exception& e) {
        std::string msg = "Exception while loading label data: " + std::string(e.what());
        Log(msg.c_str(), LOG_ERROR);
        return false;
    }
    catch (...) {
        Log("Unknown exception while loading label data", LOG_ERROR);
        return false;
    }
}

// Update materials
CTVIEWER_API void UpdateMaterials(const int* colors, int count) {
    try {
        if (!g_renderer) {
            Log("UpdateMaterials called but renderer is not initialized", LOG_ERROR);
            return;
        }

        std::string msg = "Updating " + std::to_string(count) + " materials";
        Log(msg.c_str(), LOG_INFO);

        if (!colors) {
            Log("Failed to update materials: Colors pointer is null", LOG_ERROR);
            return;
        }

        if (count <= 0) {
            Log("Failed to update materials: Invalid count", LOG_WARNING);
            return;
        }

        g_renderer->UpdateMaterials(colors, count);
        Log("Materials updated successfully", LOG_INFO);
    }
    catch (std::exception& e) {
        std::string msg = "Exception while updating materials: " + std::string(e.what());
        Log(msg.c_str(), LOG_ERROR);
    }
    catch (...) {
        Log("Unknown exception while updating materials", LOG_ERROR);
    }
}

// Resize viewport
CTVIEWER_API void Resize(int width, int height) {
    try {
        if (!g_renderer) {
            Log("Resize called but renderer is not initialized", LOG_ERROR);
            return;
        }

        std::string msg = "Resizing renderer to " + std::to_string(width) + "x" + std::to_string(height);
        Log(msg.c_str(), LOG_INFO);

        if (width <= 0 || height <= 0) {
            Log("Failed to resize: Invalid dimensions", LOG_ERROR);
            return;
        }

        g_renderer->Resize(width, height);
        Log("Renderer resized successfully", LOG_INFO);
    }
    catch (std::exception& e) {
        std::string msg = "Exception during resize: " + std::string(e.what());
        Log(msg.c_str(), LOG_ERROR);
    }
    catch (...) {
        Log("Unknown exception during resize", LOG_ERROR);
    }
}

// Rotate camera
CTVIEWER_API void RotateCamera(float deltaX, float deltaY) {
    try {
        if (!g_renderer) {
            Log("RotateCamera called but renderer is not initialized", LOG_ERROR);
            return;
        }

        g_renderer->RotateCamera(deltaX, deltaY);
    }
    catch (std::exception& e) {
        std::string msg = "Exception during camera rotation: " + std::string(e.what());
        Log(msg.c_str(), LOG_ERROR);
    }
    catch (...) {
        Log("Unknown exception during camera rotation", LOG_ERROR);
    }
}

// Zoom camera
CTVIEWER_API void ZoomCamera(float delta) {
    try {
        if (!g_renderer) {
            Log("ZoomCamera called but renderer is not initialized", LOG_ERROR);
            return;
        }

        g_renderer->ZoomCamera(delta);
    }
    catch (std::exception& e) {
        std::string msg = "Exception during camera zoom: " + std::string(e.what());
        Log(msg.c_str(), LOG_ERROR);
    }
    catch (...) {
        Log("Unknown exception during camera zoom", LOG_ERROR);
    }
}

// Reset camera
CTVIEWER_API void ResetCamera() {
    try {
        if (!g_renderer) {
            Log("ResetCamera called but renderer is not initialized", LOG_ERROR);
            return;
        }

        Log("Resetting camera", LOG_INFO);
        g_renderer->ResetCamera();
    }
    catch (std::exception& e) {
        std::string msg = "Exception during camera reset: " + std::string(e.what());
        Log(msg.c_str(), LOG_ERROR);
    }
    catch (...) {
        Log("Unknown exception during camera reset", LOG_ERROR);
    }
}

// Set rendering parameters
CTVIEWER_API void SetRenderingParams(float opacity, float brightness, float contrast, int renderMode, bool showLabels) {
    try {
        if (!g_renderer) {
            Log("SetRenderingParams called but renderer is not initialized", LOG_ERROR);
            return;
        }

        std::string msg = "Setting rendering params: opacity=" + std::to_string(opacity) +
            ", brightness=" + std::to_string(brightness) +
            ", contrast=" + std::to_string(contrast) +
            ", renderMode=" + std::to_string(renderMode) +
            ", showLabels=" + std::to_string(showLabels);
        Log(msg.c_str(), LOG_INFO);

        g_renderer->SetOpacity(opacity);
        g_renderer->SetBrightness(brightness);
        g_renderer->SetContrast(contrast);
        g_renderer->SetRenderMode(renderMode);
        g_renderer->SetShowLabels(showLabels);
    }
    catch (std::exception& e) {
        std::string msg = "Exception while setting rendering params: " + std::string(e.what());
        Log(msg.c_str(), LOG_ERROR);
    }
    catch (...) {
        Log("Unknown exception while setting rendering params", LOG_ERROR);
    }
}
