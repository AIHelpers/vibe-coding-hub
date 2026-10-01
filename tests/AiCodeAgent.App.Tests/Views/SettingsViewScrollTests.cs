using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.Core.Configuration;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(AiCodeAgent.App.Tests.Views.HeadlessTestSetup))]

namespace AiCodeAgent.App.Tests.Views;

/// <summary>
/// Headless Avalonia application used by the UI tests. Configures the
/// FluentTheme so visual styles resolve like the real app does.
/// </summary>
public static class HeadlessTestSetup
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<HeadlessTestApplication>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public class HeadlessTestApplication : Application
{
    public override void Initialize()
    {
        this.Styles.Add(new FluentTheme());
    }
}

public class SettingsViewScrollTests
{
    /// <summary>Minimal stand-in for MainViewModel's navigation bindings.</summary>
    private sealed class MainVmStub : INotifyPropertyChanged
    {
        private object? _currentViewModel;
        private bool _isSettingsMode;

        public object? CurrentViewModel
        {
            get => _currentViewModel;
            set { _currentViewModel = value; OnPropertyChanged(nameof(CurrentViewModel)); }
        }

        public bool IsSettingsMode
        {
            get => _isSettingsMode;
            set { _isSettingsMode = value; OnPropertyChanged(nameof(IsSettingsMode)); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private static (Window Window, AiCodeAgent.App.Views.SettingsView View, string TempDir) BuildHost()
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(), "settings-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var vm = new SettingsViewModel(new ConfigurationService(tempDir));
            var view = new AiCodeAgent.App.Views.SettingsView { DataContext = vm };

            // Deliberately short window so the settings page must scroll.
            var window = new Window { Width = 800, Height = 400, Content = view };
            window.Show();

            // Layout runs on the dispatcher; pump it until settled so Extent /
            // Viewport reflect the real 400px-tall window constraints.
            for (var i = 0; i < 5 && view.Bounds == default; i++)
                Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();

            return (window, view, tempDir);
        }
        catch
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
            throw;
        }
    }

