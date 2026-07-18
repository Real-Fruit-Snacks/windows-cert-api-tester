using System;
using System.Threading;
using System.Windows;

namespace ApiTester.Tests;

/// <summary>Constructs the StreamWindow with the real Terminal Workbench theme merged, on an STA
/// thread, to prove its XAML loads (StaticResource keys resolve, markup is well-formed) — the
/// failure mode a plain build can't catch, since StaticResource resolution happens at load time.</summary>
public class StreamWindowSmokeTests
{
    [Fact]
    public void StreamWindow_loads_with_the_theme()
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application();
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/ApiTester.App;component/Themes/TerminalWorkbench.xaml")
                });

                var win = new ApiTester.App.StreamWindow("wss://example.test/socket", null, insecure: false);
                win.Close();
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();

        Assert.True(error is null, error?.ToString());
    }

    [Fact]
    public void OAuthWindow_loads_with_the_theme()
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application();
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/ApiTester.App;component/Themes/TerminalWorkbench.xaml")
                });

                var win = new ApiTester.App.OAuthWindow(null, insecure: false, "https://api.example.com/orders");
                win.Close();
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();

        Assert.True(error is null, error?.ToString());
    }

    [Fact]
    public void MockServerWindow_loads_with_the_theme()
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application();
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/ApiTester.App;component/Themes/TerminalWorkbench.xaml")
                });

                var win = new ApiTester.App.MockServerWindow();
                win.Close();
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();

        Assert.True(error is null, error?.ToString());
    }
}
