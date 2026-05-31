using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Wsnap;

/// <summary>
/// v1.1 clipboard-detection mode: when ANY tool copies an image to the clipboard,
/// wsnap pops a thumbnail for it too. Uses a message-only window + clipboard listener.
/// Toggled by <see cref="Settings.ClipboardWatch"/>.
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    private HwndSource? _src;
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
        if (_src != null) return;
        var p = new HwndSourceParameters("wsnap.clipboard")
        {
            Width = 0, Height = 0, WindowStyle = 0,
            ParentWindow = new IntPtr(-3)   // HWND_MESSAGE: message-only window
        };
        _src = new HwndSource(p);
        _src.AddHook(WndProc);
        AddClipboardFormatListener(_src.Handle);
        _lastSeq = GetClipboardSequenceNumber();
    }

    public void Stop()
    {
        if (_src == null) return;
        try { RemoveClipboardFormatListener(_src.Handle); } catch { }
        _src.RemoveHook(WndProc);
        _src.Dispose();
        _src = null;
    }

    public void SetEnabled(bool on) { if (on) Start(); else Stop(); }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
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
        }
        return IntPtr.Zero;
    }

    private void TryCaptureImage()
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsImage()) return;
            var img = System.Windows.Clipboard.GetImage();
            if (img == null) return;

            string path = CaptureStore.NewPath();
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(img));
            using (var fs = File.Create(path)) enc.Save(fs);

            CrashLog.Telemetry("clipboard-capture");
            _onImage(path);
        }
        catch (Exception ex) { CrashLog.Write("clipboard-watch", ex); }
    }

    public void Dispose() => Stop();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
