//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using MediaFoundation;
using MediaFoundation.Misc;
using MediaFoundation.ReadWrite;

public class WmvWriter : IDisposable
{
    private IMFSinkWriter _sinkWriter;
    private int _streamIndex;
    private long _frameDuration;   // 100-ns units
    private long _nextTimestamp;
    private bool _initialized;
    private bool _disposed;

    /// <summary>Alias matching earlier API.</summary>
    public void Close() => Dispose();

    /// <summary>
    /// Initializes a video-only WMV writer.
    /// </summary>
    public WmvWriter(string filename, int width, int height, int fps, int quality)
    {
        if (width <= 0) throw new ArgumentException("Width must be > 0", nameof(width));
        if (height <= 0) throw new ArgumentException("Height must be > 0", nameof(height));
        if (fps <= 0) throw new ArgumentException("FPS must be > 0", nameof(fps));

        // 1) Startup MF
        hrCheck(MFExtern.MFStartup(0x00020070, MFStartup.Full));

        // Ensure dimensions are even numbers (required by many codecs)
        width = width % 2 == 0 ? width : width + 1;
        height = height % 2 == 0 ? height : height + 1;

        // Cap frame rate to a more compatible value if needed
        fps = Math.Min(fps, 30);

        // 2) Create Sink Writer (ASF/WMV via extension)
        hrCheck(MFExtern.MFCreateSinkWriterFromURL(filename, null, null, out _sinkWriter));

        // 3) Configure OUTPUT media type (WMV9/VC-1 instead of WMV3)
        hrCheck(MFExtern.MFCreateMediaType(out IMFMediaType outType));
        outType.SetGUID(MFAttributesClsid.MF_MT_MAJOR_TYPE, MFMediaType.Video);

        // Use WVC1 (VC-1/WMV9) codec which has better compatibility
        var wvc1 = new Guid("31435657-0000-0010-8000-00AA00389B71");
        outType.SetGUID(MFAttributesClsid.MF_MT_SUBTYPE, wvc1);

        // Use lower bitrate for better compatibility
        uint bitrate = (uint)(quality <= 3 ? 800_000 :
                        quality <= 7 ? 1_200_000 :
                                      2_000_000);
        outType.SetUINT32(MFAttributesClsid.MF_MT_AVG_BITRATE, (int)bitrate);

        // Progressive
        outType.SetUINT32(MFAttributesClsid.MF_MT_INTERLACE_MODE, 2);

        // Frame size: pack width|height into UINT64
        ulong packedSize = Pack2UINT32AsUINT64((uint)width, (uint)height);
        outType.SetUINT64(MFAttributesClsid.MF_MT_FRAME_SIZE, (long)packedSize);

        // Frame rate: pack fps|1 into UINT64
        ulong packedRate = Pack2UINT32AsUINT64((uint)fps, 1);
        outType.SetUINT64(MFAttributesClsid.MF_MT_FRAME_RATE, (long)packedRate);

        // Pixel aspect: 1:1
        ulong packedPAR = Pack2UINT32AsUINT64(1, 1);
        outType.SetUINT64(MFAttributesClsid.MF_MT_PIXEL_ASPECT_RATIO, (long)packedPAR);

        hrCheck(_sinkWriter.AddStream(outType, out _streamIndex));
        Marshal.ReleaseComObject(outType);

        // 4) Configure INPUT media type (uncompressed RGB32)
        hrCheck(MFExtern.MFCreateMediaType(out IMFMediaType inType));
        inType.SetGUID(MFAttributesClsid.MF_MT_MAJOR_TYPE, MFMediaType.Video);
        inType.SetGUID(MFAttributesClsid.MF_MT_SUBTYPE, MFMediaType.RGB32);
        inType.SetUINT32(MFAttributesClsid.MF_MT_INTERLACE_MODE, 2); // Progressive
        inType.SetUINT32(MFAttributesClsid.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
        inType.SetUINT64(MFAttributesClsid.MF_MT_FRAME_SIZE, (long)packedSize);
        inType.SetUINT64(MFAttributesClsid.MF_MT_FRAME_RATE, (long)packedRate);
        inType.SetUINT64(MFAttributesClsid.MF_MT_PIXEL_ASPECT_RATIO, (long)packedPAR);

        hrCheck(_sinkWriter.SetInputMediaType(_streamIndex, inType, null));
        Marshal.ReleaseComObject(inType);

        // 5) Begin writing
        hrCheck(_sinkWriter.BeginWriting());
        _frameDuration = 10_000_000L / fps;
        _nextTimestamp = 0;
        _initialized = true;
    }
    /// <summary>Adds one Bitmap frame (converted to 32bpp ARGB) to the WMV.</summary>
    public void AddFrame(Bitmap bitmap)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WmvWriter));
        if (!_initialized) throw new InvalidOperationException("Not initialized");

        // Convert to RGB32 if needed
        Bitmap frame = bitmap;
        bool needsDispose = false;

        try
        {
            // Create a properly sized frame with the correct pixel format
            if (bitmap.PixelFormat != PixelFormat.Format32bppRgb ||
                bitmap.Width % 2 != 0 || bitmap.Height % 2 != 0)
            {
                needsDispose = true;
                // Ensure even dimensions to match what we set in the constructor
                int width = bitmap.Width % 2 == 0 ? bitmap.Width : bitmap.Width + 1;
                int height = bitmap.Height % 2 == 0 ? bitmap.Height : bitmap.Height + 1;

                frame = new Bitmap(width, height, PixelFormat.Format32bppRgb);
                using (var g = Graphics.FromImage(frame))
                {
                    g.DrawImage(bitmap, 0, 0, frame.Width, frame.Height);
                }
            }

            // Lock bits
            var rect = new Rectangle(0, 0, frame.Width, frame.Height);
            var data = frame.LockBits(rect, ImageLockMode.ReadOnly, frame.PixelFormat);
            try
            {
                int stride = data.Stride;
                int bufferSize = stride * frame.Height;

                // Create MF memory buffer
                hrCheck(MFExtern.MFCreateMemoryBuffer(bufferSize, out IMFMediaBuffer buf));
                hrCheck(buf.Lock(out IntPtr dest, out _, out _));
                byte[] buffer = new byte[stride];
                for (int y = 0; y < frame.Height; y++)
                {
                    IntPtr srcPtr = data.Scan0 + y * stride;
                    IntPtr dstPtr = dest + y * stride;
                    Marshal.Copy(srcPtr, buffer, 0, stride);
                    Marshal.Copy(buffer, 0, dstPtr, stride);
                }
                buf.Unlock();
                buf.SetCurrentLength(bufferSize);

                // Create sample
                hrCheck(MFExtern.MFCreateSample(out IMFSample sample));
                sample.AddBuffer(buf);
                Marshal.ReleaseComObject(buf);

                // Timestamp & duration
                sample.SetSampleTime(_nextTimestamp);
                sample.SetSampleDuration(_frameDuration);

                // Write
                hrCheck(_sinkWriter.WriteSample(_streamIndex, sample));
                Marshal.ReleaseComObject(sample);

                _nextTimestamp += _frameDuration;
            }
            finally
            {
                frame.UnlockBits(data);
            }
        }
        finally
        {
            if (needsDispose && frame != null) frame.Dispose();
        }
    }

    /// <summary>Finalize and release all resources.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_sinkWriter != null)
        {
            _sinkWriter.Finalize_();
            Marshal.ReleaseComObject(_sinkWriter);
            _sinkWriter = null;
        }
        MFExtern.MFShutdown();
    }

    // Helper to pack two 32-bit values into one 64-bit for SetUINT64
    private static ulong Pack2UINT32AsUINT64(uint high, uint low) =>
        ((ulong)high << 32) | low; // :contentReference[oaicite:5]{index=5}

    // Helper to check HRESULTs
    private static void hrCheck(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }
}
