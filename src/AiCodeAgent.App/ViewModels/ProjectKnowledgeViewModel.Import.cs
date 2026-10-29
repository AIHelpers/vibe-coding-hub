using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Context;
using AiCodeAgent.Core.Import;
using AiCodeAgent.Core.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiCodeAgent.App.ViewModels;

/// <summary>One row of the import preview: a checkbox, where it comes from and where it goes.</summary>
public sealed partial class ImportListItem : ObservableObject
{
    public ImportListItem(ImportItem item) => Item = item;

    public ImportItem Item { get; }
    public string Label => Item.Label;
    public string Target => Item.TargetText;
    public bool CanSelect => Item.IsNew;
    public string Note => !Item.IsNew ? "already imported" : string.Join("; ", Item.Notes);
    public bool HasNote => Note.Length > 0;
    public string Group => ImportPlan.SourceTitle(Item.Source);

    public bool IsSelected
    {
        get => Item.Selected && Item.IsNew;
        set
        {
            if (Item.Selected == value) return;
            Item.Selected = value;
            OnPropertyChanged();
        }
    }
}

/// <summary>E6-S2: Project Knowledge → Import…: bring skills and rules over from Claude Code, Cursor, Kiro and Copilot.</summary>
public partial class ProjectKnowledgeViewModel
{
    private ImportPlan? _importPlan;

    public ObservableCollection<ImportListItem> ImportItems { get; } = new();

    public bool IsImportTabActive => ActiveTabIndex == 4;
    public bool HasImportItems => ImportItems.Count > 0;
    public bool HasNoImportItems => ImportItems.Count == 0;

    [ObservableProperty] private string _importStatusText = string.Empty;
    [ObservableProperty] private bool _isImportBusy;

    private ProjectImporter? NewImporter() =>
        _skillRegistry == null || string.IsNullOrEmpty(WorkingDirectory)
            ? null
            : new ProjectImporter(WorkingDirectory, SkillRegistry.GetDefaultProjectSkillsDir(WorkingDirectory),
                _skillRegistry.GlobalSkillsDirectory, ProjectImporter.DefaultUserClaudeSkills());

    /// <summary>"Import…": opens the Import tab and looks for what can be imported.</summary>
    [RelayCommand]
    private void BeginImport()
    {
        ActiveTabIndex = 4;
        ScanForImport();
    }

    [RelayCommand]
    private void ScanForImport()
    {
        ImportItems.Clear();
        _importPlan = null;
        var importer = NewImporter();
        if (importer == null)
        {
            ImportStatusText = "Open a project first.";
        }
        else
        {
            _importPlan = importer.Plan();
            foreach (var item in _importPlan.Items) ImportItems.Add(new ImportListItem(item));
            ImportStatusText = _importPlan.IsEmpty
                ? "Nothing to import: no Claude Code, Cursor, Kiro or Copilot files found in this project."
                : _importPlan.Pending.Any()
                    ? "Untick what you do not want, then import. Your source files are never changed or deleted."
                    : "Everything found is already imported.";
            if (_importPlan.Warnings.Count > 0) ImportStatusText += " " + string.Join(" ", _importPlan.Warnings);
        }
        OnPropertyChanged(nameof(HasImportItems));
        OnPropertyChanged(nameof(HasNoImportItems));
    }

    [RelayCommand]
    private async Task RunImportAsync()
    {
        var importer = NewImporter();
        if (importer == null || _importPlan == null || !_importPlan.Pending.Any()) return;
        IsImportBusy = true;
        try
        {
            var outcomes = await importer.ApplyAsync(_importPlan);
            var done = outcomes.Count(o => o.Kind == ImportResultKind.Imported);
            var failed = outcomes.Where(o => o.Kind == ImportResultKind.Failed).Select(o => $"{o.Item.Label}: {o.Message}").ToList();

            _skillRegistry?.Refresh();
            _characterRegistry?.Refresh();
            await RefreshMemoryAsync();
            await RefreshSkillsAsync();
            await Characters.RefreshAsync();

            var problems = 0;
            if (_skillRegistry != null)
            {
                var checks = await SkillDoctor.DiagnoseAsync(_skillRegistry, _characterRegistry, _presets);
                problems = checks.Count(c => c.Status == DoctorStatus.Error);
            }
            ImportStatusText = $"Imported {done} item(s)." +
                               (problems > 0 ? $" Skills doctor found {problems} problem(s); see the Skills tab." : " Skills doctor is clean.") +
                               (failed.Count > 0 ? " Failed: " + string.Join("; ", failed) : "");
            ScanForImport2();
        }
        finally
        {
            IsImportBusy = false;
        }
    }

    // Rescan without overwriting the result message.
    private void ScanForImport2()
    {
        var message = ImportStatusText;
        ScanForImport();
        ImportStatusText = message;
    }
}
