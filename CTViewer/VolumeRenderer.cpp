// VolumeRenderer.cpp
#include "pch.h"
#include "VolumeRenderer.h"
#include "Logger.h"
#include <d3dcompiler.h>
#include <DirectXColors.h>

// Reference to the logging function declared in CTViewer.cpp
extern void Log(const char* message, int severity);


// Shorthands for log severity
#define LOG_INFO 0
#define LOG_WARNING 1
#define LOG_ERROR 2

using namespace DirectX;

// Vertex structure for the cube corners
struct Vertex
{
    XMFLOAT3 position;
    XMFLOAT3 texCoord;
};

// Camera/transform constant buffer structure (matches shader)
struct ConstantBuffer
{
    XMMATRIX worldMatrix;
    XMMATRIX viewMatrix;
    XMMATRIX projectionMatrix;
    XMFLOAT3 cameraPosition;
    float padding;
};

// Rendering parameters buffer structure (matches shader)
struct RenderParamsBuffer
{
    float opacity;
    float brightness;
    float contrast;
    int renderMode;
    XMFLOAT4 volumeScale;
    int showLabels;
    float padding[2];
};

VolumeRenderer::VolumeRenderer()
{
    Log("VolumeRenderer constructor called", LOG_INFO);

    // Initialize all pointers to nullptr
    m_volumeWidth = 0;
    m_volumeHeight = 0;
    m_volumeDepth = 0;
    m_voxelSize = 1.0f;

    m_cameraPosition = { 0.0f, 0.0f, -2.0f };
    m_focusPoint = { 0.0f, 0.0f, 0.0f };
    m_upVector = { 0.0f, 1.0f, 0.0f };
    m_cameraTheta = 0.0f;
    m_cameraPhi = 0.0f;
    m_cameraRadius = 2.0f;

    m_opacity = 0.05f;
    m_brightness = 0.0f;
    m_contrast = 1.0f;
    m_renderMode = 0;
    m_showLabels = true;
}

VolumeRenderer::~VolumeRenderer()
{
    Log("VolumeRenderer destructor called", LOG_INFO);
    Shutdown();
}

bool VolumeRenderer::Initialize(HWND hwnd, int width, int height)
{
    Log("VolumeRenderer::Initialize started", LOG_INFO);

    char buffer[256];
    sprintf_s(buffer, "Window handle: 0x%p, Dimensions: %dx%d", hwnd, width, height);
    Log(buffer, LOG_INFO);

    if (!hwnd) {
        Log("Invalid window handle", LOG_ERROR);
        return false;
    }

    if (width <= 0 || height <= 0) {
        sprintf_s(buffer, "Invalid dimensions: %dx%d", width, height);
        Log(buffer, LOG_ERROR);
        return false;
    }

    m_width = width;
    m_height = height;

    // Create the device and swap chain
    Log("Creating device and swap chain...", LOG_INFO);
    if (!CreateDeviceAndSwapChain(hwnd)) {
        Log("Failed to create device and swap chain", LOG_ERROR);
        return false;
    }
    Log("Device and swap chain created successfully", LOG_INFO);

    // Create render target view
    Log("Creating render target view...", LOG_INFO);
    if (!CreateRenderTargetView()) {
        Log("Failed to create render target view", LOG_ERROR);
        return false;
    }
    Log("Render target view created successfully", LOG_INFO);

    // Create depth stencil view
    Log("Creating depth stencil view...", LOG_INFO);
    if (!CreateDepthStencilView(width, height)) {
        Log("Failed to create depth stencil view", LOG_ERROR);
        return false;
    }
    Log("Depth stencil view created successfully", LOG_INFO);

    // Set up viewport
    Log("Setting up viewport...", LOG_INFO);
    SetupViewport(width, height);

    // Create shaders and input layout
    Log("Creating shaders and input layout...", LOG_INFO);
    if (!CreateShaders()) {
        Log("Failed to create shaders", LOG_ERROR);
        return false;
    }
    Log("Shaders and input layout created successfully", LOG_INFO);

    // Create constant buffers
    Log("Creating constant buffers...", LOG_INFO);
    if (!CreateConstantBuffers()) {
        Log("Failed to create constant buffers", LOG_ERROR);
        return false;
    }
    Log("Constant buffers created successfully", LOG_INFO);

    // Create samplers
    Log("Creating samplers...", LOG_INFO);
    if (!CreateSamplers()) {
        Log("Failed to create samplers", LOG_ERROR);
        return false;
    }
    Log("Samplers created successfully", LOG_INFO);

    // Create cube vertices for ray casting
    Log("Creating vertex and index buffers...", LOG_INFO);
    Vertex vertices[] =
    {
        { XMFLOAT3(-1.0f, -1.0f, -1.0f), XMFLOAT3(0.0f, 0.0f, 0.0f) },
        { XMFLOAT3(-1.0f, -1.0f,  1.0f), XMFLOAT3(0.0f, 0.0f, 1.0f) },
        { XMFLOAT3(-1.0f,  1.0f, -1.0f), XMFLOAT3(0.0f, 1.0f, 0.0f) },
        { XMFLOAT3(-1.0f,  1.0f,  1.0f), XMFLOAT3(0.0f, 1.0f, 1.0f) },
        { XMFLOAT3(1.0f, -1.0f, -1.0f), XMFLOAT3(1.0f, 0.0f, 0.0f) },
        { XMFLOAT3(1.0f, -1.0f,  1.0f), XMFLOAT3(1.0f, 0.0f, 1.0f) },
        { XMFLOAT3(1.0f,  1.0f, -1.0f), XMFLOAT3(1.0f, 1.0f, 0.0f) },
        { XMFLOAT3(1.0f,  1.0f,  1.0f), XMFLOAT3(1.0f, 1.0f, 1.0f) }
    };

    // Create vertex buffer
    D3D11_BUFFER_DESC vertexBufferDesc = {};
    vertexBufferDesc.Usage = D3D11_USAGE_DEFAULT;
    vertexBufferDesc.ByteWidth = sizeof(Vertex) * 8;
    vertexBufferDesc.BindFlags = D3D11_BIND_VERTEX_BUFFER;

    D3D11_SUBRESOURCE_DATA vertexData = {};
    vertexData.pSysMem = vertices;

    HRESULT hr = m_device->CreateBuffer(&vertexBufferDesc, &vertexData, &m_vertexBuffer);
    if (FAILED(hr)) {
        sprintf_s(buffer, "Failed to create vertex buffer, HRESULT: 0x%08X", hr);
        Log(buffer, LOG_ERROR);
        return false;
    }

    // Create index buffer for a cube
    UINT indices[] =
    {
        0, 1, 2,  2, 1, 3,  // front face
        4, 6, 5,  5, 6, 7,  // back face
        0, 2, 4,  4, 2, 6,  // left face
        1, 5, 3,  3, 5, 7,  // right face
        0, 4, 1,  1, 4, 5,  // bottom face
        2, 3, 6,  6, 3, 7   // top face
    };

    D3D11_BUFFER_DESC indexBufferDesc = {};
    indexBufferDesc.Usage = D3D11_USAGE_DEFAULT;
    indexBufferDesc.ByteWidth = sizeof(UINT) * 36;
    indexBufferDesc.BindFlags = D3D11_BIND_INDEX_BUFFER;

    D3D11_SUBRESOURCE_DATA indexData = {};
    indexData.pSysMem = indices;

    hr = m_device->CreateBuffer(&indexBufferDesc, &indexData, &m_indexBuffer);
    if (FAILED(hr)) {
        sprintf_s(buffer, "Failed to create index buffer, HRESULT: 0x%08X", hr);
        Log(buffer, LOG_ERROR);
        return false;
    }
    Log("Vertex and index buffers created successfully", LOG_INFO);

    // Initialize default materials (256 colors)
    Log("Initializing default materials...", LOG_INFO);
    m_materials.resize(256, XMFLOAT4(0, 0, 0, 0));

    Log("VolumeRenderer initialized successfully", LOG_INFO);
    return true;
}

