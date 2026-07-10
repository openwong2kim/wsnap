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
using System.Text;
using SkiaSharp;

namespace Wsnap;

/// <summary>
/// Animated-GIF writer, self-contained (Phase 0 of the Avalonia migration). The old
/// implementation leaned on WPF's GifBitmapEncoder for palette+LZW and then patched delay/loop
/// bytes in afterwards; this one writes the whole GIF89a stream itself — per-frame octree
/// palette (256 colors), classic LZW (ported from the canonical ppmtogif/NGif encoder), a
/// Graphic Control Extension per frame and a NETSCAPE2.0 loop-forever block — so it has no UI
/// framework dependency at all. SkiaSharp is the only import, and frames arrive as SKBitmap
/// (BGRA8888) straight from the recorder.
/// </summary>
public static class GifWriter
{
    public static void Save(IReadOnlyList<SKBitmap> frames, string path, int delayMs)
    {
        if (frames.Count == 0) return;
        int delayCs = Math.Max(2, delayMs / 10);   // GIF delay is in 1/100 s

        using var s = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);

        // Header + Logical Screen Descriptor (canvas = first frame, no global color table).
        s.Write(Encoding.ASCII.GetBytes("GIF89a"));
        WriteU16(s, frames[0].Width);
        WriteU16(s, frames[0].Height);
        s.WriteByte(0x70);   // no GCT, color resolution 8
        s.WriteByte(0x00);   // background color index
        s.WriteByte(0x00);   // pixel aspect ratio

        // NETSCAPE2.0 loop-forever block.
        s.WriteByte(0x21); s.WriteByte(0xFF); s.WriteByte(0x0B);
        s.Write(Encoding.ASCII.GetBytes("NETSCAPE2.0"));
        s.WriteByte(0x03); s.WriteByte(0x01); s.WriteByte(0x00); s.WriteByte(0x00); s.WriteByte(0x00);

        foreach (var frame in frames)
        {
            var (palette, indices) = Quantize(frame);

            // Graphic Control Extension: disposal 0 / no transparency, same as the old writer.
            s.WriteByte(0x21); s.WriteByte(0xF9); s.WriteByte(0x04);
            s.WriteByte(0x00);
            s.WriteByte((byte)(delayCs & 0xFF)); s.WriteByte((byte)((delayCs >> 8) & 0xFF));
            s.WriteByte(0x00); s.WriteByte(0x00);

            // Image descriptor with a 256-entry local color table.
            s.WriteByte(0x2C);
            WriteU16(s, 0); WriteU16(s, 0);
            WriteU16(s, frame.Width); WriteU16(s, frame.Height);
            s.WriteByte(0x87);   // LCT flag + size bits (2^(7+1) = 256)

            var lct = new byte[256 * 3];
            for (int i = 0; i < palette.Count; i++)
            {
                lct[i * 3 + 0] = (byte)(palette[i] >> 16);
                lct[i * 3 + 1] = (byte)(palette[i] >> 8);
                lct[i * 3 + 2] = (byte)palette[i];
            }
            s.Write(lct);

            LzwEncode(s, indices);
        }

