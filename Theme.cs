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
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;

namespace Wsnap;

/// <summary>
/// wsnap's shared design system. One source of truth for color, type, radius and
/// control styling so the editor, settings, capture toolbar and thumbnails all feel
/// like one app — and like the landing page (same dark palette + accent #3B82F6).
///
/// The visual tokens mirror site/index.html so the product and its marketing match.
/// Styles are authored as a XAML ResourceDictionary (real ControlTemplates with
/// hover/press/checked triggers) and parsed once; code-behind windows merge it in
/// and pull styles by key via <see cref="Style"/> / <see cref="Brush"/>.
/// </summary>
public static class Theme
{
    // ---- color tokens (mirror the landing page :root) ----
    public static readonly Color Bg          = Hex("#0E0F11");
    public static readonly Color Panel       = Hex("#16181B");
    public static readonly Color Panel2      = Hex("#1C1F23");
    public static readonly Color Surface     = Hex("#23262B"); // inputs / resting controls
    public static readonly Color SurfaceHi   = Hex("#2C3036"); // hover
    public static readonly Color Text        = Hex("#F4F5F7");
    public static readonly Color Muted       = Hex("#AEB1BA");
    public static readonly Color Muted2      = Hex("#8C909A");
    public static readonly Color Accent      = Hex("#3B82F6");
    public static readonly Color AccentDeep  = Hex("#2563EB");
    public static readonly Color AccentSoft  = Color.FromArgb(0x24, 0x3B, 0x82, 0xF6);
    public static readonly Color Danger      = Hex("#EF4444");
    public static readonly Color Warn        = Hex("#FBBF24");
    public static readonly Color Success     = Hex("#22C55E");
    public static readonly Color Border      = Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF);
    public static readonly Color BorderStrong= Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF);

    public const string FontStack =
        "Segoe UI Variable Text, Segoe UI, Malgun Gothic, Apple SD Gothic Neo, sans-serif";

    public static readonly FontFamily Font = new(FontStack);

    private static Color Hex(string s)
    {
        s = s.TrimStart('#');
        byte r = Convert.ToByte(s.Substring(0, 2), 16);
        byte g = Convert.ToByte(s.Substring(2, 2), 16);
        byte b = Convert.ToByte(s.Substring(4, 2), 16);
        return Color.FromRgb(r, g, b);
    }

    public static SolidColorBrush Stroke(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    // ---- resource dictionary (lazy) ----
    private static ResourceDictionary? _dict;
    public static ResourceDictionary Dict => _dict ??= (ResourceDictionary)XamlReader.Parse(Xaml);

    /// <summary>Pull a brush token (e.g. "Accent", "Muted") as a frozen SolidColorBrush.</summary>
    public static SolidColorBrush Brush(string key) => (SolidColorBrush)Dict[key + "Brush"];

    /// <summary>Pull a named control style (e.g. "PrimaryButton", "ToolToggle").</summary>
    public static System.Windows.Style Style(string key) => (System.Windows.Style)Dict[key];

    /// <summary>Merge the theme into a window and set sane window-wide defaults.</summary>
    public static void Apply(Window w)
    {
        w.Resources.MergedDictionaries.Add(Dict);
        w.Background = Brush("Bg");
        w.FontFamily = Font;
        // Default text color for any bare TextBlock/Label placed in the window.
        System.Windows.Documents.TextElement.SetForeground(w, Brush("Text"));
        // Paint the OS title bar dark so a themed window isn't topped by white chrome.
        w.SourceInitialized += (_, _) => SetDarkTitleBar(w);
    }

    /// <summary>Ask DWM for a dark caption + matching caption color (Win10 1809+ / Win11).</summary>
    private static void SetDarkTitleBar(Window w)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            int on = 1;
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Win10 2004+); 19 on earlier builds.
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
            // 35 = DWMWA_CAPTION_COLOR (Win11): tint to our base. COLORREF = 0x00BBGGRR.
            int caption = Bg.R | (Bg.G << 8) | (Bg.B << 16);
            DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
        }
        catch { /* unsupported OS build — keep default chrome */ }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // ---- the theme, authored in XAML ----
    private const string Xaml = @"
