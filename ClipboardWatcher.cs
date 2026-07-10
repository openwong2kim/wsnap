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
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Wsnap;

/// <summary>
/// v1.1 clipboard-detection mode: when ANY tool copies an image to the clipboard,
/// wsnap pops a thumbnail for it too. Toggled by <see cref="Settings.ClipboardWatch"/>.
///
/// UI-framework-agnostic since Phase 2 of the Avalonia migration: the WPF HwndSource is
/// replaced with a raw Win32 message-only window (create/dispatch on the UI thread, whose
/// message loop both WPF and Avalonia pump), and reading/decoding goes through
/// <see cref="ClipboardCore"/>/<see cref="SkiaImage"/> instead of WPF's Clipboard +
/// PngBitmapEncoder. FileDrop formats are deliberately ignored (a plain file copy in
/// Explorer is not an "image copy" — matches the old CF_DIB-only behaviour).
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private IntPtr _hwnd;
    private WndProcDelegate? _wndProc;   // held so the marshaled callback isn't GC'd
    private uint _lastSeq;
    private readonly Action<string> _onImage;

    // When wsnap itself writes an image to the clipboard (auto-copy on capture,
    // "copy image" buttons), the resulting WM_CLIPBOARDUPDATE would otherwise echo
    // back as a brand-new thumbnail. A small suppression counter swallows our own writes.
    private static int _suppress;
    public static void SuppressNext() => Interlocked.Exchange(ref _suppress, 1);

    public ClipboardWatcher(Action<string> onImageCaptured) => _onImage = onImageCaptured;

    public void Start()
    {
        if (_hwnd != IntPtr.Zero) return;
        _wndProc = WndProc;
        var cls = new WNDCLASSW
        {
            lpszClassName = "wsnap.clipboard." + Environment.CurrentManagedThreadId,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandleW(null),
        };
        RegisterClassW(ref cls);   // idempotent enough: same-name re-register fails, CreateWindow still finds it
        _hwnd = CreateWindowExW(0, cls.lpszClassName, "wsnap.clipboard", 0,
                                0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, cls.hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            CrashLog.Write($"clipwatch-create-failed: GetLastError={Marshal.GetLastWin32Error()}");
            return;
        }
        AddClipboardFormatListener(_hwnd);
        _lastSeq = GetClipboardSequenceNumber();
    }

    public void Stop()
    {
        if (_hwnd == IntPtr.Zero) return;
        try { RemoveClipboardFormatListener(_hwnd); } catch { }
        DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        _wndProc = null;
    }

    public void SetEnabled(bool on) { if (on) Start(); else Stop(); }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            uint seq = GetClipboardSequenceNumber();
            if (seq != _lastSeq)
            {
                _lastSeq = seq;
                if (Interlocked.Exchange(ref _suppress, 0) == 1) return IntPtr.Zero; // our own write
                TryCaptureImage();
            }
            return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, w, l);
    }

    private void TryCaptureImage()
    {
        try
        {
            // PNG stream first (alpha kept), then CF_DIB re-encoded — both already PNG bytes.
            byte[]? png = ClipboardCore.TryReadImageBytes(includeFileDrop: false);
            if (png == null) return;

            string path = CaptureStore.NewPath();
            File.WriteAllBytes(path, png);

            CrashLog.Telemetry("clipboard-capture");
            _onImage(path);
        }
        catch (Exception ex) { CrashLog.Write("clipboard-watch", ex); }
    }

    public void Dispose() => Stop();

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSW
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassW(ref WNDCLASSW cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
