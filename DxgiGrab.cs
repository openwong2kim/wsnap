// wsnap — macOS-style screen capture for Windows.
// Copyright (C) 2026 openwong2kim and wsnap contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License version 3, as published
// by the Free Software Foundation.
//
// This program is distributed in the hope that it will be useful, but
// WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY
// or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License
// for more details. You should have received a copy of the GNU General
// Public License along with this program. If not, see
// <https://www.gnu.org/licenses/>.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Wsnap;

/// <summary>
/// GPU-path one-shot screen grab via DXGI Desktop Duplication. GDI's CopyFromScreen walks the
/// desktop through a CPU BitBlt — on a 4K/multi-monitor desktop that's the visible "pause"
/// between the hotkey and the overlay. Desktop duplication instead hands us the exact frame
/// the compositor already has on the GPU; the only CPU work is one staging-texture readback.
/// Unlike Windows.Graphics.Capture it draws no yellow capture border and needs no WinRT
/// projection (the 25 MB SDK assembly the v1.8 diet removed).
///
/// This is used for the big one-shot grabs only (overlay freeze, full-screen delivery):
/// duplication setup costs a few ms per output, which is noise once per capture but would be
/// pure overhead for GIF/video recorders re-grabbing 12×/second — those stay on GDI.
///
/// Every failure path returns null and the caller falls back to GDI: duplication is
/// unavailable on RDP sessions, blocked for DRM-protected content, and rotated outputs are
/// deliberately not handled (rare, and GDI handles them for free). After a few consecutive
/// failures the whole path disables itself for the session so unsupported environments don't
/// pay a failed-attempt tax on every capture.
/// </summary>
public static class DxgiGrab
{
    private static int _consecutiveFailures;
    private const int DisableAfterFailures = 3;

    /// <summary>Grab a virtual-desktop rect (physical device px, same space as CopyFromScreen).
    /// Returns null when the DXGI path can't serve it — the caller must fall back to GDI.</summary>
    public static Bitmap? TryGrab(int x, int y, int w, int h)
    {
        if (Volatile.Read(ref _consecutiveFailures) >= DisableAfterFailures) return null;

        Bitmap? bmp = null;
        try
        {
            bmp = GrabCore(x, y, w, h);
            if (bmp != null) Volatile.Write(ref _consecutiveFailures, 0);
            else Interlocked.Increment(ref _consecutiveFailures);
            return bmp;
        }
        catch (Exception ex)
        {
            bmp?.Dispose();
            // Log the first failure for diagnosability, then count quietly toward disable.
            if (Interlocked.Increment(ref _consecutiveFailures) == 1)
                CrashLog.Write("dxgi-grab", ex);
            return null;
        }
    }

    private static Bitmap? GrabCore(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return null;

        var request = new Rectangle(x, y, w, h);
        Result r = DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory);
        if (r.Failure || factory == null) return null;

        Bitmap? bmp = null;
        bool anyOutput = false;
        try
        {
            for (uint ai = 0; factory.EnumAdapters1(ai, out IDXGIAdapter1? adapter).Success; ai++)
            {
                using (adapter)
                {
                    ID3D11Device? device = null;
                    try
                    {
                        for (uint oi = 0; adapter!.EnumOutputs(oi, out IDXGIOutput? output).Success; oi++)
                        {
                            using (output)
                            {
                                var desc = output!.Description;
                                if (!desc.AttachedToDesktop) continue;

                                var outRect = Rectangle.FromLTRB(
                                    desc.DesktopCoordinates.Left, desc.DesktopCoordinates.Top,
                                    desc.DesktopCoordinates.Right, desc.DesktopCoordinates.Bottom);
                                var isect = Rectangle.Intersect(request, outRect);
                                if (isect.IsEmpty) continue;

                                // Rotated outputs deliver a rotated texture; let GDI handle those.
                                if (desc.Rotation != ModeRotation.Identity && desc.Rotation != ModeRotation.Unspecified)
                                    return Fail(ref bmp);

                                device ??= CreateDevice(adapter);
                                if (device == null) return Fail(ref bmp);

                                // GDI matched the frozen backdrop's gap behaviour: uncovered
                                // virtual-desktop areas (L-shaped monitor layouts) are black.
                                if (bmp == null)
                                {
                                    bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                                    using var g = Graphics.FromImage(bmp);
                                    g.Clear(Color.Black);
                                }

                                if (!CopyOutputRegion(device, output, outRect, isect, request, bmp))
                                    return Fail(ref bmp);
                                anyOutput = true;
                            }
                        }
                    }
                    finally { device?.Dispose(); }
                }
            }
        }
        finally { factory.Dispose(); }

