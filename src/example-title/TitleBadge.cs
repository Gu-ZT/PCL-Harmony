using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PclHarmony.Contract;

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
    public sealed class TitleBadge : IDeclarativePlugin
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
        private static IPluginHost _host;

        // Typography borrowed from the launcher's text-form wordmark, kept so the
        // badge and the descender correction stay in step even when the visible
        // wordmark is a vector or an image (those carry no font of their own).
        private static double _typographySize = 17;
        private static Typeface _typographyFace;

        internal static void Log(string message)
        {
            try { if (_host != null) _host.Log("[Example-Title] " + message); } catch { }
        }

        /// <summary>
        /// The launcher assembly, resolved lazily.
        ///
        /// This cannot be captured while declaring patches: on PCL-CE the hook runs
        /// before the launcher assembly is loaded. The hooks only need it once they
        /// actually run, by which point it is present.
        /// </summary>
        private static Assembly Target
        {
            get
            {
                if (_target != null) return _target;
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        if (a.GetName().Name == "Plain Craft Launcher 2") { _target = a; break; }
                    }
                    catch { }
                }
                return _target;
            }
        }

        public IEnumerable<PatchSpec> GetPatches(string hostKind, string hostVersion, IPluginHost host)
        {
            _host = host;
            Log("declaring patches for host=" + hostKind + " version=" + hostVersion);

            var specs = new List<PatchSpec>();
            if (string.Equals(hostKind, "ce", StringComparison.OrdinalIgnoreCase))
            {
                // PCL-CE: the window's Loaded handler is the point where the title bar
                // controls exist and are laid out.
                specs.Add(new PatchSpec
                {
                    TypeName = "PCL.FormMain",
                    MethodName = "FormMain_Loaded",
                    HookKind = "postfix",
                    HookTypeName = typeof(TitleBadge).FullName,
                    HookMethodName = "AfterCeWindowLoaded",
                });
            }
            else
            {
                specs.Add(new PatchSpec
                {
                    TypeName = "PCL.FormMain",
                    MethodName = "UpdateBackgroundAndTitleBar",
                    HookKind = "postfix",
                    HookTypeName = typeof(TitleBadge).FullName,
                    HookMethodName = "AfterTitleRefresh",
                    // Two overloads share this name: the parameterless instance method
                    // that refreshes the bar, and a static one taking the changed
                    // setting. Only the instance one runs at startup, so pin the
                    // parameter list to zero arguments.
                    TargetParameterTypes = new string[0],
                });
            }
            return specs;
        }

        /// <summary>
        /// Runs after the official launcher has refreshed its title bar. Never throws
        /// into the launcher: a decoration must not be able to break startup.
        /// </summary>
        public static void AfterTitleRefresh(object __instance)
        {
            try { Attach(__instance, null); }
            catch (Exception ex)
            {
                Log("attach failed (launcher unaffected): " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Runs after PCL-CE's main window has loaded. Anchors to the CE mark, which
        /// is the element that follows the wordmark in that title bar.
        /// </summary>
        public static void AfterCeWindowLoaded(object __instance)
        {
            try { Attach(__instance, "CELogo"); }
            catch (Exception ex)
            {
                Log("attach failed (launcher unaffected): " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Adds the badge next to the title mark.
        /// </summary>
        /// <param name="anchorName">
        /// Control to sit after. Null picks whichever wordmark form is visible.
        /// </param>
        private static void Attach(object formMain, string anchorName)
        {
            FrameworkElement anchor;
            if (anchorName != null)
            {
                anchor = GetProp(formMain, anchorName) as FrameworkElement;
            }
            else
            {
                // The launcher renders its wordmark in one of three ways depending on
                // the UiLogoType setting: a vector Path (1), a TextBlock (2), or an
                // image (3). Only one is visible at a time, so the badge anchors to
                // whichever that is.
                anchor = FirstVisible(
                    GetProp(formMain, "ShapeTitleLogo") as FrameworkElement,
                    GetProp(formMain, "LabTitleLogo") as FrameworkElement,
                    GetProp(formMain, "ImageTitleLogo") as FrameworkElement);
            }

            if (anchor == null)
            {
                Log("title mark '" + (anchorName ?? "(visible wordmark)") + "' not found; badge skipped");
                return;
            }

            // The panel that actually owns the anchor. It is not always PanTitleMain:
            // on PCL-CE the CE mark lives inside the nested wordmark grid, so taking
            // the parent from the anchor is the only reliable way.
            var host = anchor.Parent as Panel;
            if (host == null)
            {
                Log("title mark '" + anchor.GetType().Name + "' has no panel parent; badge skipped");
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
                PlaceAfter(host, anchor, badge);
            }

            Log("badge placed after " + anchor.GetType().Name
                + " size=" + badge.Width + "x" + badge.Height);
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
        private static object GetProp(object instance, string name)
        {
            if (instance == null) return null;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            // WPF's x:Name generates a FIELD in C# but a PROPERTY in VB, and the two
            // launchers are written in different languages - so check both.
            PropertyInfo p = instance.GetType().GetProperty(name, flags);
            if (p != null) return p.GetValue(instance, null);

            FieldInfo f = instance.GetType().GetField(name, flags);
            return f == null ? null : f.GetValue(instance);
        }
    }
}