void VolumeRenderer::Shutdown()
{
    Log("VolumeRenderer::Shutdown called", LOG_INFO);

    // Release all resources
    if (m_context) {
        Log("Clearing device context state", LOG_INFO);
        m_context->ClearState();
        m_context->Flush();
    }

    Log("Releasing DirectX resources", LOG_INFO);
    m_materialBuffer.Reset();
    m_constantBuffer.Reset();
    m_indexBuffer.Reset();
    m_vertexBuffer.Reset();
    m_volumeSampler.Reset();
    m_labelSRV.Reset();
    m_labelTexture.Reset();
    m_volumeSRV.Reset();
    m_volumeTexture.Reset();
    m_inputLayout.Reset();
    m_pixelShader.Reset();
    m_vertexShader.Reset();
    m_depthStencilView.Reset();
    m_renderTargetView.Reset();
    m_swapChain.Reset();
    m_context.Reset();
    m_device.Reset();

    Log("VolumeRenderer shutdown complete", LOG_INFO);
}

bool VolumeRenderer::LoadVolumeData(const unsigned char* data, int width, int height, int depth, float voxelSize)
{
    char buffer[256];
    sprintf_s(buffer, "LoadVolumeData: %dx%dx%d, voxel size: %f", width, height, depth, voxelSize);
    Log(buffer, LOG_INFO);

    if (!data) {
        Log("Volume data pointer is null", LOG_ERROR);
        return false;
    }

    if (width <= 0 || height <= 0 || depth <= 0) {
        sprintf_s(buffer, "Invalid volume dimensions: %dx%dx%d", width, height, depth);
        Log(buffer, LOG_ERROR);
        return false;
    }

    m_volumeWidth = width;
    m_volumeHeight = height;
    m_volumeDepth = depth;
    m_voxelSize = voxelSize;

    bool result = CreateVolumeTexture(data);
    if (!result) {
        Log("Failed to create volume texture", LOG_ERROR);
    }
    else {
        Log("Volume texture created successfully", LOG_INFO);
    }

    return result;
}

bool VolumeRenderer::LoadLabelData(const unsigned char* data, int width, int height, int depth)
{
    char buffer[256];
    sprintf_s(buffer, "LoadLabelData: %dx%dx%d", width, height, depth);
    Log(buffer, LOG_INFO);

    if (!data) {
        Log("Label data pointer is null", LOG_ERROR);
        return false;
    }

    if (width <= 0 || height <= 0 || depth <= 0) {
        sprintf_s(buffer, "Invalid label dimensions: %dx%dx%d", width, height, depth);
        Log(buffer, LOG_ERROR);
        return false;
    }

    bool result = CreateLabelTexture(data);
    if (!result) {
        Log("Failed to create label texture", LOG_ERROR);
    }
    else {
        Log("Label texture created successfully", LOG_INFO);
    }

    return result;
}

