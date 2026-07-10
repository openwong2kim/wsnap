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
using System.Threading;
using SkiaSharp;

namespace Wsnap;

/// <summary>
/// v1.1 region GIF recorder. Grabs frames from a fixed screen rect on a timer until
/// the user stops, then encodes a looping animated GIF and hands back the path.
/// Deliberately simple (no separate trim editor) — fits the capture+DnD identity.
///
/// UI-framework-agnostic since Phase 4: the region is a plain device-px Rectangle, the
/// "recording" pill comes from <see cref="RecorderUi"/> (host-registered), and the WPF
/// DispatcherTimer is replaced by a threading timer whose ticks are posted through the
/// SynchronizationContext captured at <see cref="Start"/> — so all frame/stop work still
/// runs on the starting (UI) thread, exactly the old threading model.
/// </summary>
public sealed class GifRecorder
{
    private const int DefaultFps = 12;
    private const int DefaultMaxSeconds = 30;

    private readonly Rectangle _region;
    private readonly Action<string> _onSaved;
    // SKBitmap (not BitmapSource) since Phase 0: frames go straight from the GDI grab into
    // SkiaSharp buffers so the framework-agnostic GifWriter can encode them without WPF.
    private readonly List<SKBitmap> _frames = new();
    private Timer? _timer;
    private SynchronizationContext? _ctx;
    private bool _inTick;
    private readonly int _fps;
    private readonly int _maxSeconds;
    private readonly bool _showControl;
    private IRecorderBadge? _badge;
    private bool _stopped;

    /// <summary>Fires once the clip is saved OR recording is cancelled/ends with no frames, so an
    /// awaiting caller (CLI/MCP via the resident host) can complete even on the empty path.</summary>
    public event Action? Finished;

    /// <summary>
    /// Region GIF recorder. <paramref name="maxSeconds"/> caps the clip and is the auto-stop for
    /// programmatic/agent recording; <paramref name="showControl"/> shows the red "recording" badge
    /// — kept true for external/agent-initiated captures as a visibility + click-to-kill signal;
    /// <paramref name="fps"/> is the sample rate.
    /// </summary>
    public GifRecorder(Rectangle region, Action<string> onSaved,
                       int maxSeconds = DefaultMaxSeconds, bool showControl = true, int fps = DefaultFps)
    {
        _region = region;
        _onSaved = onSaved;
        _fps = Math.Clamp(fps, 1, 30);
        _maxSeconds = Math.Clamp(maxSeconds, 1, 120);
        _showControl = showControl;
    }

    /// <summary>True while frames are still being grabbed (before stop / auto-stop).</summary>
    public bool IsRecording => _timer != null && !_stopped;

    /// <summary>Frames captured / recorded seconds. Kept valid after <see cref="Stop"/> clears the
    /// buffer (snapshotted just before Clear), so the command result reports real counts.</summary>
    private int _savedFrames;
    public int FrameCount => _savedFrames > 0 ? _savedFrames : _frames.Count;
    public double Seconds => FrameCount / (double)_fps;

    /// <summary>Programmatic stop (CLI/MCP/agent). Idempotent — the internal <c>_stopped</c> guard handles races.</summary>
    public void StopExternal() => Stop();

    public void Start()
    {
        if (_region.Width < 2 || _region.Height < 2) { Finished?.Invoke(); return; }
        if (_showControl)
        {
            _badge = RecorderUi.TryShow(L.T("gif.recording0"), 0xF0C02A2A);
            if (_badge != null) _badge.Clicked += Stop;
        }
        // Tick on the starting thread: the threading timer fires on the pool, but each tick is
        // posted through the captured context, so _frames/_stopped stay single-threaded.
        _ctx = SynchronizationContext.Current;
        _timer = new Timer(_ =>
        {
            if (_ctx != null) _ctx.Post(__ => OnTick(), null);
            else OnTick();
        }, null, TimeSpan.FromMilliseconds(1000.0 / _fps), TimeSpan.FromMilliseconds(1000.0 / _fps));
    }

    private void OnTick()
    {
        if (_stopped || _inTick) return;   // reentrancy guard (a slow grab + queued posts)
        _inTick = true;
        try
        {
            using var bmp = ScreenGrab.Grab(_region.X, _region.Y, _region.Width, _region.Height);
            _frames.Add(SkiaImage.ToSKBitmap(bmp));
            _badge?.SetText(L.T("gif.recording", _frames.Count));
            if (_frames.Count >= _fps * _maxSeconds) Stop();
        }
        catch (Exception ex) { CrashLog.Write("gif-tick", ex); Stop(); }
        finally { _inTick = false; }
    }

    private void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        _timer?.Dispose(); _timer = null;
        _badge?.Close(); _badge = null;

        try
        {
            if (_frames.Count == 0) { Toast.Show(L.T("gif.canceled")); return; }
            _savedFrames = _frames.Count;   // snapshot before Clear so FrameCount/Seconds survive into Finished

            Toast.Show(L.T("gif.encoding"));
            string path = CaptureStore.NewPath(".gif");
            try
            {
                GifWriter.Save(_frames, path, 1000 / _fps);
                CrashLog.Telemetry("gif-saved");
                _onSaved(path);
            }
            catch (Exception ex)
            {
                CrashLog.Write("gif-save", ex);
                Toast.Show(L.T("gif.saveFail"));
            }
            foreach (var f in _frames) f.Dispose();   // SKBitmap holds native memory
            _frames.Clear();
        }
        finally { Finished?.Invoke(); }   // always signal the awaiting host (saved, empty, or failed)
    }
}