        if (!anyOutput) return Fail(ref bmp);
        return bmp;
    }

    private static Bitmap? Fail(ref Bitmap? bmp)
    {
        bmp?.Dispose();
        bmp = null;
        return null;
    }

    private static ID3D11Device? CreateDevice(IDXGIAdapter1 adapter)
    {
        Result r = D3D11.D3D11CreateDevice(
            adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            null, out ID3D11Device? device);
        return r.Success ? device : null;
    }

    /// <summary>Duplicate one output, acquire the current desktop frame, and copy the
    /// intersecting region into <paramref name="dst"/> at request-relative coordinates.</summary>
    private static bool CopyOutputRegion(
        ID3D11Device device, IDXGIOutput output,
        Rectangle outRect, Rectangle isect, Rectangle request, Bitmap dst)
    {
        using IDXGIOutput1? output1 = output.QueryInterfaceOrNull<IDXGIOutput1>();
        if (output1 == null) return false;

        IDXGIOutputDuplication? dup;
        try { dup = output1.DuplicateOutput(device); }   // throws when duplication is unavailable (RDP, DRM, driver)
        catch { return false; }
        if (dup == null) return false;

        using (dup)
        {
            // Only trust frames the compositor actually PRESENTED since duplication started
            // (LastPresentTime != 0). The initial/mouse-only frames reference a desktop texture
            // that some drivers leave black — verified on this very machine. On a live desktop a
            // present arrives within a frame or two (the user just pressed a hotkey); on a truly
            // static screen we hit the short deadline and the caller falls back to GDI.
            IDXGIResource? resource = null;
            long deadline = Environment.TickCount64 + 150;
            while (resource == null)
            {
                long remain = deadline - Environment.TickCount64;
                if (remain <= 0) return false;
                Result fr = dup.AcquireNextFrame((uint)Math.Min(remain, 60), out OutduplFrameInfo info, out resource);
                if (fr.Failure)
                {
                    resource?.Dispose(); resource = null;
                    if (fr == Vortice.DXGI.ResultCode.WaitTimeout) continue;
                    return false;
                }
                if (info.LastPresentTime == 0)
                {
                    resource.Dispose(); resource = null;
                    try { dup.ReleaseFrame(); } catch { }
                }
            }

            try
            {
                using ID3D11Texture2D? frame = resource.QueryInterfaceOrNull<ID3D11Texture2D>();
                if (frame == null) return false;

                var fd = frame.Description;
                var staging = new Texture2DDescription
                {
                    Width = fd.Width,
                    Height = fd.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = fd.Format,          // B8G8R8A8_UNorm from the compositor
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    BindFlags = BindFlags.None,
                };
                using ID3D11Texture2D cpuTex = device.CreateTexture2D(staging);
                var ctx = device.ImmediateContext;
                ctx.CopyResource(cpuTex, frame);

                var mapped = ctx.Map(cpuTex, 0, MapMode.Read);
                try
                {
                    CopyRows(mapped.DataPointer, mapped.RowPitch, outRect, isect, request, dst);
                }
                finally { ctx.Unmap(cpuTex, 0); }
                return true;
            }
            finally
            {
                resource.Dispose();
                try { dup.ReleaseFrame(); } catch { /* frame already released */ }
            }
        }
    }

    /// <summary>Row-copy BGRA pixels from the mapped output texture into the destination bitmap,
    /// forcing alpha opaque: compositor buffers carry meaningless alpha under layered windows,
    /// and downstream (PNG save, loupe sampling) expects an opaque screenshot.</summary>
    private static unsafe void CopyRows(
        IntPtr src, uint srcPitch, Rectangle outRect, Rectangle isect, Rectangle request, Bitmap dst)
    {
        var dstRect = new Rectangle(isect.X - request.X, isect.Y - request.Y, isect.Width, isect.Height);
        BitmapData data = dst.LockBits(dstRect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int srcX = isect.X - outRect.X;   // region origin inside this output's texture
            int srcY = isect.Y - outRect.Y;
            for (int row = 0; row < isect.Height; row++)
            {
                uint* s = (uint*)((byte*)src + (srcY + row) * srcPitch) + srcX;
                uint* d = (uint*)((byte*)data.Scan0 + row * data.Stride);
                for (int i = 0; i < isect.Width; i++)
                    d[i] = s[i] | 0xFF000000u;
            }
        }
        finally { dst.UnlockBits(data); }
    }
}
