using System;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ProbeApp;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Position = new Avalonia.PixelPoint(200, 200);
        ClickMe.Click += (_, _) =>
            Program.Log("[ui] button click: real click reached button (expected for real clicks)");
        Opened += (_, _) =>
        {
            Activate();
            Program.Log("[ui] window opened (topmost, manual pos 200,200)");
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
            t.Tick += (_, _) => Close();
            t.Start();
        };
        Closed += (_, _) => Program.Log("[ui] window closed");
    }
}