    /// <summary>
    /// The settings fields must be scrollable so every control can be brought
    /// on screen: the content ScrollViewer must be height-bounded (Extent >
    /// Viewport on a short window) and a mouse wheel over the fields must
    /// move the offset, including all the way to the bottom.
    /// </summary>
    [AvaloniaFact]
    public void Settings_Content_Is_Scrollable_And_Reaches_Bottom()
    {
        var (window, view, tempDir) = BuildHost();
        try
        {
            var sv = view
                .GetVisualDescendants()
                .OfType<ScrollViewer>()
                .FirstOrDefault();

            Assert.NotNull(sv);
            Assert.True(
                sv!.Viewport.Height > 0,
                $"Viewport must be measured but was {sv.Viewport.Height}");
            Assert.True(
                sv.Extent.Height > 0,
                $"Extent must be measured but was {sv.Extent.Height}");

            // On a 400px-tall window the settings content (~700px tall) must
            // overflow the viewport, otherwise nothing can ever be scrolled.
            Assert.True(
                sv.Extent.Height > sv.Viewport.Height + 1,
                $"ScrollViewer is not scrollable: Extent {sv.Extent.Height} vs Viewport {sv.Viewport.Height} " +
                "(an ancestor is passing it an unbounded/unclamped height)");

            // Simulate a real mouse-wheel over the middle of the fields area.
            var topLeft = sv.TranslatePoint(new Point(0, 0), window)!.Value;
            var center = topLeft + new Point(sv.Bounds.Width / 2, sv.Bounds.Height / 2);
            window.MouseWheel(center, new Vector(0, -500));
            Dispatcher.UIThread.RunJobs();

            Assert.True(
                sv.Offset.Y > 0,
                $"Mouse wheel must scroll the settings content but Offset.Y stayed {sv.Offset.Y}");

            // Scrolling to the very bottom must be possible.
            window.MouseWheel(center, new Vector(0, -3000));
            Dispatcher.UIThread.RunJobs();
            var expected = sv.Extent.Height - sv.Viewport.Height;
            Assert.True(
                Math.Abs(sv.Offset.Y - expected) <= 1,
                $"Cannot reach the bottom: Offset.Y={sv.Offset.Y}, expected {expected}");
        }
        finally
        {
            window.Close();
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// End-to-end reproduction of the real hosting chain from MainWindow:
    /// Window → RootGrid(Auto,*,Auto) → ContentArea → ContentControl whose
    /// Content resolves through a lazy SettingsView DataTemplate, with
    /// navigation happening only after the window is shown and laid out —
    /// exactly how NavigateToSettings works at runtime. Verifies at several
    /// window sizes that wheel scrolling reaches the very bottom and that
    /// the single pinned footer Save button stays fully on screen.
    /// </summary>
    [AvaloniaFact]
    public void Settings_Chain_Scrolls_To_Bottom_From_Real_Navigation_Path()
    {
        var createdDirs = new List<string>();
        try
        {
            foreach (var (winWidth, winHeight) in new[] { (1200, 800), (1000, 600), (800, 400) })
            {
                var tempDir = Path.Combine(
                    Path.GetTempPath(), "settings-chain-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                createdDirs.Add(tempDir);

                var settingsVm = new SettingsViewModel(new ConfigurationService(tempDir));
                var mainVm = new MainVmStub { CurrentViewModel = new object(), IsSettingsMode = false };

                // Mirror MainWindow's chain.
                var rootGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
                var header = new Border { Height = 44 };
                Grid.SetRow(header, 0);
                rootGrid.Children.Add(header);

                var contentArea = new Grid { RowDefinitions = new RowDefinitions("*"), ClipToBounds = true };
                Grid.SetRow(contentArea, 1);

                var settingsHost = new ContentControl
                {
                    ClipToBounds = true,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch
                };
                settingsHost.DataTemplates.Add(
                    new FuncDataTemplate(typeof(SettingsViewModel), (_, _) => new AiCodeAgent.App.Views.SettingsView()));
                settingsHost.Bind(Visual.IsVisibleProperty, new Binding(nameof(MainVmStub.IsSettingsMode)));
                settingsHost.Bind(ContentControl.ContentProperty, new Binding(nameof(MainVmStub.CurrentViewModel)));
                contentArea.Children.Add(settingsHost);
                rootGrid.Children.Add(contentArea);

                var window = new Window { Width = winWidth, Height = winHeight, Content = rootGrid };
                window.DataContext = mainVm;
                window.Show();

                for (var i = 0; i < 20 && window.Bounds == default; i++)
                    Dispatcher.UIThread.RunJobs();
                Dispatcher.UIThread.RunJobs();

                // Navigate to settings exactly like NavigateToSettings does
                // at runtime: only after the window is shown and laid out.
                mainVm.CurrentViewModel = settingsVm;
                mainVm.IsSettingsMode = true;
                Dispatcher.UIThread.RunJobs();

                var sv = settingsHost
                    .GetVisualDescendants()
                    .OfType<ScrollViewer>()
                    .FirstOrDefault();
                Assert.True(sv != null, $"[{winWidth}x{winHeight}] ScrollViewer missing after navigation");
                Assert.True(
                    sv!.Viewport.Height > 0,
                    $"[{winWidth}x{winHeight}] Viewport not measured ({sv.Viewport.Height})");
                Assert.True(
                    sv.Extent.Height > 0,
                    $"[{winWidth}x{winHeight}] Extent not measured ({sv.Extent.Height})");

                var topLeft = sv.TranslatePoint(new Point(0, 0), window)!.Value;
                var center = topLeft + new Point(sv.Bounds.Width / 2, sv.Bounds.Height / 2);
                var overflow = sv.Extent.Height - sv.Viewport.Height;

                if (overflow > 1)
                {
                    // Content overflows: wheel must scroll it, including all
                    // the way down to the very bottom.
                    window.MouseWheel(center, new Vector(0, -500));
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(
                        sv.Offset.Y > 0,
                        $"[{winWidth}x{winHeight}] wheel did not scroll (Offset.Y={sv.Offset.Y})");

                    window.MouseWheel(center, new Vector(0, -3000));
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(
                        Math.Abs(sv.Offset.Y - overflow) <= 1,
                        $"[{winWidth}x{winHeight}] cannot reach bottom: Offset.Y={sv.Offset.Y}, expected {overflow}");
                }
                else
                {
                    // Nothing to scroll at this size: offset must be zero, so
                    // nothing can ever be "stuck below the fold".
                    Assert.True(
                        sv.Offset.Y <= 0.5,
                        $"[{winWidth}x{winHeight}] nothing to scroll but Offset.Y={sv.Offset.Y}");
                }

                // The single pinned Save button must be fully on screen.
                var saveButton = settingsHost
                    .GetVisualDescendants()
                    .OfType<Button>()
                    .FirstOrDefault(b => (b.Content as string)?.Contains("Save Settings") == true);
                Assert.True(saveButton != null, $"[{winWidth}x{winHeight}] Save button missing");
                var saveTopLeft = saveButton!.TranslatePoint(new Point(0, 0), window)!.Value;
                var saveBottomRight = saveButton.TranslatePoint(
                    new Point(saveButton.Bounds.Width, saveButton.Bounds.Height), window)!.Value;
                Assert.True(
                    saveButton.IsEffectivelyVisible &&
                    saveTopLeft.Y >= 0 && saveBottomRight.Y <= window.Bounds.Height + 0.5 &&
                    saveTopLeft.X >= 0 && saveBottomRight.X <= window.Bounds.Width + 0.5,
                    $"[{winWidth}x{winHeight}] Save button not fully on screen: " +
                    $"{saveTopLeft}..{saveBottomRight}, window {window.Bounds.Size}");

                window.Close();
            }
        }
        finally
        {
            foreach (var dir in createdDirs)
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }
}