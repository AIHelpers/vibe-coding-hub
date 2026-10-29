using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using AiCodeAgent.Core.Flows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiCodeAgent.App.ViewModels;

/// <summary>A character a template needs, with whether the project has it.</summary>
public sealed record TemplateCharacterItem(string Id, bool Exists)
{
    public string Mark => Exists ? "✓" : "✗";
    public bool Missing => !Exists;
}

/// <summary>One card in the Flows template gallery: a mini diagram, the characters the flow needs and what using it adds.</summary>
public sealed partial class FlowTemplateCardViewModel : ObservableObject
{
    public FlowTemplateCardViewModel(FlowTemplate template, TemplateInstallPlan plan, ISet<string> existingCharacters)
    {
        Template = template;
        Plan = plan;
        Diagram = FlowDiagram.Build(template.Flow);
        Steps = FlowGraph.Build(template.Flow).DescribeWaves();
        foreach (var id in template.Characters)
            Characters.Add(new TemplateCharacterItem(id, existingCharacters.Contains(id)));
    }

    public FlowTemplate Template { get; }

    /// <summary>What using the template adds to the project (characters and skills).</summary>
    public TemplateInstallPlan Plan { get; }

    public FlowDiagram Diagram { get; }
    public ObservableCollection<TemplateCharacterItem> Characters { get; } = new();

    public string Title => $"{Template.Icon} {Template.Title}";
    public string Description => Template.Description;
    public string SampleTask => Template.SampleTask;

    /// <summary>"1: reproduce · 2: fix · …" - the execution order, parallel steps joined by ∥.</summary>
    public string Steps { get; }

    public string NodesPath => Diagram.Nodes;
    public string EdgesPath => Diagram.Edges.Length > 0 ? Diagram.Edges : "M0,0";
    public string LoopsPath => Diagram.Loops.Length > 0 ? Diagram.Loops : "M0,0";
    public double DiagramWidth => Diagram.Width;
    public double DiagramHeight => Diagram.Height;

    /// <summary>The project lacks characters the template needs (they can be created from the examples).</summary>
    public bool HasMissing => Plan.CharactersToCreate.Count > 0;

    /// <summary>Characters that are neither in the project nor among the examples.</summary>
    public bool HasUnavailable => Plan.Unavailable.Count > 0;
    public string UnavailableText => "Not available: " + string.Join(", ", Plan.Unavailable);

    /// <summary>"Use template" was pressed while characters are missing: waiting for Create / Use anyway / Cancel.</summary>
    [ObservableProperty] private bool _isConfirming;

    [ObservableProperty] private string _confirmText = string.Empty;
}
