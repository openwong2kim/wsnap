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
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Wsnap;

/// <summary>
/// Dark theme for the WinForms tray menu. The stock ContextMenuStrip renders a light-gray
/// Office-style menu — the single place where wsnap still looked like a foreign app instead
/// of its dark design system. This renderer mirrors Theme.cs tokens (Panel/Surface/Accent)
/// in System.Drawing form: dark panel, rounded hover pills, hairline separators.
/// </summary>
internal static class TrayMenuTheme
{
    // Theme.cs tokens, mirrored as GDI colors (WinForms can't read WPF brushes).
    private static readonly Color Panel     = Color.FromArgb(0x1C, 0x1F, 0x23);   // Panel2
    private static readonly Color Text      = Color.FromArgb(0xF4, 0xF5, 0xF7);
    private static readonly Color Muted     = Color.FromArgb(0x8C, 0x90, 0x9A);
    private static readonly Color Hover     = Color.FromArgb(0x2C, 0x30, 0x36);   // SurfaceHi
    private static readonly Color Hairline  = Color.FromArgb(0x33, 0x3A, 0x42);

    /// <summary>Apply the dark theme to a menu and every submenu it owns.</summary>
    public static void Apply(ContextMenuStrip menu)
    {
        var renderer = new DarkRenderer();
        menu.Renderer = renderer;
        menu.BackColor = Panel;
        menu.ForeColor = Text;
        menu.ShowImageMargin = false;
        ApplyItems(menu.Items, renderer);
    }

    /// <summary>Submenus are independent ToolStripDropDowns with their OWN renderer — they do
    /// not inherit the parent's, so each one must be themed explicitly or it pops up light.</summary>
    private static void ApplyItems(ToolStripItemCollection items, ToolStripRenderer renderer)
    {
        foreach (ToolStripItem it in items)
        {
            it.ForeColor = Text;
            if (it is ToolStripMenuItem mi && mi.HasDropDownItems && mi.DropDown is ToolStripDropDownMenu dd)
            {
                dd.Renderer = renderer;
                dd.BackColor = Panel;
                dd.ForeColor = Text;
                dd.ShowImageMargin = false;
                ApplyItems(dd.Items, renderer);
            }
        }
    }

    private sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled)
            {
                base.OnRenderMenuItemBackground(e);
                return;
            }
            // Rounded hover pill, inset like the WPF SubtleButton hover.
            var r = new Rectangle(2, 1, e.Item.Width - 5, e.Item.Height - 2);
            using var path = RoundedRect(r, 6);
            using var fill = new SolidBrush(Hover);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillPath(fill, path);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(Hairline);
            int y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Muted;
            base.OnRenderArrow(e);
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    /// <summary>Color table for the parts the professional renderer draws itself
    /// (menu chrome, borders, gradients — all flattened to the dark panel).</summary>
    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Panel;
        public override Color ImageMarginGradientBegin => Panel;
        public override Color ImageMarginGradientMiddle => Panel;
        public override Color ImageMarginGradientEnd => Panel;
        public override Color MenuBorder => Hairline;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color SeparatorDark => Hairline;
        public override Color SeparatorLight => Hairline;
    }
}