void VolumeRenderer::UpdateMaterials(const int* colors, int count)
{
    char buffer[256];
    sprintf_s(buffer, "UpdateMaterials: count = %d", count);
    Log(buffer, LOG_INFO);

    if (!colors) {
        Log("Colors pointer is null", LOG_ERROR);
        return;
    }

    if (count <= 0 || count > 256) {
        sprintf_s(buffer, "Invalid color count: %d, clamping to 256", count);
        Log(buffer, LOG_WARNING);
        count = min(256, max(1, count));
    }

    // Update material colors
    for (int i = 0; i < count; i++) {
        // Extract ARGB components
        int argb = colors[i];
        float a = ((argb >> 24) & 0xFF) / 255.0f;
        float r = ((argb >> 16) & 0xFF) / 255.0f;
        float g = ((argb >> 8) & 0xFF) / 255.0f;
        float b = (argb & 0xFF) / 255.0f;

        m_materials[i] = XMFLOAT4(r, g, b, a);
    }

    // Update material buffer
    if (m_context && m_materialBuffer) {
        Log("Updating material buffer on GPU", LOG_INFO);

        D3D11_MAPPED_SUBRESOURCE mappedResource;
        HRESULT hr = m_context->Map(m_materialBuffer.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mappedResource);
        if (SUCCEEDED(hr)) {
            memcpy(mappedResource.pData, m_materials.data(), sizeof(XMFLOAT4) * m_materials.size());
            m_context->Unmap(m_materialBuffer.Get(), 0);
            Log("Material buffer updated successfully", LOG_INFO);
        }
        else {
            sprintf_s(buffer, "Failed to map material buffer, HRESULT: 0x%08X", hr);
            Log(buffer, LOG_ERROR);
        }
    }
    else {
        Log("Cannot update materials - context or material buffer is null", LOG_ERROR);
    }
}

void VolumeRenderer::Render()
{
    try {
        if (!m_context || !m_device || !m_swapChain) {
            Log("Cannot render - DirectX resources not initialized", LOG_ERROR);
            return;
        }

        // Clear the back buffer
        float clearColor[4] = { 0.0f, 0.0f, 0.0f, 1.0f };
        m_context->ClearRenderTargetView(m_renderTargetView.Get(), clearColor);
        m_context->ClearDepthStencilView(m_depthStencilView.Get(), D3D11_CLEAR_DEPTH, 1.0f, 0);

        // Set the render target
        m_context->OMSetRenderTargets(1, m_renderTargetView.GetAddressOf(), m_depthStencilView.Get());

        // Set up the vertex shader stage
        UINT stride = sizeof(Vertex);
        UINT offset = 0;
        m_context->IASetVertexBuffers(0, 1, m_vertexBuffer.GetAddressOf(), &stride, &offset);
        m_context->IASetIndexBuffer(m_indexBuffer.Get(), DXGI_FORMAT_R32_UINT, 0);
        m_context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        m_context->IASetInputLayout(m_inputLayout.Get());
        m_context->VSSetShader(m_vertexShader.Get(), nullptr, 0);

        // Update and set constant buffers
        UpdateConstantBuffers();
        m_context->VSSetConstantBuffers(0, 1, m_constantBuffer.GetAddressOf());
        m_context->PSSetConstantBuffers(0, 1, m_constantBuffer.GetAddressOf());

        // Set up the pixel shader stage
        m_context->PSSetShader(m_pixelShader.Get(), nullptr, 0);

        // Set textures and samplers
        if (m_volumeSRV) {
            m_context->PSSetShaderResources(0, 1, m_volumeSRV.GetAddressOf());
        }
        else {
            Log("Volume shader resource view is null during render", LOG_WARNING);
        }

        if (m_labelSRV) {
            m_context->PSSetShaderResources(1, 1, m_labelSRV.GetAddressOf());
        }
        else {
            Log("Label shader resource view is null during render", LOG_INFO); // Not an error, might not have labels
        }

        if (m_volumeSampler) {
            m_context->PSSetSamplers(0, 1, m_volumeSampler.GetAddressOf());
        }
        else {
            Log("Volume sampler is null during render", LOG_WARNING);
        }

        // Draw the cube (which becomes the volume)
        m_context->DrawIndexed(36, 0, 0);

        // Present the back buffer to the screen
        HRESULT hr = m_swapChain->Present(1, 0);
        if (FAILED(hr)) {
            char buffer[256];
            sprintf_s(buffer, "SwapChain Present failed, HRESULT: 0x%08X", hr);
            Log(buffer, LOG_ERROR);
        }
    }
    catch (std::exception& e) {
        std::string msg = "Exception in Render: " + std::string(e.what());
        Log(msg.c_str(), LOG_ERROR);
    }
    catch (...) {
        Log("Unknown exception in Render", LOG_ERROR);
    }
}

