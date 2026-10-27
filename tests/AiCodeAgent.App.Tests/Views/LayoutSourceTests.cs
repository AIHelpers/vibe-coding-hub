using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace AiCodeAgent.App.Tests.Views;

/// <summary>E4-S3: the XAML decisions behind the layout fixes, checked without a UI (the rendered check is LayoutOverlapTests).</summary>
public class LayoutSourceTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AiCodeAgent.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("AiCodeAgent.slnx not found above " + AppContext.BaseDirectory);
    }

    private static XDocument View(string name) =>
        XDocument.Load(Path.Combine(RepoRoot(), "src", "AiCodeAgent.App", "Views", name));

    private static XElement Find(XDocument doc, string name) =>
        doc.Descendants().First(e => (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == name
                                     || (string?)e.Attribute("Name") == name);

    [Fact]
    public void ChatActionBar_IsAWrapPanel_NotAHorizontalStackPanel()
    {
        var bar = View("ChatActionBarView.axaml");
        var panel = Find(bar, "ChatActionBar");
        Assert.Equal("WrapPanel", panel.Name.LocalName);
        foreach (var name in new[] { "PipelineCombo", "RunPipelineButton", "CharacterCombo", "EffortCombo", "RewindButton" })
            Assert.Same(panel, Find(bar, name).Parent);

        var main = View("MainWindow.axaml");
        Assert.Contains(main.Descendants(), e => e.Name.LocalName == "ChatActionBarView");
        Assert.DoesNotContain(main.Descendants(), e => (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "RewindButton");
    }

    [Fact]
    public void FlowsParallelSpinner_HasRoomForItsDigit()
    {
        var flows = View("FlowsView.axaml");
        var spinner = Find(flows, "MaxParallelBox");
        Assert.Equal("NumericUpDown", spinner.Name.LocalName);
        Assert.True(double.Parse((string)spinner.Attribute("MinWidth")!, CultureInfo.InvariantCulture) >= 90);
        Assert.Null(spinner.Attribute("Width")); // a fixed width would cap it below MinWidth

        Assert.Equal("WrapPanel", Find(flows, "HeaderTools").Name.LocalName);
    }

    [Fact]
    public void SettingsFields_AreLeftAligned_WithOneFieldWidth()
    {
        var settings = View("SettingsView.axaml");
        var fixedWidth = settings.Descendants()
            .Where(e => e.Name.LocalName is "ComboBox" or "TextBox" or "Grid" or "TextBlock")
            .Where(e => (string?)e.Attribute("Width") is "300" or "400")
            .ToList();
        Assert.True(fixedWidth.Count >= 8);
        Assert.All(fixedWidth, e => Assert.Equal("Left", (string?)e.Attribute("HorizontalAlignment")));

        // Pickers share the width of the first one (and no picker is left to stretch to the page width).
        foreach (var name in new[] { "ProviderComboBox", "ThemeComboBox" })
            Assert.Equal("300", (string?)Find(settings, name).Attribute("Width"));
    }

    [Fact]
    public void FlowsPaletteAndNodes_DoNotRepeatTheAvatarInTheLabel()
    {
        var vm = File.ReadAllText(Path.Combine(RepoRoot(), "src", "AiCodeAgent.App", "ViewModels", "FlowEditorViewModel.cs"));
        Assert.DoesNotContain("Label = c.Label", vm);
        Assert.DoesNotContain("CharacterLabel = c.Label", vm);
        Assert.Matches(new Regex(@"Label = c\.DisplayName"), vm);
        Assert.Matches(new Regex(@"CharacterLabel = c\.DisplayName"), vm);
    }
}
