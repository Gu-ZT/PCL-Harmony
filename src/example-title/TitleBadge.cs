using System;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HarmonyLib;
using PclHarmony;

namespace PclExampleTitlePlugin
{
    /// <summary>
    /// Draws a "Harness" badge immediately after the launcher's title wordmark.
    ///
    /// Appearance: a white rounded pill the size of a small label, with the letters
    /// knocked out of it - the glyph strokes are transparent, so what shows through
    /// them is whatever sits behind the title bar. The typeface matches the title's
    /// own and the size is half of it.
    ///
    /// Why a Path and not a TextBlock: knocking text out of a shape needs the plate
    /// as a geometry with the glyph geometry subtracted. A TextBlock cannot be
    /// subtracted from anything, so the text is turned into glyph outlines
    /// (FormattedText.BuildGeometry) and the plate is built with
    /// CombinedGeometry(Exclude).
    /// </summary>
    public sealed class TitleBadge : IPlugin
    {
        public string Name { get { return "PCL.Example.TitleBadge"; } }

        private const string BadgeText = "Harmony";
        private const string BadgeName = "PclHarmonyTitleBadge";

        /// <summary>
        /// Badge font size as a fraction of the title's.
        ///
        /// Tuned against the real title bar: at three quarters the pill grew taller
        /// than the space above/below the wordmark and clipped at the top edge, so
        /// it sits a little smaller than that.
        /// </summary>
        private const double SizeRatio = 0.62;

        private static Assembly _target;

        // Typography borrowed from the launcher's text-form wordmark, kept so the
        // badge and the descender correction stay in step even when the visible
        // wordmark is a vector or an image (those carry no font of their own).
        private static double _typographySize = 17;
        private static Typeface _typographyFace;

        public void Load(Harmony harmony, Assembly target)
        {
            _target = target;

            MethodInfo refresh = FindTitleRefresh();
            if (refresh == null)
            {
                PluginApi.Log("[Example-Title] title refresh method not found; badge disabled");
                return;
            }

            harmony.Patch(refresh, null, new HarmonyMethod(P("AfterTitleRefresh")), null);
            PluginApi.Log("[Example-Title] badge attached to "
                          + refresh.DeclaringType.Name + "." + refresh.Name);
        }