        s.WriteByte(0x3B);   // trailer
    }

    private static void WriteU16(Stream s, int v)
    {
        s.WriteByte((byte)(v & 0xFF));
        s.WriteByte((byte)((v >> 8) & 0xFF));
    }

    // ---------------- palette quantization (Gervautz–Purgathofer octree) ----------------

    /// <summary>Quantize one BGRA frame to ≤256 colors: (palette as 0xRRGGBB, per-pixel indices).</summary>
    private static (List<int> Palette, byte[] Indices) Quantize(SKBitmap frame)
    {
        var tree = new Octree();
        int w = frame.Width, h = frame.Height;
        var indices = new byte[w * h];

        unsafe
        {
            uint* px = (uint*)frame.GetPixels();
            int stridePx = frame.RowBytes / 4;

            for (int y = 0; y < h; y++)
            {
                uint* row = px + (long)y * stridePx;
                for (int x = 0; x < w; x++)
                {
                    uint v = row[x];   // BGRA bytes read LE = 0xAARRGGBB
                    tree.Add((int)((v >> 16) & 0xFF), (int)((v >> 8) & 0xFF), (int)(v & 0xFF));
                }
            }

            var palette = tree.BuildPalette();

            // Screen content is run-heavy — a one-entry cache skips most tree walks.
            uint last = 0; byte lastIdx = 0; bool have = false;
            int n = 0;
            for (int y = 0; y < h; y++)
            {
                uint* row = px + (long)y * stridePx;
                for (int x = 0; x < w; x++)
                {
                    uint v = row[x] | 0xFF000000;   // alpha is irrelevant to the palette walk
                    if (!have || v != last)
                    {
                        last = v; have = true;
                        lastIdx = (byte)tree.IndexOf((int)((v >> 16) & 0xFF), (int)((v >> 8) & 0xFF), (int)(v & 0xFF));
                    }
                    indices[n++] = lastIdx;
                }
            }
            return (palette, indices);
        }
    }

    private sealed class Octree
    {
        private sealed class Node
        {
            public bool Leaf;
            public int Count, Index;
            public long R, G, B;
            public Node?[] Kids = new Node?[8];
        }

        private readonly Node _root = new();
        private readonly List<Node>[] _reducible =
            { new(), new(), new(), new(), new(), new(), new(), new() };   // by node level 0..7
        private int _leaves;

        public void Add(int r, int g, int b)
        {
            var node = _root;
            for (int level = 0; level < 8; level++)
            {
                if (node.Leaf) break;   // a reduced branch absorbs deeper colors here
                int bit = 7 - level;
                int idx = (((r >> bit) & 1) << 2) | (((g >> bit) & 1) << 1) | ((b >> bit) & 1);
                var kid = node.Kids[idx];
                if (kid == null)
                {
                    kid = new Node();
                    node.Kids[idx] = kid;
                    if (level == 7) { kid.Leaf = true; _leaves++; }
                    else _reducible[level + 1].Add(kid);
                }
                node = kid;
            }
            node.Count++; node.R += r; node.G += g; node.B += b;
            while (_leaves > 256) Reduce();
        }

        /// <summary>Merge the deepest reducible node's children into it (deepest-first keeps
        /// color error minimal — all nodes below the popped level are already leaves).</summary>
        private void Reduce()
        {
            for (int level = 7; level >= 1; level--)
            {
                var list = _reducible[level];
                if (list.Count == 0) continue;
                var node = list[^1];
                list.RemoveAt(list.Count - 1);
                if (node.Leaf) continue;   // already merged into an ancestor

                int merged = 0;
                for (int i = 0; i < 8; i++)
                {
                    var kid = node.Kids[i];
                    if (kid == null) continue;
                    node.Count += kid.Count; node.R += kid.R; node.G += kid.G; node.B += kid.B;
                    if (kid.Leaf) merged++;
                    node.Kids[i] = null;
                }
                node.Leaf = true;
                _leaves += 1 - merged;
                return;
            }
        }

        public List<int> BuildPalette()
        {
            var palette = new List<int>(256);
            void Walk(Node n)
            {
                if (n.Leaf)
                {
                    int c = Math.Max(1, n.Count);
                    n.Index = palette.Count;
                    palette.Add((int)(n.R / c) << 16 | (int)(n.G / c) << 8 | (int)(n.B / c));
                    return;
                }
                foreach (var kid in n.Kids) if (kid != null) Walk(kid);
            }
            Walk(_root);
            return palette;
        }

        public int IndexOf(int r, int g, int b)
        {
            var node = _root;
            for (int level = 0; level < 8 && !node.Leaf; level++)
            {
                int bit = 7 - level;
                int idx = (((r >> bit) & 1) << 2) | (((g >> bit) & 1) << 1) | ((b >> bit) & 1);
                var kid = node.Kids[idx];
                if (kid == null) break;   // can't happen for colors that were Add()ed
                node = kid;
            }
            return node.Index;
        }
    }

    // ---------------- LZW (GIF variable-code, ported from the canonical ppmtogif encoder) ----------------

    private const int MaxBits = 12;
    private const int MaxMaxCode = 1 << MaxBits;
    private const int HSize = 5003;   // 80% occupancy prime

    private static void LzwEncode(Stream s, byte[] pixels)
    {
        const int initBits = 9;                 // min code size 8 → 9-bit codes
        s.WriteByte(8);

        int clearCode = 1 << (initBits - 1);    // 256
        int eofCode = clearCode + 1;            // 257
        int freeEnt = clearCode + 2;            // 258
        int nBits = initBits;
        int maxCode = (1 << nBits) - 1;
        bool clearFlag = false;

        var htab = new int[HSize];
        var codetab = new int[HSize];
        Array.Fill(htab, -1);

        int curAccum = 0, curBits = 0;
        var block = new byte[255];
        int blockLen = 0;

        void FlushBlock()
        {
            if (blockLen == 0) return;
            s.WriteByte((byte)blockLen);
            s.Write(block, 0, blockLen);
            blockLen = 0;
        }

        void PutByte(byte b)
        {
            block[blockLen++] = b;
            if (blockLen == 255) FlushBlock();
        }

        void Output(int code)
        {
            curAccum |= code << curBits;
            curBits += nBits;
            while (curBits >= 8) { PutByte((byte)(curAccum & 0xFF)); curAccum >>= 8; curBits -= 8; }

            // Bump the code width exactly when the decoder will (its dictionary mirrors freeEnt).
            if (freeEnt > maxCode || clearFlag)
            {
                if (clearFlag) { nBits = initBits; maxCode = (1 << nBits) - 1; clearFlag = false; }
                else { nBits++; maxCode = nBits == MaxBits ? MaxMaxCode : (1 << nBits) - 1; }
            }
        }

        int hshift = 0;
        for (int f = HSize; f < 65536; f *= 2) hshift++;
        hshift = 8 - hshift;

        Output(clearCode);
        int ent = pixels[0];

        for (int p = 1; p < pixels.Length; p++)
        {
            int c = pixels[p];
            int fcode = (c << MaxBits) + ent;
            int i = (c << hshift) ^ ent;

            bool found = false;
            if (htab[i] == fcode) { ent = codetab[i]; continue; }
            if (htab[i] >= 0)   // occupied slot — secondary probe
            {
                int disp = i == 0 ? 1 : HSize - i;
                do
                {
                    i -= disp;
                    if (i < 0) i += HSize;
                    if (htab[i] == fcode) { ent = codetab[i]; found = true; break; }
                } while (htab[i] >= 0);
            }
            if (found) continue;

            Output(ent);
            if (freeEnt < MaxMaxCode)
            {
                codetab[i] = freeEnt++;
                htab[i] = fcode;
            }
            else
            {
                Array.Fill(htab, -1);
                freeEnt = clearCode + 2;
                clearFlag = true;
                Output(clearCode);
            }
            ent = c;
        }

        Output(ent);
        Output(eofCode);
        while (curBits > 0) { PutByte((byte)(curAccum & 0xFF)); curAccum >>= 8; curBits -= 8; }
        FlushBlock();
        s.WriteByte(0);   // block terminator
    }
}