<ResourceDictionary
    xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
    xmlns:sys='clr-namespace:System;assembly=mscorlib'>

  <!-- ===== brushes ===== -->
  <SolidColorBrush x:Key='BgBrush'           Color='#0E0F11'/>
  <SolidColorBrush x:Key='PanelBrush'        Color='#16181B'/>
  <SolidColorBrush x:Key='Panel2Brush'       Color='#1C1F23'/>
  <SolidColorBrush x:Key='SurfaceBrush'      Color='#23262B'/>
  <SolidColorBrush x:Key='SurfaceHiBrush'    Color='#2C3036'/>
  <SolidColorBrush x:Key='TextBrush'         Color='#F4F5F7'/>
  <SolidColorBrush x:Key='MutedBrush'        Color='#AEB1BA'/>
  <SolidColorBrush x:Key='Muted2Brush'       Color='#8C909A'/>
  <SolidColorBrush x:Key='AccentBrush'       Color='#3B82F6'/>
  <SolidColorBrush x:Key='AccentDeepBrush'   Color='#2563EB'/>
  <SolidColorBrush x:Key='AccentSoftBrush'   Color='#243B82F6'/>
  <SolidColorBrush x:Key='DangerBrush'       Color='#EF4444'/>
  <SolidColorBrush x:Key='WarnBrush'         Color='#FBBF24'/>
  <SolidColorBrush x:Key='SuccessBrush'      Color='#22C55E'/>
  <SolidColorBrush x:Key='BorderBrush2'      Color='#18FFFFFF'/>
  <SolidColorBrush x:Key='BorderStrongBrush' Color='#28FFFFFF'/>

  <FontFamily x:Key='UiFont'>Segoe UI Variable Text, Segoe UI, Malgun Gothic, Apple SD Gothic Neo</FontFamily>

  <!-- ===== primary button ===== -->
  <Style x:Key='PrimaryButton' TargetType='Button'>
    <Setter Property='FontFamily' Value='{StaticResource UiFont}'/>
    <Setter Property='FontSize' Value='13.5'/>
    <Setter Property='FontWeight' Value='SemiBold'/>
    <Setter Property='Foreground' Value='White'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='SnapsToDevicePixels' Value='True'/>
    <Setter Property='Padding' Value='16,8,16,8'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border x:Name='b' CornerRadius='9' Background='{StaticResource AccentBrush}'
                  Padding='{TemplateBinding Padding}' SnapsToDevicePixels='True'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='b' Property='Background' Value='{StaticResource AccentDeepBrush}'/>
            </Trigger>
            <Trigger Property='IsPressed' Value='True'>
              <Setter TargetName='b' Property='Background' Value='{StaticResource AccentDeepBrush}'/>
              <Setter TargetName='b' Property='RenderTransform'>
                <Setter.Value><ScaleTransform ScaleX='0.97' ScaleY='0.97'/></Setter.Value>
              </Setter>
              <Setter TargetName='b' Property='RenderTransformOrigin' Value='0.5,0.5'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter TargetName='b' Property='Opacity' Value='0.4'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- ===== ghost / secondary button ===== -->
  <Style x:Key='GhostButton' TargetType='Button'>
    <Setter Property='FontFamily' Value='{StaticResource UiFont}'/>
    <Setter Property='FontSize' Value='13.5'/>
    <Setter Property='FontWeight' Value='SemiBold'/>
    <Setter Property='Foreground' Value='{StaticResource TextBrush}'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='SnapsToDevicePixels' Value='True'/>
    <Setter Property='Padding' Value='16,8,16,8'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border x:Name='b' CornerRadius='9' Background='Transparent'
                  BorderBrush='{StaticResource BorderStrongBrush}' BorderThickness='1'
                  Padding='{TemplateBinding Padding}' SnapsToDevicePixels='True'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='b' Property='Background' Value='{StaticResource SurfaceBrush}'/>
              <Setter TargetName='b' Property='BorderBrush' Value='{StaticResource AccentBrush}'/>
            </Trigger>
            <Trigger Property='IsPressed' Value='True'>
              <Setter TargetName='b' Property='Background' Value='{StaticResource SurfaceHiBrush}'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter TargetName='b' Property='Opacity' Value='0.4'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- ===== subtle/icon button (toolbar) ===== -->
  <Style x:Key='SubtleButton' TargetType='Button'>
    <Setter Property='FontFamily' Value='{StaticResource UiFont}'/>
    <Setter Property='FontSize' Value='13'/>
    <Setter Property='FontWeight' Value='Medium'/>
    <Setter Property='Foreground' Value='{StaticResource TextBrush}'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='SnapsToDevicePixels' Value='True'/>
    <Setter Property='Padding' Value='10,6,10,6'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border x:Name='b' CornerRadius='8' Background='Transparent'
                  Padding='{TemplateBinding Padding}' SnapsToDevicePixels='True'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='b' Property='Background' Value='{StaticResource SurfaceBrush}'/>
            </Trigger>
            <Trigger Property='IsPressed' Value='True'>
              <Setter TargetName='b' Property='Background' Value='{StaticResource SurfaceHiBrush}'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter TargetName='b' Property='Opacity' Value='0.35'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- ===== tool toggle (editor tools; shows active state) ===== -->
  <Style x:Key='ToolToggle' TargetType='ToggleButton'>
    <Setter Property='FontFamily' Value='{StaticResource UiFont}'/>
    <Setter Property='FontSize' Value='12.5'/>
    <Setter Property='FontWeight' Value='Medium'/>
    <Setter Property='Foreground' Value='{StaticResource MutedBrush}'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='SnapsToDevicePixels' Value='True'/>
    <Setter Property='Padding' Value='10,6,10,6'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ToggleButton'>
          <Border x:Name='b' CornerRadius='8' Background='Transparent'
                  Padding='{TemplateBinding Padding}' SnapsToDevicePixels='True'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='b' Property='Background' Value='{StaticResource SurfaceBrush}'/>
              <Setter Property='Foreground' Value='{StaticResource TextBrush}'/>
            </Trigger>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='b' Property='Background' Value='{StaticResource AccentBrush}'/>
              <Setter Property='Foreground' Value='White'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- ===== textbox ===== -->
  <Style x:Key='Field' TargetType='TextBox'>
    <Setter Property='FontFamily' Value='{StaticResource UiFont}'/>
    <Setter Property='FontSize' Value='13'/>
    <Setter Property='Foreground' Value='{StaticResource TextBrush}'/>
    <Setter Property='CaretBrush' Value='{StaticResource AccentBrush}'/>
    <Setter Property='SelectionBrush' Value='{StaticResource AccentBrush}'/>
    <Setter Property='VerticalContentAlignment' Value='Center'/>
    <Setter Property='Padding' Value='9,6,9,6'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='TextBox'>
          <Border x:Name='b' CornerRadius='8' Background='{StaticResource SurfaceBrush}'
                  BorderBrush='{StaticResource BorderBrush2}' BorderThickness='1' SnapsToDevicePixels='True'>
            <ScrollViewer x:Name='PART_ContentHost' Margin='{TemplateBinding Padding}'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='b' Property='BorderBrush' Value='{StaticResource BorderStrongBrush}'/>
            </Trigger>
            <Trigger Property='IsKeyboardFocused' Value='True'>
              <Setter TargetName='b' Property='BorderBrush' Value='{StaticResource AccentBrush}'/>
            </Trigger>
            <Trigger Property='IsReadOnly' Value='True'>
              <Setter TargetName='b' Property='Background' Value='{StaticResource PanelBrush}'/>
              <Setter Property='Foreground' Value='{StaticResource MutedBrush}'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- ===== checkbox ===== -->
  <Style x:Key='Toggle' TargetType='CheckBox'>
    <Setter Property='FontFamily' Value='{StaticResource UiFont}'/>
    <Setter Property='FontSize' Value='13'/>
    <Setter Property='Foreground' Value='{StaticResource TextBrush}'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='VerticalContentAlignment' Value='Center'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='CheckBox'>
          <StackPanel Orientation='Horizontal' Background='Transparent'>
            <Border x:Name='box' Width='18' Height='18' CornerRadius='5'
                    Background='{StaticResource SurfaceBrush}'
                    BorderBrush='{StaticResource BorderStrongBrush}' BorderThickness='1'
                    VerticalAlignment='Center' SnapsToDevicePixels='True'>
              <Path x:Name='tick' Stretch='Uniform' Margin='3.5' Stroke='White' StrokeThickness='2'
                    StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round'
                    Visibility='Collapsed' Data='M2,7 L6,11 L13,2'/>
            </Border>
            <ContentPresenter Margin='9,0,0,0' VerticalAlignment='Center'/>
          </StackPanel>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='box' Property='BorderBrush' Value='{StaticResource AccentBrush}'/>
            </Trigger>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='box' Property='Background' Value='{StaticResource AccentBrush}'/>
              <Setter TargetName='box' Property='BorderBrush' Value='{StaticResource AccentBrush}'/>
              <Setter TargetName='tick' Property='Visibility' Value='Visible'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- ===== combo box (dark dropdown — used by the settings language picker) ===== -->
  <Style x:Key='ComboItem' TargetType='ComboBoxItem'>
    <Setter Property='FontFamily' Value='{StaticResource UiFont}'/>
    <Setter Property='FontSize' Value='13'/>
    <Setter Property='Foreground' Value='{StaticResource TextBrush}'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Padding' Value='11,7,11,7'/>
    <Setter Property='SnapsToDevicePixels' Value='True'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ComboBoxItem'>
          <Border x:Name='b' Background='Transparent' CornerRadius='6' Margin='3,1,3,1'
                  Padding='{TemplateBinding Padding}' SnapsToDevicePixels='True'>
            <ContentPresenter VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsHighlighted' Value='True'>
              <Setter TargetName='b' Property='Background' Value='{StaticResource SurfaceHiBrush}'/>
            </Trigger>
            <Trigger Property='IsSelected' Value='True'>
              <Setter TargetName='b' Property='Background' Value='{StaticResource AccentBrush}'/>
              <Setter Property='Foreground' Value='White'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='Combo' TargetType='ComboBox'>
    <Setter Property='FontFamily' Value='{StaticResource UiFont}'/>
    <Setter Property='FontSize' Value='13'/>
    <Setter Property='Foreground' Value='{StaticResource TextBrush}'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='SnapsToDevicePixels' Value='True'/>
    <Setter Property='MaxDropDownHeight' Value='340'/>
    <Setter Property='ItemContainerStyle' Value='{StaticResource ComboItem}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ComboBox'>
          <Grid>
            <ToggleButton Focusable='False' ClickMode='Press'
                IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'>
              <ToggleButton.Template>
                <ControlTemplate TargetType='ToggleButton'>
                  <Border x:Name='bd' CornerRadius='8' Background='{StaticResource SurfaceBrush}'
                          BorderBrush='{StaticResource BorderBrush2}' BorderThickness='1' SnapsToDevicePixels='True'>
                    <Grid>
                      <Grid.ColumnDefinitions>
                        <ColumnDefinition Width='*'/>
                        <ColumnDefinition Width='Auto'/>
                      </Grid.ColumnDefinitions>
                      <Path Grid.Column='1' x:Name='arw' Margin='0,0,11,0' VerticalAlignment='Center'
                            Width='10' Height='6' Stretch='Uniform' Fill='{StaticResource MutedBrush}'
                            Data='M0,0 L5,5 L10,0 Z'/>
                    </Grid>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property='IsMouseOver' Value='True'>
                      <Setter TargetName='bd' Property='BorderBrush' Value='{StaticResource BorderStrongBrush}'/>
                    </Trigger>
                    <Trigger Property='IsChecked' Value='True'>
                      <Setter TargetName='bd' Property='BorderBrush' Value='{StaticResource AccentBrush}'/>
                      <Setter TargetName='arw' Property='Fill' Value='{StaticResource AccentBrush}'/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </ToggleButton.Template>
            </ToggleButton>
            <ContentPresenter IsHitTestVisible='False'
                Content='{TemplateBinding SelectionBoxItem}'
                ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}'
                Margin='11,7,30,7' VerticalAlignment='Center' HorizontalAlignment='Left'/>
            <Popup x:Name='PART_Popup' Placement='Bottom' AllowsTransparency='True' Focusable='False'
                   IsOpen='{TemplateBinding IsDropDownOpen}' PopupAnimation='Slide'>
              <Border MinWidth='{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}'
                      MaxHeight='{TemplateBinding MaxDropDownHeight}'
                      Background='{StaticResource Panel2Brush}' CornerRadius='8'
                      BorderBrush='{StaticResource BorderStrongBrush}' BorderThickness='1'
                      Margin='0,4,0,6' SnapsToDevicePixels='True'>
                <Border.Effect><DropShadowEffect BlurRadius='14' ShadowDepth='3' Opacity='0.45'/></Border.Effect>
                <ScrollViewer Margin='2'>
                  <ItemsPresenter/>
                </ScrollViewer>
              </Border>
            </Popup>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- ===== slider ===== -->
  <Style x:Key='Track' TargetType='Slider'>
    <Setter Property='Foreground' Value='{StaticResource AccentBrush}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Slider'>
          <Grid VerticalAlignment='Center' MinHeight='22'>
            <Border Height='4' CornerRadius='2' Background='{StaticResource SurfaceHiBrush}' VerticalAlignment='Center'/>
            <Track x:Name='PART_Track'>
              <Track.DecreaseRepeatButton>
                <RepeatButton Command='{x:Static Slider.DecreaseLarge}' Focusable='False'>
                  <RepeatButton.Template>
                    <ControlTemplate TargetType='RepeatButton'>
                      <Border Height='4' CornerRadius='2' Background='{StaticResource AccentBrush}' VerticalAlignment='Center'/>
                    </ControlTemplate>
                  </RepeatButton.Template>
                </RepeatButton>
              </Track.DecreaseRepeatButton>
              <Track.IncreaseRepeatButton>
                <RepeatButton Command='{x:Static Slider.IncreaseLarge}' Focusable='False'>
                  <RepeatButton.Template>
                    <ControlTemplate TargetType='RepeatButton'>
                      <Border Background='Transparent'/>
                    </ControlTemplate>
                  </RepeatButton.Template>
                </RepeatButton>
              </Track.IncreaseRepeatButton>
              <Track.Thumb>
                <Thumb Cursor='Hand'>
                  <Thumb.Template>
                    <ControlTemplate TargetType='Thumb'>
                      <Ellipse x:Name='th' Width='16' Height='16' Fill='White'>
                        <Ellipse.Effect><DropShadowEffect BlurRadius='6' ShadowDepth='1' Opacity='0.5'/></Ellipse.Effect>
                      </Ellipse>
                      <ControlTemplate.Triggers>
                        <Trigger Property='IsMouseOver' Value='True'>
                          <Setter TargetName='th' Property='Fill' Value='#EAF1FF'/>
                        </Trigger>
                      </ControlTemplate.Triggers>
                    </ControlTemplate>
                  </Thumb.Template>
                </Thumb>
              </Track.Thumb>
            </Track>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

</ResourceDictionary>";
}