        /// <summary>
        /// Locates the method that assigns the title wordmark. The title text comes
        /// from user settings and is re-applied whenever they change, so hooking that
        /// assignment is both "the control exists" and "the font is current".
        /// </summary>
        private static MethodInfo FindTitleRefresh()
        {
            try
            {
                var frm = T("PCL.FormMain");
                if (frm == null) return null;

                // Preferred: the known title-bar refresh routine.
                var m = frm.GetMethod("UpdateBackgroundAndTitleBar",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (m != null) return m;

                // Fallback: the parameterless method that calls the LabTitleLogo setter.
                var labelProp = frm.GetProperty("LabTitleLogo",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                var setter = labelProp == null ? null : labelProp.GetSetMethod(true);
                if (setter == null) return null;

                foreach (var candidate in frm.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                                         | BindingFlags.Instance
                                                         | BindingFlags.DeclaredOnly))
                {
                    if (candidate.GetParameters().Length != 0) continue;
                    if (candidate.ReturnType != typeof(void)) continue;

                    byte[] il;
                    try { il = candidate.GetMethodBody() == null ? null : candidate.GetMethodBody().GetILAsByteArray(); }
                    catch { continue; }
                    if (il == null) continue;

                    for (int i = 0; i + 4 < il.Length; i++)
                    {
                        if (il[i] != 0x6F && il[i] != 0x28) continue;   // callvirt / call
                        if (BitConverter.ToInt32(il, i + 1) == setter.MetadataToken) return candidate;
                    }
                }
            }
            catch (Exception ex)
            {
                PluginApi.Log("[Example-Title] lookup failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            return null;
        }

        /// <summary>
        /// Runs after the title has been refreshed. Never throws into the launcher:
        /// a decoration must not be able to break startup.
        /// </summary>
        private static void AfterTitleRefresh(object __instance)
        {
            try { Attach(__instance); }
            catch (Exception ex)
            {
                PluginApi.Log("[Example-Title] attach failed (launcher unaffected): "
                              + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Attach(object formMain)
        {
            var host = GetProp(formMain, "PanTitleMain") as Panel;
            if (host == null)
            {
                PluginApi.Log("[Example-Title] PanTitleMain unavailable; badge skipped");
                return;
            }

            // The launcher renders its wordmark in one of three ways depending on
            // the UiLogoType setting: a vector Path (1), a TextBlock (2), or an
            // image (3). Only one is visible at a time, so the badge anchors to
            // whichever that is.
            FrameworkElement wordmark = FirstVisible(
                GetProp(formMain, "ShapeTitleLogo") as FrameworkElement,
                GetProp(formMain, "LabTitleLogo") as FrameworkElement,
                GetProp(formMain, "ImageTitleLogo") as FrameworkElement);

            if (wordmark == null)
            {
                PluginApi.Log("[Example-Title] no visible wordmark; badge skipped");
                return;
            }

            // Typography comes from the text form even when another form is shown:
            // it carries the launcher's font family and the size the wordmark is
            // drawn at, and it stays configured while collapsed.
            var textForm = GetProp(formMain, "LabTitleLogo") as TextBlock;
            if (textForm != null && textForm.FontSize > 0)
            {
                _typographySize = textForm.FontSize;
                _typographyFace = new Typeface(textForm.FontFamily, textForm.FontStyle,
                                               textForm.FontWeight, FontStretches.Normal);
            }

            FrameworkElement badge = FindExisting(host) ?? BuildBadge(textForm);
            if (badge.Parent == null)
            {
                badge.Name = BadgeName;
                badge.HorizontalAlignment = HorizontalAlignment.Left;
                badge.VerticalAlignment = VerticalAlignment.Center;
                badge.Margin = new Thickness(8, 0, 0, 0);
                badge.IsHitTestVisible = false;   // never steal clicks from the title bar
                PlaceAfter(host, wordmark, badge);
            }

            PluginApi.Log("[Example-Title] badge placed after " + wordmark.GetType().Name
                          + " size=" + (badge as FrameworkElement).Width
                          + "x" + (badge as FrameworkElement).Height);
        }

        private static FrameworkElement FirstVisible(params FrameworkElement[] candidates)
        {
            foreach (var c in candidates)
                if (c != null && c.Visibility == Visibility.Visible) return c;
            return null;
        }

        private static FrameworkElement FindExisting(Panel host)
        {
            foreach (UIElement child in host.Children)
            {
                var fe = child as FrameworkElement;
                if (fe != null && fe.Name == BadgeName) return fe;
            }
            return null;
        }

        /// <summary>
        /// Places the badge so it reads as part of the wordmark: a horizontal
        /// StackPanel wrapping the mark and the badge, put back into the exact
        /// cell the wordmark occupied.
        ///
        /// Adding the badge to the next grid column does not work here: the
        /// title bar's remaining columns hold the navigation buttons, so the badge
        /// would land on top of them.
        /// </summary>
        private static void PlaceAfter(Panel host, FrameworkElement wordmark, FrameworkElement badge)
        {
            var grid = host as Grid;
            if (grid == null)
            {
                host.Children.Add(badge);
                return;
            }

            // Pull the wordmark out of the grid, wrap it with the badge, and put the
            // wrapper back in the same place. Grid/margin/alignment are transferred
            // so the wordmark does not visually move.
            int col = Grid.GetColumn(wordmark);
            int row = Grid.GetRow(wordmark);
            Thickness margin = wordmark.Margin;
            HorizontalAlignment hAlign = wordmark.HorizontalAlignment;
            VerticalAlignment vAlign = wordmark.VerticalAlignment;

            grid.Children.Remove(wordmark);

            var wrap = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = hAlign,
                Margin = margin,
            };
            wordmark.Margin = new Thickness(0);
            wordmark.VerticalAlignment = VerticalAlignment.Center;
            badge.Margin = new Thickness(8, 0, 0, 0);

            // The pill's bottom edge must line up with the bottom of the wordmark's
            // glyphs. Bottom-aligning the two elements is not enough on its own: a
            // TextBlock's box includes the font's descender space, which the letters
            // do not fill, so the pill would sit slightly low. The correction is the
            // unused descender of the wordmark's font, expressed in device pixels.
            badge.VerticalAlignment = VerticalAlignment.Bottom;
            double sink = DescenderSlack(wordmark);
            wrap.Children.Add(wordmark);
            wrap.Children.Add(badge);

            Grid.SetColumn(wrap, col);
            Grid.SetRow(wrap, row);
            grid.Children.Add(wrap);

            // Nudge after layout, once the wordmark's box has a real height.
            //
            // A POSITIVE bottom margin would push the badge up, away from the ink
            // line, so the correction is applied as a negative margin - the plate
            // is pulled down by exactly the unused descender space.
            EventHandler apply = delegate
            {
                try
                {
                    if (wordmark.ActualHeight <= 0) return;
                    badge.Margin = new Thickness(8, 0, 0, -sink);
                }
                catch { }
            };
            wordmark.LayoutUpdated += apply;
            apply(null, EventArgs.Empty);
        }

        /// <summary>
        /// How far the wordmark's text box extends below its glyphs, in pixels.
        ///
        /// A line box is (ascent + descent) tall while the letters sit on the
        /// baseline, so the gap under the glyphs is the descent scaled by the same
        /// factor the wordmark is drawn at. Used as a bottom margin so the badge's
        /// plate ends where the letters end.
        /// </summary>
        private static double DescenderSlack(FrameworkElement wordmark)
        {
            try
            {
                var text = wordmark as TextBlock;
                double size = 0;
                Typeface face = null;

                if (text != null)
                {
                    size = text.FontSize;
                    face = new Typeface(text.FontFamily, text.FontStyle,
                                        text.FontWeight, FontStretches.Normal);
                }
                else
                {
                    // The vector wordmark carries no font of its own; the badge was
                    // sized from the launcher's text form, so mirror that size.
                    size = _typographySize;
                    face = _typographyFace;
                }
                if (face == null || size <= 0) return 0;

                double basePx = 100.0;
                var probe = new FormattedText("Hxg", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, face, basePx, Brushes.Black, 1.0);

                // descent = height - baseline, at the probe size; scale to real size.
                double descentRatio = (probe.Height - probe.Baseline) / basePx;

                // The wordmark's box reaches further down than its ink; pull the plate
                // down by part of that gap so the pill's lower edge meets the letters'
                // bottom rather than the box's.
                double slack = descentRatio * size * 0.25;
                return slack < 0 ? 0 : slack;
            }
            catch { return 0; }
        }

        /// <summary>
        /// Builds the pill: a rounded plate geometry with the glyph outlines removed.
        /// </summary>
        private static FrameworkElement BuildBadge(TextBlock label)
        {
            double titleSize = label != null && label.FontSize > 0 ? label.FontSize : 17;
            FontFamily family = label != null && label.FontFamily != null
                ? label.FontFamily
                : new FontFamily("Segoe UI");
            FontStyle style = label != null ? label.FontStyle : FontStyles.Normal;
            FontWeight weight = label != null ? label.FontWeight : FontWeights.Normal;

            var typeface = new Typeface(family, style, weight, FontStretches.Normal);
            var text = new FormattedText(
                BadgeText,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                titleSize * SizeRatio,
                Brushes.Black,
                VisualTreeHelper.GetDpi(label ?? new TextBlock()).PixelsPerDip);

            const double padX = 7.0;
            const double padY = 2.5;
            double width = text.Width + padX * 2;
            double height = text.Height + padY * 2;

            // Glyph outlines, moved into the plate's padding box.
            Geometry glyphs = text.BuildGeometry(new Point(padX, padY));

            // Plate = rounded rectangle minus the glyphs, so the letters are holes.
            var plateRect = new RectangleGeometry(new Rect(0, 0, width, height), height / 2, height / 2);
            var plate = new CombinedGeometry(GeometryCombineMode.Exclude, plateRect, glyphs);

            var shape = new System.Windows.Shapes.Path
            {
                Data = plate,
                Fill = Brushes.White,
                Width = width,
                Height = height,
                Stretch = Stretch.None,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                SnapsToDevicePixels = true,
            };

            var host = new Grid
            {
                Width = width,
                Height = height,
                Background = Brushes.Transparent,
            };
            host.Children.Add(shape);
            return host;
        }

        // ---------------------------------------------------------- helpers ---
        private static Type T(string full)
        {
            return _target == null ? null : _target.GetType(full, false);
        }

        private static object GetProp(object instance, string name)
        {
            if (instance == null) return null;
            var p = instance.GetType().GetProperty(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return p == null ? null : p.GetValue(instance, null);
        }

        private static MethodInfo P(string name)
        {
            return typeof(TitleBadge).GetMethod(name,
                BindingFlags.NonPublic | BindingFlags.Static);
        }
    }
}
