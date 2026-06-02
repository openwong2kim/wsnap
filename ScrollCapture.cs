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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Wsnap;

/// <summary>
/// v1.1 scroll capture (best-effort). Picks a region, programmatically wheel-scrolls
/// the window under it, grabs frames, and stitches them by detecting vertical overlap
/// between consecutive frames. Works well for text/web; fragile on smooth-scrolling or
/// parallax content — stops automatically when no new content appears.
/// </summary>
public sealed class ScrollCapture
{
    private const int MaxSteps = 60;
    private const int MaxHeightPx = 20000;

    private readonly Int32Rect _r;
    private readonly Action<string> _onSaved;
    private readonly List<Bitmap> _strips = new();
    private Window? _control;
    private bool _stop;

    public ScrollCapture(Int32Rect region, Action<string> onSaved)
    {
        _r = region;
        _onSaved = onSaved;
    }

    public async void Start()
    {
        if (_r.Width < 4 || _r.Height < 8) return;
        ShowControl();

        int cx = _r.X + _r.Width / 2, cy = _r.Y + _r.Height / 2;
        SetCursorPos(cx, cy);
        await Task.Delay(200);

        int[]? prevSig = null;
        int totalH = 0, noProgress = 0;

        try
        {
            for (int step = 0; step < MaxSteps && !_stop; step++)
            {
                using var frame = ScreenGrab.Grab(_r.X, _r.Y, _r.Width, _r.Height);
                int[] sig = RowSignature(frame);

                if (prevSig == null)
                {
                    _strips.Add((Bitmap)frame.Clone());
                    totalH += frame.Height;
                }
                else
                {
                    int shift = BestShift(prevSig, sig);
                    if (shift < 3) { if (++noProgress >= 2) break; }
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

        _control?.Close();
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

    /// <summary>Per-row brightness signature (sampled columns) for overlap matching.</summary>
    private static int[] RowSignature(Bitmap bmp)
    {
        int h = bmp.Height, w = bmp.Width;
        var sig = new int[h];
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
                    int sum = 0;
                    for (int x = 0; x < w * 4; x += step)
                        sum += row[x] + row[x + 1] + row[x + 2];
                    sig[y] = sum;
                }
            }
        }
        finally { bmp.UnlockBits(data); }
        return sig;
    }

    /// <summary>Find vertical scroll amount: prev[y] ≈ new[y-shift] over the overlap.</summary>
    private static int BestShift(int[] prev, int[] cur)
    {
        int h = prev.Length;
        int maxShift = (2 * h) / 3;          // keep a meaningful overlap
        long best = long.MaxValue; int bestShift = 0;
        for (int s = 0; s <= maxShift; s++)
        {
            long cost = 0; int n = h - s;
            for (int y = s; y < h; y++)
            {
                int d = prev[y] - cur[y - s];
                cost += d < 0 ? -d : d;
            }
            cost = cost / Math.Max(1, n);
            if (cost < best) { best = cost; bestShift = s; }
        }
        return bestShift;
    }

    private void ShowControl()
    {
        var status = new TextBlock
        {
            Text = L.T("scroll.recording"),
            Foreground = System.Windows.Media.Brushes.White, FontSize = 13,
            Margin = new Thickness(12, 8, 12, 8)
        };
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xF0, 0x1E, 0x6F, 0xEB)),
            Child = status, Cursor = Cursors.Hand
        };
        _control = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent,
            Topmost = true, ShowInTaskbar = false, SizeToContent = SizeToContent.WidthAndHeight,
            Content = border
        };
        _control.MouseLeftButtonDown += (_, _) => _stop = true;
        _control.KeyDown += (_, e) => { if (e.Key == Key.Escape) _stop = true; };
        _control.Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            _control.Left = wa.Left + (wa.Width - _control.ActualWidth) / 2;
            _control.Top = wa.Top + 12;
        };
        _control.Show();
    }

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint f, uint dx, uint dy, uint data, UIntPtr extra);
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
}