void VolumeRenderer::Resize(int width, int height)
{
    char buffer[256];
    sprintf_s(buffer, "Resize called: %dx%d", width, height);
    Log(buffer, LOG_INFO);

    if (width <= 0 || height <= 0) {
        sprintf_s(buffer, "Invalid resize dimensions: %dx%d", width, height);
        Log(buffer, LOG_ERROR);
        return;
    }

    if (!m_device || !m_swapChain) {
        Log("Cannot resize - device or swap chain is null", LOG_ERROR);
        return;
    }

    // Release render target and depth stencil
    Log("Releasing render target and depth stencil", LOG_INFO);
    m_renderTargetView.Reset();
    m_depthStencilView.Reset();

    // Resize the swap chain
    Log("Resizing swap chain buffers", LOG_INFO);
    HRESULT hr = m_swapChain->ResizeBuffers(0, width, height, DXGI_FORMAT_UNKNOWN, 0);
    if (FAILED(hr)) {
        sprintf_s(buffer, "ResizeBuffers failed, HRESULT: 0x%08X", hr);
        Log(buffer, LOG_ERROR);
        return;
    }

    // Recreate the render target view
    Log("Recreating render target view", LOG_INFO);
    ComPtr<ID3D11Texture2D> backBuffer;
    hr = m_swapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&backBuffer);
    if (FAILED(hr)) {
        sprintf_s(buffer, "GetBuffer failed, HRESULT: 0x%08X", hr);
        Log(buffer, LOG_ERROR);
        return;
    }

    hr = m_device->CreateRenderTargetView(backBuffer.Get(), nullptr, &m_renderTargetView);
    if (FAILED(hr)) {
        sprintf_s(buffer, "CreateRenderTargetView failed, HRESULT: 0x%08X", hr);
        Log(buffer, LOG_ERROR);
        return;
    }

    // Recreate the depth stencil view
    Log("Recreating depth stencil view", LOG_INFO);
    if (!CreateDepthStencilView(width, height)) {
        Log("Failed to recreate depth stencil view", LOG_ERROR);
        return;
    }

    // Update the viewport
    Log("Updating viewport", LOG_INFO);
    m_width = width;
    m_height = height;
    SetupViewport(width, height);

    Log("Resize completed successfully", LOG_INFO);
}

void VolumeRenderer::RotateCamera(float deltaX, float deltaY)
{
    // Update camera angles
    m_cameraTheta += deltaX;
    m_cameraPhi += deltaY;

    // Clamp phi to avoid gimbal lock
    m_cameraPhi = max(-XM_PIDIV2 + 0.1f, min(XM_PIDIV2 - 0.1f, m_cameraPhi));

    // Calculate new camera position
    float sinTheta = sin(m_cameraTheta);
    float cosTheta = cos(m_cameraTheta);
    float sinPhi = sin(m_cameraPhi);
    float cosPhi = cos(m_cameraPhi);

    m_cameraPosition.x = m_focusPoint.x + m_cameraRadius * cosPhi * sinTheta;
    m_cameraPosition.y = m_focusPoint.y + m_cameraRadius * sinPhi;
    m_cameraPosition.z = m_focusPoint.z + m_cameraRadius * cosPhi * cosTheta;

    char buffer[256];
    sprintf_s(buffer, "Camera rotated to theta: %f, phi: %f, radius: %f",
        m_cameraTheta, m_cameraPhi, m_cameraRadius);
    Log(buffer, LOG_INFO);
}

void VolumeRenderer::ZoomCamera(float delta)
{
    // Update camera distance
    m_cameraRadius -= delta;
    m_cameraRadius = max(0.5f, min(10.0f, m_cameraRadius));

    // Update camera position
    RotateCamera(0, 0);

    char buffer[256];
    sprintf_s(buffer, "Camera zoomed to radius: %f", m_cameraRadius);
    Log(buffer, LOG_INFO);
}

void VolumeRenderer::ResetCamera()
{
    Log("Resetting camera to default position", LOG_INFO);

    // Reset camera parameters
    m_cameraPosition = { 0.0f, 0.0f, -2.0f };
    m_focusPoint = { 0.0f, 0.0f, 0.0f };
    m_upVector = { 0.0f, 1.0f, 0.0f };
    m_cameraTheta = 0.0f;
    m_cameraPhi = 0.0f;
    m_cameraRadius = 2.0f;
}

