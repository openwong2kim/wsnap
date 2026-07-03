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
using System.IO;
using System.Threading.Tasks;
using Wsnap.Control;

namespace Wsnap;

/// <summary>
/// v1.7 automation: watch a folder and auto-OCR any image dropped into it, dropping the
/// recognized text on the clipboard and beside the image as a <c>.txt</c> sidecar. Toggled by
/// <see cref="Settings.WatchFolderOcr"/>; the watched directory is <see cref="Settings.WatchFolderPath"/>.
/// <para>
/// Wired exactly like <see cref="ClipboardWatcher"/> (Start/Stop/SetEnabled/Dispose), with the
/// same lifetime — App creates one, calls <see cref="SetEnabled"/> from ApplyRuntime, and
/// <see cref="Dispose"/>s on exit.
/// </para>
/// <para>
/// Re-processing is fenced two ways: an on-disk <c>.txt</c> sidecar (survives restarts) and an
/// in-memory seen-set (collapses the duplicate Created/Renamed events a single drop produces).
/// So even when the watch folder overlaps the capture <see cref="Settings.SaveFolder"/> and
/// wsnap's own shots land here, each image is OCR'd at most once.
/// </para>
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    /// <summary>Image extensions we auto-OCR. Kept deliberately narrow (no GIF/animated).</summary>
    private static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".bmp" };

    private FileSystemWatcher? _fsw;
    private string? _watchedDir;                 // normalized dir currently bound, for change detection
    private readonly Action<string>? _onOcr;     // optional post-OCR hook (App may pop a thumbnail, etc.)

    // Collapse the burst of events a single file drop raises. The sidecar is the durable guard;
    // this set is only a fast in-session filter, so clearing it when large is harmless.
    private const int SeenCap = 4096;
    private readonly object _seenLock = new();
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="onOcr">Optional callback (image path) fired on the UI thread after a
    /// successful OCR. Pass none for the plain clipboard+sidecar behaviour.</param>
    public FolderWatcher(Action<string>? onOcr = null) => _onOcr = onOcr;

    /// <summary>Begin watching <see cref="Settings.WatchFolderPath"/>. No-op if already bound to
    /// that same folder; rebinds if the configured path changed; quietly does nothing if the
    /// path is blank or missing (so a later SetEnabled can pick it up once it exists).</summary>
    public void Start()
    {
        string dir = Settings.Current.WatchFolderPath;
        if (string.IsNullOrWhiteSpace(dir)) { Stop(); return; }

        FileSystemWatcher? fsw = null;
        try
        {
            dir = Path.GetFullPath(dir);
            if (!Directory.Exists(dir)) { Stop(); return; }

            // Already watching the right folder? Leave it. Watching a stale one? Rebind.
            if (_fsw != null)
            {
                if (string.Equals(_watchedDir, dir, StringComparison.OrdinalIgnoreCase)) return;
                Stop();
            }

            fsw = new FileSystemWatcher(dir)
            {
                NotifyFilter = NotifyFilters.FileName,   // Created / Renamed of files
                IncludeSubdirectories = false,
                InternalBufferSize = 64 * 1024,          // headroom for bursty drops
            };
            fsw.Created += OnTouched;
            fsw.Renamed += OnTouched;                    // atomic writers create temp then rename to final
            fsw.Error += OnError;
            fsw.EnableRaisingEvents = true;

            _fsw = fsw;
            _watchedDir = dir;
        }
        catch (Exception ex)
        {
            CrashLog.Write("folder-watch", ex);
            try { fsw?.Dispose(); } catch { }
            _fsw = null;
            _watchedDir = null;
        }
    }

    /// <summary>Stop watching and release the FileSystemWatcher.</summary>
    public void Stop()
    {
        if (_fsw == null) return;
        try
        {
            _fsw.EnableRaisingEvents = false;
            _fsw.Created -= OnTouched;
            _fsw.Renamed -= OnTouched;
            _fsw.Error -= OnError;
            _fsw.Dispose();
        }
        catch (Exception ex) { CrashLog.Write("folder-watch", ex); }
        _fsw = null;
        _watchedDir = null;
    }

    /// <summary>App calls this from ApplyRuntime with <see cref="Settings.WatchFolderOcr"/>.</summary>
    public void SetEnabled(bool on) { if (on) Start(); else Stop(); }

    public void Dispose() => Stop();

    // ---------------------------------------------------------------------------------------

    // FileSystemWatcher raises Created/Renamed on a ThreadPool (MTA) thread. Fire-and-forget the
    // async pipeline — ProcessAsync swallows everything, so nothing goes unobserved.
    private void OnTouched(object sender, FileSystemEventArgs e)
    {
        if (IsWatchableImage(e.FullPath))
            _ = ProcessAsync(e.FullPath);
    }

    private void OnError(object sender, ErrorEventArgs e) =>
        CrashLog.Write("folder-watch", e.GetException());

    private async Task ProcessAsync(string path)
    {
        try
        {
            // (b) Collapse duplicate events for the same path within this session.
            lock (_seenLock)
            {
                if (_seen.Count > SeenCap) _seen.Clear();   // durable guard is the sidecar below
                if (!_seen.Add(path)) return;
            }

            // (a) Already OCR'd (this run or a previous one) → its sidecar is on disk. Skip.
            string sidecar = Path.ChangeExtension(path, ".txt");
            if (File.Exists(sidecar)) return;

            // Let the writer finish — a fresh drop may still be flushing to disk.
            if (!await WaitUntilReadable(path).ConfigureAwait(false)) return;

            var result = await CaptureCore.OcrImage(path, null).ConfigureAwait(false);
            if (!result.Ok) return;                                // OCR failed / engine unavailable
            if (result.Text is not { Length: > 0 } text) return;   // nothing readable — stay silent, no sidecar

            // Clipboard OLE calls must run on the STA/UI thread; we're on a worker here.
            RunOnUi(() => ImageClipboard.CopyText(text));

            // Sidecar beside the image — also the durable "already processed" marker.
            TryWriteSidecar(sidecar, text);

            Toast.Show($"OCR copied to clipboard: {Path.GetFileName(path)}");

            if (_onOcr is { } cb) RunOnUi(() => cb(path));
        }
        catch (Exception ex) { CrashLog.Write("folder-watch", ex); }
    }

    /// <summary>
    /// Wait until <paramref name="path"/> is fully written: openable AND size-stable across two
    /// polls. This clears both an exclusive writer (the open fails until it releases) and a
    /// share-friendly writer (the size keeps growing until it stops). ~200ms×10 max, then give up.
    /// </summary>
    private static async Task<bool> WaitUntilReadable(string path)
    {
        long lastLen = -1;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                long len;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    len = fs.Length;
                if (len > 0 && len == lastLen) return true;  // opened twice at the same size → done
                lastLen = len;
            }
            catch (FileNotFoundException) { return false; }   // vanished (e.g. temp file renamed away)
            catch (DirectoryNotFoundException) { return false; }
            catch (IOException) { lastLen = -1; }             // still locked / mid-write — reset & retry
            catch (UnauthorizedAccessException) { lastLen = -1; }
            await Task.Delay(200).ConfigureAwait(false);
        }
        return false;
    }

    private static void TryWriteSidecar(string sidecarPath, string text)
    {
        // File.WriteAllText defaults to UTF-8 (no BOM) — right for Korean/mixed OCR output.
        try { File.WriteAllText(sidecarPath, text); }
        catch (Exception ex) { CrashLog.Write("folder-watch", ex); }
    }

    private static bool IsWatchableImage(string path)
    {
        string ext = Path.GetExtension(path);
        foreach (var e in ImageExts)
            if (string.Equals(ext, e, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Marshal an action onto the UI/STA thread (falls back to inline when headless).</summary>
    private static void RunOnUi(Action action)
    {
        var app = System.Windows.Application.Current;
        if (app == null) { action(); return; }
        try { app.Dispatcher.Invoke(action); }
        catch (Exception ex) { CrashLog.Write("folder-watch", ex); }
    }
}
