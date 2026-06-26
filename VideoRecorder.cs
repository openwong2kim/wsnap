// wsnap — macOS-style screen capture for Windows.
// Copyright (C) 2026 openwong2kim and wsnap contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License version 3, as published by
// by the Free Software Foundation.
//
// This program is distributed in the hope that it will be useful, but
// WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY
// or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License
// for more details.
//
// You should have received a copy of the GNU General Public License along
// with this program. If not, see <https://www.gnu.org/licenses/>.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Wsnap;

public enum VideoFormat { Mp4, Apng }

/// <summary>
/// Region video recorder. Captures frames from a fixed screen rect at a steady FPS and
/// streams them as raw top-down BGRA into ffmpeg's stdin, which encodes either:
///   • <see cref="VideoFormat.Mp4"/> — a single H.264 .mp4 (yuv420p, faststart), or
///   • <see cref="VideoFormat.Apng"/> — a lossless full-colour RGBA animated PNG (.apng) that
///     loops forever — essentially a lossless GIF. APNG is itself a valid PNG, so WPF decodes
///     its first frame for the thumbnail with no poster extraction needed.
/// This replaces the deferred Media Foundation SinkWriter approach — ffmpeg is
/// environment-agnostic (works on Windows N / stripped images without mfplat.dll) and is the
/// path that can actually be validated. See <see cref="FFmpegProvider"/>.
///
/// The capture loop runs on a background thread so the UI thread is never blocked by a frame
/// write (a big region's BGRA row stream can be multi-MB). ffmpeg back-pressures naturally:
/// if encoding lags, the stdin write blocks and we simply capture fewer frames. Stop is
/// marshalled to the control window's dispatcher when it comes from the background thread.
/// </summary>
public sealed class VideoRecorder
{
    private const int MaxSecondsMp4 = 120;
    private const int MaxSecondsApng = 30;   // APNG is lossless RGBA — grows fast, cap like GIF

    private readonly Int32Rect _region;
    private readonly Action<string, string?> _onSaved;
    private readonly int _fps;
    private readonly VideoFormat _format;
    private readonly int _maxSeconds;

    private Process? _ffmpeg;
    private Window? _control;
    private TextBlock? _status;
    private Thread? _thread;
    private volatile bool _stopped;
    private int _frames;
    private string? _outPath;
    private bool _failed;

    /// <param name="onSaved">Invoked with (filePath, posterPath). For MP4, posterPath is a
    /// first-frame PNG so <see cref="ThumbnailWindow"/> can display it (an mp4 has no WIC image
    /// decoder). For APNG it is null — an .apng is itself a valid PNG, so WPF shows frame 1.</param>
    public VideoRecorder(Int32Rect region, Action<string, string?> onSaved, VideoFormat format, int? fps = null)
    {
        _region = region;
        _onSaved = onSaved;
        _fps = Math.Clamp(fps ?? Settings.Current.VideoFps, 1, 60);
        _format = format;
        _maxSeconds = format == VideoFormat.Apng ? MaxSecondsApng : MaxSecondsMp4;
    }

    /// <summary>True iff ffmpeg can be resolved right now (no download). Checked by App to
    /// decide whether to start video or fall back to GIF.</summary>
    public static bool IsAvailable => FFmpegProvider.TryResolve() != null;

