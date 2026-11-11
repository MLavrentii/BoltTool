using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Windows;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace WS.BoltTool.UI
{
    /// <summary>Adds a "Wind-Smile" ribbon tab with buttons for the Bolt Tool commands.</summary>
    public static class RibbonUi
    {
        const string TabId = "WS_BOLTTOOL_TAB";

        public static void Create()
        {
            if (ComponentManager.Ribbon != null) Build();
            else ComponentManager.ItemInitialized += OnItemInitialized;
        }

        static void OnItemInitialized(object sender, RibbonItemEventArgs e)
        {
            if (ComponentManager.Ribbon == null) return;
            ComponentManager.ItemInitialized -= OnItemInitialized;
            Build();
        }

        static void Build()
        {
            try
            {
                var ribbon = ComponentManager.Ribbon;
                if (ribbon.FindTab(TabId) != null) return;
                var tab = new RibbonTab { Title = "Wind-Smile", Id = TabId };
                var src = new RibbonPanelSource { Title = "Bolt Tool" };
                src.Items.Add(Button("Bolt Tool", "BOLTTOOL", "Create bolt, nut and washer blocks", "B"));
                src.Items.Add(Button("Edit Bolt", "BOLTEDIT", "Edit a placed Bolt Tool block", "E"));
                src.Items.Add(Button("Torque", "BOLTTORQUE", "Tightening torque calculator", "T"));
                tab.Panels.Add(new RibbonPanel { Source = src });
                ribbon.Tabs.Add(tab);
            }
            catch { /* the ribbon is optional - commands still work from the command line */ }
        }

        static RibbonButton Button(string text, string command, string tip, string letter) => new RibbonButton
        {
            Text = text, Id = "WS_" + command, ShowText = true, ShowImage = true,
            Size = RibbonItemSize.Large, Orientation = System.Windows.Controls.Orientation.Vertical,
            LargeImage = Icon(letter, 32), Image = Icon(letter, 16),
            ToolTip = tip, CommandParameter = command, CommandHandler = new Handler()
        };

        /// <summary>Simple hexagon icon with a letter, drawn in code (no image files needed).</summary>
        static ImageSource Icon(string letter, int size)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                double c = size / 2.0, r = size * 0.46;
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    for (int i = 0; i < 6; i++)
                    {
                        double a = Math.PI / 3 * i;
                        var p = new Point(c + r * Math.Cos(a), c + r * Math.Sin(a));
                        if (i == 0) ctx.BeginFigure(p, true, true); else ctx.LineTo(p, true, true);
                    }
                }
                dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xEB)), null, g);
                var ft = new FormattedText(letter, System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                    size * 0.55, Brushes.White, 1.0);
                dc.DrawText(ft, new Point(c - ft.Width / 2, c - ft.Height / 2));
            }
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            bmp.Freeze();
            return bmp;
        }

        class Handler : ICommand
        {
            public event EventHandler CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object parameter) => true;
            public void Execute(object parameter)
            {
                if (parameter is RibbonButton b && b.CommandParameter is string cmd)
                    AcApp.DocumentManager.MdiActiveDocument?.SendStringToExecute("_" + cmd + " ", true, false, false);
            }
        }
    }
}
