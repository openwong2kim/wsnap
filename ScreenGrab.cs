using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Wsnap;

/// <summary>Pixel grabbing off the live screen (device pixels in, Bitmap/BitmapSource out).</summary>
public static class ScreenGrab
{
    public static Bitmap Grab(int x, int y, int w, int h)
    {
        var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h), CopyPixelOperation.SourceCopy);
        return bmp;
    }

    /// <summary>Fast GDI handoff to a frozen WPF BitmapSource (no PNG round-trip).</summary>
    public static BitmapSource ToBitmapSource(Bitmap bmp)
    {
        IntPtr h = bmp.GetHbitmap();
        try
        {
            var src = Imaging.CreateBitmapSourceFromHBitmap(
                h, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally { DeleteObject(h); }
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
