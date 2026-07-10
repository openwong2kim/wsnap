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
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Wsnap;

/// <summary>
/// Scroll capture (best-effort). Picks a region, programmatically wheel-scrolls the window
/// under it, grabs frames, and stitches them by detecting vertical overlap between consecutive
/// frames. v2 hardening: a two-component row signature (brightness + chroma) so equal-brightness
/// rows no longer collide, and a confidence gate that rejects low-overlap matches (smooth-scroll
/// lag, parallax) instead of forcing a noisy shift that corrupts the stitch — so when content
/// can't be aligned, the result is a shorter but clean image rather than a garbled one. Still
/// best for text/web; stops automatically when no new content appears.
/// UI-framework-agnostic since Phase 4 (plain Rectangle region + RecorderUi badge).
/// </summary>
public sealed class ScrollCapture
{
    private const int MaxSteps = 60;
    private const int MaxHeightPx = 20000;

    private readonly Rectangle _r;
    private readonly Action<string> _onSaved;
    private readonly List<Bitmap> _strips = new();
    private IRecorderBadge? _badge;
    private bool _stop;

    public ScrollCapture(Rectangle region, Action<string> onSaved)
    {
        _r = region;
        _onSaved = onSaved;
    }

    public async void Start()
    {
        if (_r.Width < 4 || _r.Height < 8) return;
        _badge = RecorderUi.TryShow(L.T("scroll.recording"), 0xF01E6FEB);
        if (_badge != null) _badge.Clicked += () => _stop = true;

        int cx = _r.X + _r.Width / 2, cy = _r.Y + _r.Height / 2;
        SetCursorPos(cx, cy);
        await Task.Delay(200);

        RowSig[]? prevSig = null;
        int totalH = 0, noProgress = 0;

        try
        {
            for (int step = 0; step < MaxSteps && !_stop; step++)
            {
                using var frame = ScreenGrab.Grab(_r.X, _r.Y, _r.Width, _r.Height);
                RowSig[] sig = RowSignature(frame);

                if (prevSig == null)
                {
                    _strips.Add((Bitmap)frame.Clone());
                    totalH += frame.Height;
                }
                else
                {
                    var (shift, residual) = BestShift(prevSig, sig);
                    // Reject low-confidence overlaps (smooth-scroll lag / parallax / no movement)
                    // instead of forcing a noisy shift that would corrupt the stitch.
                    if (shift < 3 || residual > 0.12) { if (++noProgress >= 2) break; }
                    else
                    {
                        noProgress = 0;
                        var strip = frame.Clone(
                            new Rectangle(0, frame.Height - shift, frame.Width, shift), frame.PixelFormat);
                        _strips.Add(strip);
                        totalH += shift;
                        if (totalH >= MaxHeightPx) break;
                    }
                }
                prevSig = sig;

                SetCursorPos(cx, cy);
                mouse_event(MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)(-120 * 3)), UIntPtr.Zero);
                await Task.Delay(140);   // let it repaint
            }
        }
        catch (Exception ex) { CrashLog.Write("scroll-capture", ex); }

        _badge?.Close(); _badge = null;
        Finish(totalH);
    }

    private void Finish(int totalH)
    {
        if (_strips.Count == 0) { Toast.Show(L.T("scroll.canceled")); return; }
        try
        {
            using var tall = new Bitmap(_r.Width, Math.Max(1, totalH), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(tall))
            {
                int y = 0;
                foreach (var s in _strips) { g.DrawImage(s, 0, y); y += s.Height; }
            }
            string path = CaptureStore.SaveBitmap(tall);
            CrashLog.Telemetry("scroll-saved");
            _onSaved(path);
        }
        catch (Exception ex) { CrashLog.Write("scroll-stitch", ex); Toast.Show(L.T("scroll.saveFail")); }
        finally { foreach (var s in _strips) s.Dispose(); _strips.Clear(); }
    }

    private readonly struct RowSig
    {
        public readonly int Br;
        public readonly int Cv;
        public RowSig(int br, int cv) { Br = br; Cv = cv; }
    }

    /// <summary>Per-row signature: total brightness <em>and</em> a colour-variance term, so two
    /// rows with identical brightness but different content no longer collide (the v1 single-sum
    /// signature matched such rows as equal, corrupting the overlap estimate).</summary>
    private static RowSig[] RowSignature(Bitmap bmp)
    {
        int h = bmp.Height, w = bmp.Width;
        var sig = new RowSig[h];
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            unsafe
            {
                byte* basePtr = (byte*)data.Scan0;
                int step = Math.Max(1, w / 64) * 4;   // sample ~64 columns
                for (int y = 0; y < h; y++)
                {
                    byte* row = basePtr + y * stride;
                    int br = 0, cv = 0;
                    for (int x = 0; x < w * 4; x += step)
                    {
                        byte b = row[x], g = row[x + 1], r = row[x + 2];
                        br += r + g + b;
                        cv += Math.Abs(r - g) + Math.Abs(g - b);   // chroma spread
                    }
                    sig[y] = new RowSig(br, cv);
                }
            }
        }
        finally { bmp.UnlockBits(data); }
        return sig;
    }

    /// <summary>Find vertical scroll amount: prev[y] ≈ new[y-shift] over the overlap.</summary>
    private static (int shift, double residual) BestShift(RowSig[] prev, RowSig[] cur)
    {
        int h = prev.Length;
        int maxShift = (2 * h) / 3;          // keep a meaningful overlap
        double best = double.MaxValue; int bestShift = 0;
        double meanSig = 0;                  // average |signal| for normalization
        for (int y = 0; y < h; y++) meanSig += Math.Abs(prev[y].Br) + Math.Abs(prev[y].Cv);
        meanSig /= Math.Max(1, h);

        for (int s = 0; s <= maxShift; s++)
        {
            long cost = 0; int n = h - s;
            for (int y = s; y < h; y++)
            {
                int dbr = prev[y].Br - cur[y - s].Br;
                int dcv = prev[y].Cv - cur[y - s].Cv;
                cost += (dbr < 0 ? -dbr : dbr) + (dcv < 0 ? -dcv : dcv);
            }
            double normCost = cost / (double)Math.Max(1, n);
            if (normCost < best) { best = normCost; bestShift = s; }
        }
        double residual = meanSig > 0 ? best / meanSig : (best > 0 ? 1 : 0);
        return (bestShift, residual);
    }

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint f, uint dx, uint dy, uint data, UIntPtr extra);
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
}