    public void Start()
    {
        if (_region.Width < 2 || _region.Height < 2) return;

        // H.264/yuv420p needs even dimensions; APNG (PNG) supports any size. Keep the exact
        // region for APNG so the lossless grab is pixel-perfect.
        int w = _format == VideoFormat.Mp4 ? _region.Width & ~1 : _region.Width;
        int h = _format == VideoFormat.Mp4 ? _region.Height & ~1 : _region.Height;
        if (w < 2 || h < 2) return;

        var ffmpeg = FFmpegProvider.TryResolve();
        if (ffmpeg == null) { Toast.Show(L.T("vid.ffmpegMissing")); return; }

        string ext = _format == VideoFormat.Apng ? ".apng" : ".mp4";
        _outPath = CaptureStore.NewPath(ext);

        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(_outPath) ?? Environment.CurrentDirectory
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        // Raw top-down BGRA frames on stdin: one frame = w*h*4 bytes (identical for both formats).
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("rawvideo");
        psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("bgra");
        psi.ArgumentList.Add("-s"); psi.ArgumentList.Add($"{w}x{h}");
        psi.ArgumentList.Add("-r"); psi.ArgumentList.Add(_fps.ToStringInvariant());
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add("pipe:0");

        if (_format == VideoFormat.Apng)
        {
            // Lossless full-colour RGBA animated PNG, looping forever.
            psi.ArgumentList.Add("-plays"); psi.ArgumentList.Add("0");
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("apng");
        }
        else
        {
            psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("libx264");
            psi.ArgumentList.Add("-preset"); psi.ArgumentList.Add("veryfast");
            psi.ArgumentList.Add("-crf"); psi.ArgumentList.Add("23");
            psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("yuv420p");
            psi.ArgumentList.Add("-movflags"); psi.ArgumentList.Add("+faststart");
        }
        psi.ArgumentList.Add(_outPath);

        try { _ffmpeg = Process.Start(psi); }
        catch (Exception ex)
        {
            CrashLog.Write("video-start", ex);
            Toast.Show(L.T("vid.saveFail"));
            return;
        }
        if (_ffmpeg == null) { Toast.Show(L.T("vid.saveFail")); return; }

        // Drain stderr so ffmpeg never blocks on a full pipe (minimal at -loglevel error).
        _ffmpeg.ErrorDataReceived += (_, _) => { };
        _ffmpeg.BeginErrorReadLine();

        ShowControl();

        _thread = new Thread(() => CaptureLoop(w, h)) { IsBackground = true, Name = "wsnap-video" };
        _thread.Start();
    }

    /// <summary>Frame capture + pipe loop on a background thread. Paces to <c>_fps</c> with a
    /// Stopwatch so wall-clock duration roughly matches the recorded length.</summary>
    private void CaptureLoop(int w, int h)
    {
        var interval = TimeSpan.FromSeconds(1.0 / _fps);
        var sw = Stopwatch.StartNew();
        var next = sw.Elapsed;
        int maxFrames = _fps * _maxSeconds;

        while (!_stopped)
        {
            try
            {
                using var bmp = ScreenGrab.Grab(_region.X, _region.Y, w, h);
                WriteFrame(bmp, w, h);
                _frames++;
                UpdateStatus();
                if (_frames >= maxFrames) { Stop(); return; }
            }
            catch (Exception ex)
            {
                CrashLog.Write("video-tick", ex);
                _failed = true;
                Stop();
                return;
            }

            next += interval;
            var delay = next - sw.Elapsed;
            if (delay > TimeSpan.Zero) Thread.Sleep(delay);
        }
    }