// Private implementation methods
bool VolumeRenderer::CreateDeviceAndSwapChain(HWND hwnd)
{
    Log("CreateDeviceAndSwapChain called", LOG_INFO);

    // Create device and swap chain
    DXGI_SWAP_CHAIN_DESC swapChainDesc = {};
    swapChainDesc.BufferCount = 1;
    swapChainDesc.BufferDesc.Width = m_width;
    swapChainDesc.BufferDesc.Height = m_height;
    swapChainDesc.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    swapChainDesc.BufferDesc.RefreshRate.Numerator = 60;
    swapChainDesc.BufferDesc.RefreshRate.Denominator = 1;
    swapChainDesc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    swapChainDesc.OutputWindow = hwnd;
    swapChainDesc.SampleDesc.Count = 1;
    swapChainDesc.SampleDesc.Quality = 0;
    swapChainDesc.Windowed = TRUE;

    UINT createDeviceFlags = 0;
#ifdef _DEBUG
    createDeviceFlags |= D3D11_CREATE_DEVICE_DEBUG;
    Log("Enabling DirectX debug layer", LOG_INFO);
#endif

    D3D_FEATURE_LEVEL featureLevels[] = {
        D3D_FEATURE_LEVEL_11_1,
        D3D_FEATURE_LEVEL_11_0,
        D3D_FEATURE_LEVEL_10_1,
        D3D_FEATURE_LEVEL_10_0
    };
    UINT numFeatureLevels = ARRAYSIZE(featureLevels);

    char buffer[256];
    sprintf_s(buffer, "Requested swap chain dimensions: %dx%d", m_width, m_height);
    Log(buffer, LOG_INFO);

    D3D_FEATURE_LEVEL featureLevel;
    HRESULT hr = D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
        createDeviceFlags, featureLevels, numFeatureLevels, D3D11_SDK_VERSION, &swapChainDesc,
        &m_swapChain, &m_device, &featureLevel, &m_context);

    if (FAILED(hr)) {
        sprintf_s(buffer, "D3D11CreateDeviceAndSwapChain failed, HRESULT: 0x%08X", hr);
        Log(buffer, LOG_ERROR);

        // Try without debug layer if that could be the issue
        if (createDeviceFlags & D3D11_CREATE_DEVICE_DEBUG) {
            Log("Retrying without debug layer", LOG_INFO);
            createDeviceFlags &= ~D3D11_CREATE_DEVICE_DEBUG;

            hr = D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
                createDeviceFlags, featureLevels, numFeatureLevels, D3D11_SDK_VERSION, &swapChainDesc,
                &m_swapChain, &m_device, &featureLevel, &m_context);

            if (FAILED(hr)) {
                sprintf_s(buffer, "Second attempt failed, HRESULT: 0x%08X", hr);
                Log(buffer, LOG_ERROR);
                return false;
            }

            Log("Device created successfully without debug layer", LOG_INFO);
        }
        else {
            return false;
        }
    }

    const char* featureLevelStr = "Unknown";
    switch (featureLevel) {
    case D3D_FEATURE_LEVEL_11_1: featureLevelStr = "11.1"; break;
    case D3D_FEATURE_LEVEL_11_0: featureLevelStr = "11.0"; break;
    case D3D_FEATURE_LEVEL_10_1: featureLevelStr = "10.1"; break;
    case D3D_FEATURE_LEVEL_10_0: featureLevelStr = "10.0"; break;
    }

    sprintf_s(buffer, "Device created with feature level %s", featureLevelStr);
    Log(buffer, LOG_INFO);

    return true;
}

bool VolumeRenderer::CreateRenderTargetView()
{
    Log("CreateRenderTargetView called", LOG_INFO);

    // Get the back buffer and create a render target view
    ComPtr<ID3D11Texture2D> backBuffer;
    HRESULT hr = m_swapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&backBuffer);
    if (FAILED(hr)) {
        char buffer[256];
        sprintf_s(buffer, "Failed to get back buffer, HRESULT: 0x%08X", hr);
        Log(buffer, LOG_ERROR);
        return false;
    }

    // Get back buffer dimensions for logging
    D3D11_TEXTURE2D_DESC backBufferDesc;
    backBuffer->GetDesc(&backBufferDesc);
    char buffer[256];
    sprintf_s(buffer, "Back buffer dimensions: %dx%d", backBufferDesc.Width, backBufferDesc.Height);
    Log(buffer, LOG_INFO);

    hr = m_device->CreateRenderTargetView(backBuffer.Get(), nullptr, &m_renderTargetView);
    if (FAILED(hr)) {
        sprintf_s(buffer, "CreateRenderTargetView failed, HRESULT: 0x%08X", hr);
        Log(buffer, LOG_ERROR);
        return false;
    }

    Log("Render target view created successfully", LOG_INFO);
    return true;
}

bool VolumeRenderer::CreateDepthStencilView(int width, int height)
{
    char buffer[256];
    sprintf_s(buffer, "CreateDepthStencilView called: %dx%d", width, height);
    Log(buffer, LOG_INFO);

    // Create depth stencil texture
    D3D11_TEXTURE2D_DESC depthDesc = {};
    depthDesc.Width = width;
    depthDesc.Height = height;
    depthDesc.MipLevels = 1;
    depthDesc.ArraySize = 1;
    depthDesc.Format = DXGI_FORMAT_D24_UNORM_S8_UINT;
    depthDesc.SampleDesc.Count = 1;
    depthDesc.SampleDesc.Quality = 0;
    depthDesc.Usage = D3D11_USAGE_DEFAULT;
    depthDesc.BindFlags = D3D11_BIND_DEPTH_STENCIL;

    ComPtr<ID3D11Texture2D> depthStencil;
    HRESULT hr = m_device->CreateTexture2D(&depthDesc, nullptr, &depthStencil);
    if (FAILED(hr)) {
        sprintf_s(buffer, "CreateTexture2D for depth stencil failed, HRESULT: 0x%08X", hr);
        Log(buffer, LOG_ERROR);
        return false;
    }

    // Create depth stencil view
    D3D11_DEPTH_STENCIL_VIEW_DESC depthStencilViewDesc = {};
    depthStencilViewDesc.Format = depthDesc.Format;
    depthStencilViewDesc.ViewDimension = D3D11_DSV_DIMENSION_TEXTURE2D;
    depthStencilViewDesc.Texture2D.MipSlice = 0;

    hr = m_device->CreateDepthStencilView(depthStencil.Get(), &depthStencilViewDesc, &m_depthStencilView);
    if (FAILED(hr)) {
        sprintf_s(buffer, "CreateDepthStencilView failed, HRESULT: 0x%08X", hr);
        Log(buffer, LOG_ERROR);
        return false;
    }

    Log("Depth stencil view created successfully", LOG_INFO);
    return true;
}

