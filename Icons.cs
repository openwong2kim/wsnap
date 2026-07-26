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
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Wsnap;

/// <summary>
/// Crisp vector icons drawn as stroked Paths on a 24-unit viewBox. The path data follows the
/// Lucide / SF Symbols line language (consistent geometry, rounded caps, even optical weight),
/// tuned to a 1.75-unit stroke so the glyphs read as native chrome on the macOS-toned UI rather
/// than the heavier 2.0 stroke of the earlier set. No icon font (no tofu / no Win10-vs-11 glyph
/// drift), and a Viewbox scales them to any size.
/// </summary>
public static class Icons
{
    // 24×24 stroke path data (Lucide-derived geometry, normalized to wsnap's subset).
    private static readonly Dictionary<string, string> Data = new()
    {
        // two overlapping rounded squares (front + back)
        ["copy"]    = "M9,9 L18,9 A2,2 0 0 1 20,11 L20,18 A2,2 0 0 1 18,20 L11,20 A2,2 0 0 1 9,18 Z M5,15 L5,6 A2,2 0 0 1 7,4 L15,4",

        // floppy disk — outline + notch + label slot
        ["save"]    = "M5,4 L17,4 L20,7 L20,18 A1,1 0 0 1 19,19 L5,19 A1,1 0 0 1 4,18 L4,5 A1,1 0 0 1 5,4 Z M8,4 L8,10 L15,10 L15,4 M8,13 L16,13 L16,19 L8,19 Z",

        // pencil editing the line beneath
        ["edit"]    = "M12,20 L21,20 M16.5,3.5 A2.12,2.12 0 0 1 19.5,6.5 L7,19 L3,20 L4,16 Z",

        // T (type) — the universal "text" affordance
        ["text"]    = "M4,5 L4,4 L20,4 L20,5 M9,20 L15,20 M12,4 L12,20",

        // folder with tab
        ["folder"]  = "M3,7 A2,2 0 0 1 5,5 L9,5 L11,7 L19,7 A2,2 0 0 1 21,9 L21,18 A2,2 0 0 1 19,20 L5,20 A2,2 0 0 1 3,18 Z",

        // share = upload arrow into a tray
        ["share"]   = "M12,3 L12,14 M8,7 L12,3 L16,7 M5,14 L5,18 A2,2 0 0 0 7,20 L17,20 A2,2 0 0 0 19,18 L19,14",

        // thumbtack — head + body + pin
        ["pin"]     = "M12,17 L12,21 M9,11 L9,5 L8,4 A1,1 0 0 1 9,3 L15,3 A1,1 0 0 1 16,4 L15,5 L15,11 L16,13 A1,1 0 0 1 15,14 L9,14 A1,1 0 0 1 8,13 Z",

        // close — plain X
        ["close"]   = "M6,6 L18,18 M18,6 L6,18",

        // trash can — lid + body + inner stripes
        ["trash"]   = "M4,7 L20,7 M10,7 L10,4 L14,4 L14,7 M6,7 L7,20 A1,1 0 0 0 8,21 L16,21 A1,1 0 0 0 17,20 L18,7 M10,11 L10,17 M14,11 L14,17",

        // video camera (GIF recording) — body + lens triangle
        ["gif"]     = "M3,7 A2,2 0 0 1 5,5 L14,5 A2,2 0 0 1 16,7 L16,17 A2,2 0 0 1 14,19 L5,19 A2,2 0 0 1 3,17 Z M16,10 L21,7 L21,17 L16,14",

        // external open — box with arrow breaking out the top-right
        ["open"]    = "M14,4 L20,4 L20,10 M20,4 L12,12 M16,14 L16,18 A1,1 0 0 1 15,19 L5,19 A1,1 0 0 1 4,18 L4,8 A1,1 0 0 1 5,7 L11,7",

        // refresh — circular arrow with arrowhead
        ["refresh"] = "M20,11 A8,8 0 1 0 18.5,15 M20,5 L20,11 L14,11",
    };

    /// <summary>Build a stroked icon scaled to <paramref name="size"/> px square. Default stroke
    /// weight 1.75 matches Lucide's "light" optical weight on this UI; pass a heavier weight for
    /// big hero icons.</summary>
    public static FrameworkElement Make(string key, double size, Brush stroke, double weight = 1.75)
    {
        var path = new Path
        {
            Data = Geometry.Parse(Data.TryGetValue(key, out var d) ? d : "M4,4 L20,20"),
            Stroke = stroke,
            StrokeThickness = weight,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.None,
            SnapsToDevicePixels = true
        };
        return new Viewbox
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Child = new Canvas { Width = 24, Height = 24, Children = { path }, Background = Brushes.Transparent }
        };
    }
}
