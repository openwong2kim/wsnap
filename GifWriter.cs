using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;

namespace Wsnap;

/// <summary>
/// Animated-GIF writer. WPF's GifBitmapEncoder produces a valid multi-frame GIF but
/// with no frame delay and no loop, so we post-process the bytes to inject a
/// Graphic Control Extension (delay) before every image and a NETSCAPE2.0 loop block.
/// </summary>
public static class GifWriter
{
    public static void Save(IList<BitmapSource> frames, string path, int delayMs)
    {
        if (frames.Count == 0) return;

        var enc = new GifBitmapEncoder();
        foreach (var f in frames) enc.Frames.Add(BitmapFrame.Create(f));

        byte[] raw;
        using (var ms = new MemoryStream()) { enc.Save(ms); raw = ms.ToArray(); }

        int delayCs = Math.Max(2, delayMs / 10);   // GIF delay is in 1/100 s
        byte[] fixedBytes = InjectDelaysAndLoop(raw, delayCs);
        File.WriteAllBytes(path, fixedBytes);
    }

    private static byte[] InjectDelaysAndLoop(byte[] g, int delayCs)
    {
        var o = new List<byte>(g.Length + 256);
        int i = 0;

        // Header (6) + Logical Screen Descriptor (7).
        o.AddRange(Slice(g, 0, 13));
        byte packed = g[10];
        i = 13;

        // Global Color Table.
        if ((packed & 0x80) != 0)
        {
            int gct = 3 * (1 << ((packed & 0x07) + 1));
            o.AddRange(Slice(g, i, gct));
            i += gct;
        }

        // NETSCAPE2.0 loop-forever block.
        o.Add(0x21); o.Add(0xFF); o.Add(0x0B);
        o.AddRange(Encoding.ASCII.GetBytes("NETSCAPE2.0"));
        o.Add(0x03); o.Add(0x01); o.Add(0x00); o.Add(0x00); o.Add(0x00);

        while (i < g.Length)
        {
            byte b = g[i];

            if (b == 0x3B) { o.Add(b); break; }   // trailer

            if (b == 0x2C) // image descriptor -> prepend a fresh GCE with our delay
            {
                WriteGce(o, delayCs);
                o.AddRange(Slice(g, i, 10));       // 0x2C + 9 descriptor bytes
                byte imgPacked = g[i + 9];
                i += 10;
                if ((imgPacked & 0x80) != 0)        // local color table
                {
                    int lct = 3 * (1 << ((imgPacked & 0x07) + 1));
                    o.AddRange(Slice(g, i, lct));
                    i += lct;
                }
                o.Add(g[i]); i++;                   // LZW min code size
                i = CopySubBlocks(g, i, o);          // image data
                continue;
            }

            if (b == 0x21) // extension
            {
                byte label = g[i + 1];
                if (label == 0xF9) { i = SkipSubBlocks(g, i + 2); continue; } // drop old GCE; we add our own
                o.Add(0x21); o.Add(label); i += 2;
                i = CopySubBlocks(g, i, o);
                continue;
            }

            o.Add(b); i++; // safety
        }

        return o.ToArray();
    }

    private static void WriteGce(List<byte> o, int delayCs)
    {
        o.Add(0x21); o.Add(0xF9); o.Add(0x04);
        o.Add(0x00);                                   // packed: disposal=0, no transparency
        o.Add((byte)(delayCs & 0xFF)); o.Add((byte)((delayCs >> 8) & 0xFF));
        o.Add(0x00);                                   // transparent index (unused)
        o.Add(0x00);                                   // block terminator
    }

    private static int CopySubBlocks(byte[] g, int p, List<byte> o)
    {
        while (true)
        {
            byte n = g[p]; o.Add(n); p++;
            if (n == 0) break;
            for (int k = 0; k < n; k++) { o.Add(g[p]); p++; }
        }
        return p;
    }

    private static int SkipSubBlocks(byte[] g, int p)
    {
        while (true)
        {
            byte n = g[p]; p++;
            if (n == 0) break;
            p += n;
        }
        return p;
    }

    private static IEnumerable<byte> Slice(byte[] a, int start, int len)
    {
        for (int k = 0; k < len; k++) yield return a[start + k];
    }
}