bool VolumeRenderer::CreateShaders()
{
    Log("CreateShaders called", LOG_INFO);

    // Compile and create vertex shader
    Log("Compiling vertex shader...", LOG_INFO);
    ComPtr<ID3DBlob> vsBlob;
    ComPtr<ID3DBlob> errorBlob;

    HRESULT hr = D3DCompileFromFile(L"VertexShader.hlsl", nullptr, nullptr, "main", "vs_5_0",
        D3DCOMPILE_DEBUG, 0, &vsBlob, &errorBlob);
    if (FAILED(hr)) {
        char buffer[256];
        sprintf_s(buffer, "Vertex shader compilation failed, HRESULT: 0x%08X", hr);
        Log(buffer, LOG_ERROR);

        if (errorBlob) {
            Log("Shader compilation error:", LOG_ERROR);
            Log(static_cast<char*>(errorBlob->GetBufferPointer()), LOG_ERROR);
        }
        return false;
    }

    hr = m_device->CreateVertexShader(vsBlob->GetBufferPointer(), vsBlob->GetBufferSize(),
        nullptr, &m_vertexShader);
    if (FAILED(hr)) {
        char buffer[256];
        sprintf_s(buffer, "CreateVertexShader failed, HRESULT: 0x%08X", hr);
        Log(buffer, LOG_ERROR);
        return false;
    }

    // Create input layout
    Log("Creating input layout...", LOG_INFO);
    D3D11_INPUT_ELEMENT_DESC layout[] =
    {
        { "POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 0, D3D11_INPUT_PER_VERTEX_DATA, 0 },
        { "TEXCOORD", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 12, D3D11_INPUT_PER_VERTEX_DATA, 0 }
    };

    hr = m_device->CreateInputLayout(layout, 2, vsBlob->GetBufferPointer(), vsBlob->GetBufferSize(), &m_inputLayout);
    if (FAILED(hr)) {
        char buffer[256];
        sprintf_s(buffer, "CreateInputLayout failed, HRESULT: 0x%08X", hr);
        Log(buffer, LOG_ERROR);
        return false;
    }

    // Compile and create pixel shader
    Log("Compiling pixel shader...", LOG_INFO);
    ComPtr<ID3DBlob> psBlob;
    hr = D3DCompileFromFile(L"PixelShader.hlsl", nullptr, nullptr, "main", "ps_5_0",
        D3DCOMPILE_DEBUG, 0, &psBlob, &errorBlob);
    if (FAILED(hr)) {
        char buffer[256];
        sprintf_s(buffer, "Pixel shader compilation failed, HRESULT: 0x%08X", hr);
        Log(buffer, LOG_ERROR);

        if (errorBlob) {
            Log("Shader compilation error:", LOG_ERROR);
            Log(static_cast<char*>(errorBlob->GetBufferPointer()), LOG_ERROR);
        }
        return false;
    }

    hr = m_device->CreatePixelShader(psBlob->GetBufferPointer(), psBlob->GetBufferSize(),
        nullptr, &m_pixelShader);
    if (FAILED(hr)) {
        char buffer[256];
        sprintf_s(buffer, "CreatePixelShader failed, HRESULT: 0x%08X", hr);
        Log(buffer, LOG_ERROR);
        return false;
    }

    Log("Shaders created successfully", LOG_INFO);
    return true;
}

bool VolumeRenderer::CreateConstantBuffers()
{
    Log("CreateConstantBuffers called", LOG_INFO);

    // Create constant buffer for camera
    D3D11_BUFFER_DESC bufferDesc = {};
    bufferDesc.Usage = D3D11_USAGE_DEFAULT;
    bufferDesc.ByteWidth = sizeof(ConstantBuffer);
    bufferDesc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;

    HRESULT hr = m_device->CreateBuffer(&bufferDesc, nullptr, &m_constantBuffer);
    if (FAILED(hr)) {
        char buffer[256];
        sprintf_s(buffer, "CreateBuffer for constant buffer failed, HRESULT: 0x%08X", hr);
        Log(buffer, LOG_ERROR);
        return false;
    }

    // Create material buffer
    D3D11_BUFFER_DESC materialDesc = {};
    materialDesc.Usage = D3D11_USAGE_DYNAMIC;
    materialDesc.ByteWidth = sizeof(XMFLOAT4) * 256; // Space for all possible materials
    materialDesc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    materialDesc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    materialDesc.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;
    materialDesc.StructureByteStride = sizeof(XMFLOAT4);

    // Initialize with empty materials
    m_materials.resize(256, XMFLOAT4(0, 0, 0, 0));
    D3D11_SUBRESOURCE_DATA materialData = {};
    materialData.pSysMem = m_materials.data();

    hr = m_device->CreateBuffer(&materialDesc, &materialData, &m_materialBuffer);
    if (FAILED(hr)) {
        char buffer[256];
        sprintf_s(buffer, "CreateBuffer for material buffer failed, HRESULT: 0x%08X", hr);
        Log(buffer, LOG_ERROR);
        return false;
    }

    // Create shader resource view for material buffer
    D3D11_SHADER_RESOURCE_VIEW_DESC srvDesc = {};
    srvDesc.Format = DXGI_FORMAT_UNKNOWN;
    srvDesc.ViewDimension = D3D11_SRV_DIMENSION_BUFFER;
    srvDesc.Buffer.FirstElement = 0;
    srvDesc.Buffer.NumElements = 256;

    hr = m_device->CreateShaderResourceView(m_materialBuffer.Get(), &srvDesc, &m_materialSRV);
    if (FAILED(hr)) {
        char buffer[256];
        sprintf_s(buffer, "CreateShaderResourceView for material buffer failed, HRESULT: 0x%08X", hr);
        Log(buffer, LOG_ERROR);
        return false;
    }

    Log("Constant buffers created successfully", LOG_INFO);
    return true;
}

bool VolumeRenderer::CreateSamplers()
{
    Log("CreateSamplers called", LOG_INFO);

    // Create sampler state
    D3D11_SAMPLER_DESC samplerDesc = {};
    samplerDesc.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    samplerDesc.AddressU = D3D11_TEXTURE_ADDRESS_CLAMP;
    samplerDesc.AddressV = D3D11_TEXTURE_ADDRESS_CLAMP;
    samplerDesc.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    samplerDesc.ComparisonFunc = D3D11_COMPARISON_NEVER;
    samplerDesc.MinLOD = 0;
    samplerDesc.MaxLOD = D3D11_FLOAT32_MAX;

    HRESULT hr = m_device->CreateSamplerState(&samplerDesc, &m_volumeSampler);
    if (FAILED(hr)) {
        char buffer[256];
        sprintf_s(buffer, "CreateSamplerState failed, HRESULT: 0x%08X", hr);
        Log(buffer, LOG_ERROR);
        return false;
    }

    Log("Samplers created successfully", LOG_INFO);
    return true;
}

bool VolumeRenderer::CreateVolumeTexture(const unsigned char* data)
{
    Log("CreateVolumeTexture called", LOG_INFO);

    if (!m_device) {
        Log("Cannot create volume texture - device is null", LOG_ERROR);
        return false;
    }

    if (m_volumeWidth <= 0 || m_volumeHeight <= 0 || m_volumeDepth <= 0) {
        char buffer[256];
        sprintf_s(buffer, "Invalid volume dimensions: %dx%dx%d", m_volumeWidth, m_volumeHeight, m_volumeDepth);
        Log(buffer, LOG_ERROR);
        return false;
    }

    if (!data) {
        Log("Volume data pointer is null", LOG_ERROR);
        return false;
    }

    // Create a 3D texture for volume data
    D3D11_TEXTURE3D_DESC texDesc = {};
    texDesc.Width = m_volumeWidth;
    texDesc.Height = m_volumeHeight;
    texDesc.Depth = m_volumeDepth;
    texDesc.MipLevels = 1;
    texDesc.Format = DXGI_FORMAT_R8_UNORM;
    texDesc.Usage = D3D11_USAGE_DEFAULT;
    texDesc.BindFlags = D3D11_BIND_SHADER_RESOURCE;

    // Set up initial data
    D3D11_SUBRESOURCE_DATA initialData = {};
    initialData.pSysMem = data;
    initialData.SysMemPitch = m_volumeWidth;
    initialData.SysMemSlicePitch = m_volumeWidth * m_volumeHeight;

    // Create the texture
    char buffer[256];
    sprintf_s(buffer, "Creating 3D texture: %dx%dx%d", m_volumeWidth, m_volumeHeight, m_volumeDepth);
    Log(buffer, LOG_INFO);

    HRESULT hr = m_device->CreateTexture3D(&texDesc, &initialData, &m_volumeTexture);
    if (FAILED(hr)) {
        sprintf_s(buffer, "CreateTexture3D failed, HRESULT: 0x%08X", hr);
        Log(buffer, LOG_ERROR);
        return false;
    }

    // Create shader resource view
    D3D11_SHADER_RESOURCE_VIEW_DESC srvDesc = {};
    srvDesc.Format = texDesc.Format;
    srvDesc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE3D;
    srvDesc.Texture3D.MipLevels = 1;

    hr = m_device->CreateShaderResourceView(m_volumeTexture.Get(), &srvDesc, &m_volumeSRV);
    if (FAILED(hr)) {
        sprintf_s(buffer, "CreateShaderResourceView failed, HRESULT: 0x%08X", hr);
        Log(buffer, LOG_ERROR);
        return false;
    }

    Log("Volume texture created successfully", LOG_INFO);
    return true;
}

bool VolumeRenderer::CreateLabelTexture(const unsigned char* data)
{
    Log("CreateLabelTexture called", LOG_INFO);

    if (!m_device) {
        Log("Cannot create label texture - device is null", LOG_ERROR);
        return false;
    }

    if (m_volumeWidth <= 0 || m_volumeHeight <= 0 || m_volumeDepth <= 0) {
        char buffer[256];
        sprintf_s(buffer, "Invalid volume dimensions: %dx%dx%d", m_volumeWidth, m_volumeHeight, m_volumeDepth);
        Log(buffer, LOG_ERROR);
        return false;
    }

    if (!data) {
        Log("Label data pointer is null", LOG_ERROR);
        return false;
    }

    D3D11_TEXTURE3D_DESC texDesc = {};
    texDesc.Width = m_volumeWidth;
    texDesc.Height = m_volumeHeight;
    texDesc.Depth = m_volumeDepth;
    texDesc.MipLevels = 1;
    texDesc.Format = DXGI_FORMAT_R8_UNORM;
    texDesc.Usage = D3D11_USAGE_DEFAULT;
    texDesc.BindFlags = D3D11_BIND_SHADER_RESOURCE;

    // Set up initial data
    D3D11_SUBRESOURCE_DATA initialData = {};
    initialData.pSysMem = data;
    initialData.SysMemPitch = m_volumeWidth;
    initialData.SysMemSlicePitch = m_volumeWidth * m_volumeHeight;

    // Create the texture
    char buffer[256];
    sprintf_s(buffer, "Creating 3D label texture: %dx%dx%d", m_volumeWidth, m_volumeHeight, m_volumeDepth);
    Log(buffer, LOG_INFO);

    HRESULT hr = m_device->CreateTexture3D(&texDesc, &initialData, &m_labelTexture);
    if (FAILED(hr)) {
        sprintf_s(buffer, "CreateTexture3D for labels failed, HRESULT: 0x%08X", hr);
        Log(buffer, LOG_ERROR);
        return false;
    }

    D3D11_SHADER_RESOURCE_VIEW_DESC srvDesc = {};
    srvDesc.Format = texDesc.Format;
    srvDesc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE3D;
    srvDesc.Texture3D.MipLevels = 1;

    hr = m_device->CreateShaderResourceView(m_labelTexture.Get(), &srvDesc, &m_labelSRV);
    if (FAILED(hr)) {
        sprintf_s(buffer, "CreateShaderResourceView for labels failed, HRESULT: 0x%08X", hr);
        Log(buffer, LOG_ERROR);
        return false;
    }

    Log("Label texture created successfully", LOG_INFO);
    return true;
}

void VolumeRenderer::SetupViewport(int width, int height)
{
    char buffer[256];
    sprintf_s(buffer, "SetupViewport: %dx%d", width, height);
    Log(buffer, LOG_INFO);

    // Set up the viewport
    D3D11_VIEWPORT viewport = {};
    viewport.Width = static_cast<float>(width);
    viewport.Height = static_cast<float>(height);
    viewport.MinDepth = 0.0f;
    viewport.MaxDepth = 1.0f;
    viewport.TopLeftX = 0.0f;
    viewport.TopLeftY = 0.0f;

    m_context->RSSetViewports(1, &viewport);
}

void VolumeRenderer::UpdateConstantBuffers()
{
    if (!m_context || !m_constantBuffer) {
        Log("Cannot update constant buffers - context or buffer is null", LOG_ERROR);
        return;
    }

    // Create view and projection matrices
    float aspectRatio = static_cast<float>(m_width) / static_cast<float>(m_height);

    XMMATRIX world = XMMatrixIdentity();
    XMMATRIX view = XMMatrixLookAtLH(
        XMLoadFloat3(&m_cameraPosition),
        XMLoadFloat3(&m_focusPoint),
        XMLoadFloat3(&m_upVector));
    XMMATRIX projection = XMMatrixPerspectiveFovLH(XM_PIDIV4, aspectRatio, 0.1f, 100.0f);

    // Set up constant buffer data
    ConstantBuffer cb;
    cb.worldMatrix = XMMatrixTranspose(world);
    cb.viewMatrix = XMMatrixTranspose(view);
    cb.projectionMatrix = XMMatrixTranspose(projection);
    cb.cameraPosition = m_cameraPosition;
    cb.padding = 0.0f;

    // Update constant buffer
    m_context->UpdateSubresource(m_constantBuffer.Get(), 0, nullptr, &cb, 0, 0);

    // Create and update render parameters buffer
    ComPtr<ID3D11Buffer> renderParamsBuffer;
    D3D11_BUFFER_DESC bufferDesc = {};
    bufferDesc.Usage = D3D11_USAGE_DEFAULT;
    bufferDesc.ByteWidth = sizeof(RenderParamsBuffer);
    bufferDesc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;

    HRESULT hr = m_device->CreateBuffer(&bufferDesc, nullptr, &renderParamsBuffer);
    if (SUCCEEDED(hr)) {
        RenderParamsBuffer params;
        params.opacity = m_opacity;
        params.brightness = m_brightness;
        params.contrast = m_contrast;
        params.renderMode = m_renderMode;
        params.volumeScale = XMFLOAT4(1.0f, 1.0f, 1.0f, 1.0f);
        params.showLabels = m_showLabels ? 1 : 0;
        params.padding[0] = params.padding[1] = 0.0f;

        m_context->UpdateSubresource(renderParamsBuffer.Get(), 0, nullptr, &params, 0, 0);
        m_context->PSSetConstantBuffers(1, 1, renderParamsBuffer.GetAddressOf());
    }
    else {
        char buffer[256];
        sprintf_s(buffer, "Failed to create render params buffer, HRESULT: 0x%08X", hr);
        Log(buffer, LOG_ERROR);
    }

    // Set material buffer
    if (m_materialSRV) {
        m_context->PSSetShaderResources(2, 1, m_materialSRV.GetAddressOf());
    }
    else {
        Log("Material shader resource view is null", LOG_WARNING);
    }
}

// Getter/setter methods
void VolumeRenderer::SetOpacity(float opacity) {
    m_opacity = max(0.0f, min(1.0f, opacity));
}

void VolumeRenderer::SetBrightness(float brightness) {
    m_brightness = max(-1.0f, min(1.0f, brightness));
}

void VolumeRenderer::SetContrast(float contrast) {
    m_contrast = max(0.1f, min(5.0f, contrast));
}

void VolumeRenderer::SetRenderMode(int mode) {
    m_renderMode = mode;
}

void VolumeRenderer::SetShowLabels(bool show) {
    m_showLabels = show;
}
