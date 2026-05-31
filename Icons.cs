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
/// Crisp vector icons drawn as stroked Paths on a 24-unit viewBox — the SAME line-art
/// language as the landing page, so the app and its site read as one brand. No icon
/// font (no tofu / no Win10-vs-11 glyph drift), and a Viewbox scales them to any size.
/// </summary>
public static class Icons
{
    // 24x24 stroke path data (mirrors site/index.html svg paths where they exist).
    private static readonly Dictionary<string, string> Data = new()
    {
        ["copy"]   = "M8,8 L19,8 L19,19 L8,19 Z M5,16 L5,5 L16,5",
        ["save"]   = "M12,3 L12,15 M12,15 L7.5,10.5 M12,15 L16.5,10.5 M4,17 L4,19 A2,2 0 0 0 6,21 L18,21 A2,2 0 0 0 20,19 L20,17",
        ["edit"]   = "M12,20 L21,20 M16.5,3.5 A2.12,2.12 0 0 1 19.5,6.5 L7,19 L3,20 L4,16 Z",
        ["text"]   = "M4,7 L4,5 L20,5 L20,7 M4,12 L14,12 M4,17 L11,17",
        ["folder"] = "M3,7 A2,2 0 0 1 5,5 L9,5 L11,7 L19,7 A2,2 0 0 1 21,9 L21,17 A2,2 0 0 1 19,19 L5,19 A2,2 0 0 1 3,17 Z",
        ["share"]  = "M12,3 L12,13 M12,3 L8.5,6.5 M12,3 L15.5,6.5 M5,17 L5,19 A2,2 0 0 0 7,21 L17,21 A2,2 0 0 0 19,19 L19,17",
        ["pin"]    = "M12,21 C12,21 19,14.5 19,9 A7,7 0 0 0 5,9 C5,14.5 12,21 12,21 Z M12,7 A2,2 0 0 1 12,11 A2,2 0 0 1 12,7 Z",
        ["close"]  = "M6,6 L18,18 M18,6 L6,18",
        ["trash"]  = "M4,7 L20,7 M9,7 L9,4 L15,4 L15,7 M6,7 L7,20 L17,20 L18,7",
        ["gif"]     = "M3,7 A2,2 0 0 1 5,5 L14,5 A2,2 0 0 1 16,7 L16,17 A2,2 0 0 1 14,19 L5,19 A2,2 0 0 1 3,17 Z M16,10 L21,7 L21,17 L16,14",
        ["open"]    = "M14,4 L20,4 L20,10 M20,4 L11,13 M18,13 L18,19 A1,1 0 0 1 17,20 L5,20 A1,1 0 0 1 4,19 L4,7 A1,1 0 0 1 5,6 L11,6",
        ["refresh"] = "M20,11 A8,8 0 1 0 19,15 M20,5 L20,11 L14,11",
    };

    /// <summary>Build a stroked icon scaled to <paramref name="size"/> px square.</summary>
    public static FrameworkElement Make(string key, double size, Brush stroke, double weight = 2.0)
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