    /// <summary>LockBits the GDI+ bitmap and stream its rows as BGRA. A <c>new Bitmap()</c>
    /// with <c>Format32bppArgb</c> is top-down (Scan0 = top row) and stores bytes as BGRA, so
    /// we emit rows in forward order — matching ffmpeg's <c>-pix_fmt bgra</c> top-down rawvideo.</summary>
    private void WriteFrame(Bitmap bmp, int w, int h)
    {
        if (_ffmpeg == null) return;
        var rect = new Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var stdin = _ffmpeg.StandardInput.BaseStream;
            int stride = data.Stride;            // == w*4 for 32bpp (no padding)
            // A GDI+ Bitmap created with `new Bitmap(w,h,Format32bppArgb)` is TOP-DOWN:
            // Scan0 is the top row (verified empirically — see the orientation probe).
            // ffmpeg rawvideo is also top-down, so emit rows in FORWARD order — no flip.
            for (int y = 0; y < h; y++)
            {
                IntPtr row = data.Scan0 + y * stride;
                int left = stride;
                while (left > 0)
                {
                    int chunk = Math.Min(left, 65536);
                    byte[] buf = BufferPool(chunk);
                    Marshal.Copy(row + (stride - left), buf, 0, chunk);
                    stdin.Write(buf, 0, chunk);   // blocks on backpressure — self-throttles
                    left -= chunk;
                }
            }
        }
        finally { bmp.UnlockBits(data); }
    }

    [ThreadStatic] private static byte[]? _buf;
    private static byte[] BufferPool(int min)
    {
        if (_buf == null || _buf.Length < min) _buf = new byte[min];
        return _buf;
    }

    private void UpdateStatus()
    {
        if (_status == null) return;
        try
        {
            _status.Dispatcher.BeginInvoke(new Action(() =>
                _status.Text = L.T("vid.recording", _frames)), DispatcherPriority.DataBind);
        }
        catch { /* window closing */ }
    }

    /// <summary>Stop capture, flush + close stdin so ffmpeg finishes the mux, wait, then hand
    /// the mp4 to the thumbnail. Safe to call from the UI thread (click/Esc) or the loop thread.</summary>
    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;

        // Let the loop drain its current frame, then finish.
        try { _thread?.Join(3000); } catch { }

        if (_ffmpeg != null)
        {
            try
            {
                _ffmpeg.StandardInput.BaseStream.Flush();
                _ffmpeg.StandardInput.Close();
            }
            catch { /* ffmpeg may already be gone */ }
            try { _ffmpeg.WaitForExit(15000); } catch { }
            try { _ffmpeg.Dispose(); } catch { }
            _ffmpeg = null;
        }

        CloseControl();

        if (_failed || _frames == 0 || _outPath == null || !File.Exists(_outPath))
        {
            Toast.Show(L.T("vid.saveFail"));
            try { if (_outPath != null && File.Exists(_outPath)) File.Delete(_outPath); } catch { }
            return;
        }

        CrashLog.Telemetry(_format == VideoFormat.Apng ? "apng-saved" : "video-saved");
        // APNG is a valid PNG — WPF decodes its first frame for the thumbnail, so no poster
        // needed. MP4 has no image decoder, so extract a first-frame PNG for display.
        _onSaved(_outPath, _format == VideoFormat.Apng ? null : ExtractPoster(_outPath));
    }

    /// <summary>Grab the first frame of the mp4 as a PNG (alongside it) so the thumbnail can
    /// show a still — WPF's BitmapImage cannot decode H.264. Returns null on any failure;
    /// the caller degrades gracefully (no poster shown, mp4 actions still work).</summary>
    private static string? ExtractPoster(string mp4)
    {
        var ff = FFmpegProvider.TryResolve();
        if (ff == null) return null;
        string png = Path.ChangeExtension(mp4, ".poster.png");
        try
        {
            var psi = new ProcessStartInfo(ff)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(mp4);
            psi.ArgumentList.Add("-frames:v"); psi.ArgumentList.Add("1");
            psi.ArgumentList.Add(png);
            using var p = Process.Start(psi);
            if (p == null) return null;
            p.BeginErrorReadLine();
            p.WaitForExit(8000);
            return File.Exists(png) && new FileInfo(png).Length > 0 ? png : null;
        }
        catch (Exception ex) { CrashLog.Write("video-poster", ex); return null; }
    }

    private void CloseControl()
    {
        var c = _control;
        if (c == null) return;
        try
        {
            if (c.Dispatcher.CheckAccess()) c.Close();
            else c.Dispatcher.Invoke(new Action(c.Close));
        }
        catch { }
    }

    private void ShowControl()
    {
        _status = new TextBlock
        {
            Text = L.T("vid.recording0"),
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 13, Margin = new Thickness(12, 8, 12, 8)
        };
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xF0, 0xC0, 0x2A, 0x2A)),
            Child = _status, Cursor = Cursors.Hand
        };
        _control = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent,
            Topmost = true, ShowInTaskbar = false, SizeToContent = SizeToContent.WidthAndHeight,
            Content = border
        };
        _control.MouseLeftButtonDown += (_, _) => Stop();
        _control.KeyDown += (_, e) => { if (e.Key == Key.Escape) Stop(); };
        _control.Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            _control!.Left = wa.Left + (wa.Width - _control.ActualWidth) / 2;
            _control.Top = wa.Top + 12;   // top-center, away from most capture regions
        };
        _control.Show();
        _control.Activate();
    }
}

internal static class FpsFormat
{
    /// <summary>Invariant-culture integer string (no comma grouping for locales like ko/de).</summary>
    public static string ToStringInvariant(this int v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
