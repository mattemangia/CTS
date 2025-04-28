using System.Drawing.Imaging;
using System.Drawing;

using System;

/// <summary>
/// Improved FastBitmap class with better synchronization and error handling
/// </summary>
public sealed class FastBitmap : IDisposable
{
    private readonly Bitmap _bitmap;
    private BitmapData _bitmapData;
    private byte[] _pixelData;
    private bool _disposed;
    private readonly int _width;
    private readonly int _height;
    private int _stride;
    private readonly int _bytesPerPixel;

    public int Width => _width;
    public int Height => _height;

    public FastBitmap(Bitmap bitmap)
    {
        _bitmap = bitmap ?? throw new ArgumentNullException(nameof(bitmap));
        _width = bitmap.Width;
        _height = bitmap.Height;

        // Calculate bytes per pixel based on format
        switch (bitmap.PixelFormat)
        {
            case PixelFormat.Format24bppRgb:
                _bytesPerPixel = 3;
                break;
            case PixelFormat.Format32bppRgb:
            case PixelFormat.Format32bppArgb:
            case PixelFormat.Format32bppPArgb:
                _bytesPerPixel = 4;
                break;
            default:
                throw new ArgumentException($"Unsupported pixel format: {bitmap.PixelFormat}");
        }
    }

    public void LockBits()
    {
        Rectangle rect = new Rectangle(0, 0, _width, _height);
        _bitmapData = _bitmap.LockBits(rect, ImageLockMode.ReadOnly, _bitmap.PixelFormat);
        _stride = _bitmapData.Stride;

        // Allocate buffer and copy bitmap data
        _pixelData = new byte[_stride * _height];
        System.Runtime.InteropServices.Marshal.Copy(_bitmapData.Scan0, _pixelData, 0, _pixelData.Length);
    }

    public byte GetGrayValue(int x, int y)
    {
        if (x < 0 || x >= _width || y < 0 || y >= _height)
            throw new ArgumentOutOfRangeException($"Coordinates ({x},{y}) out of bounds");

        if (_pixelData == null)
            throw new InvalidOperationException("Bitmap not locked. Call LockBits first.");

        int index = y * _stride + x * _bytesPerPixel;

        // Calculate grayscale using standard formula
        if (_bytesPerPixel >= 3)
        {
            byte b = _pixelData[index];
            byte g = _pixelData[index + 1];
            byte r = _pixelData[index + 2];

            // Standard grayscale conversion formula
            return (byte)(0.299 * r + 0.587 * g + 0.114 * b);
        }

        // For formats we don't handle specifically
        return _pixelData[index];
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_bitmapData != null)
            {
                _bitmap.UnlockBits(_bitmapData);
                _bitmapData = null;
            }

            _pixelData = null;
            _disposed = true;
        }
    }
}
