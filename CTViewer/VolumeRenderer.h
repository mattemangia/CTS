
// VolumeRenderer.h
#pragma once
extern void Log(const char* message, int severity);

#include <d3d11.h>
#include <DirectXMath.h>
#include <wrl/client.h>
#include <vector>
#include <memory>
using namespace DirectX;
using Microsoft::WRL::ComPtr;

class VolumeRenderer
{
public:
    VolumeRenderer();
    ~VolumeRenderer();

    bool Initialize(HWND hwnd, int width, int height);
    void Shutdown();
    bool LoadVolumeData(const unsigned char* data, int width, int height, int depth, float voxelSize);
    bool LoadLabelData(const unsigned char* data, int width, int height, int depth);
    void UpdateMaterials(const int* colors, int count);
    void Render();
    void Resize(int width, int height);
    void RotateCamera(float deltaX, float deltaY);
    void ZoomCamera(float delta);
    void ResetCamera();

    // Getter/setter methods
    void SetOpacity(float opacity);
    void SetBrightness(float brightness);
    void SetContrast(float contrast);
    void SetRenderMode(int mode);
    void SetShowLabels(bool show);

private:
    // DirectX resources
    ComPtr<ID3D11Device> m_device;
    ComPtr<ID3D11DeviceContext> m_context;
    ComPtr<IDXGISwapChain> m_swapChain;
    ComPtr<ID3D11RenderTargetView> m_renderTargetView;
    ComPtr<ID3D11DepthStencilView> m_depthStencilView;
    ComPtr<ID3D11VertexShader> m_vertexShader;
    ComPtr<ID3D11PixelShader> m_pixelShader;
    ComPtr<ID3D11InputLayout> m_inputLayout;
    ComPtr<ID3D11Buffer> m_vertexBuffer;
    ComPtr<ID3D11Buffer> m_indexBuffer;
    ComPtr<ID3D11Buffer> m_constantBuffer;
    ComPtr<ID3D11Buffer> m_materialBuffer;
    ComPtr<ID3D11ShaderResourceView> m_materialSRV;

    // Volume texture resources
    ComPtr<ID3D11Texture3D> m_volumeTexture;
    ComPtr<ID3D11ShaderResourceView> m_volumeSRV;
    ComPtr<ID3D11Texture3D> m_labelTexture;
    ComPtr<ID3D11ShaderResourceView> m_labelSRV;
    ComPtr<ID3D11SamplerState> m_volumeSampler;

    // Volume data properties
    int m_volumeWidth;
    int m_volumeHeight;
    int m_volumeDepth;
    float m_voxelSize;

    // Viewport dimensions
    int m_width;
    int m_height;

    // Camera parameters - make sure these have proper XMFLOAT3 type
    XMFLOAT3 m_cameraPosition;
    XMFLOAT3 m_focusPoint;
    XMFLOAT3 m_upVector;
    float m_cameraTheta;
    float m_cameraPhi;
    float m_cameraRadius;

    // Rendering parameters
    float m_opacity;
    float m_brightness;
    float m_contrast;
    int m_renderMode;
    bool m_showLabels;
    std::vector<XMFLOAT4> m_materials;


    // Helper methods
    bool CreateDeviceAndSwapChain(HWND hwnd);
    bool CreateRenderTargetView();
    bool CreateDepthStencilView(int width, int height);
    bool CreateShaders();
    bool CreateConstantBuffers();
    bool CreateSamplers();
    bool CreateVolumeTexture(const unsigned char* data = nullptr);
    bool CreateLabelTexture(const unsigned char* data = nullptr);
    void SetupViewport(int width, int height);
    void UpdateConstantBuffers();
};

