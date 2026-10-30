using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Flows;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Core.Remote;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.App.ViewModels;

/// <summary>E7-S3 prototype: attach the canvas to a flow that <c>aiagent serve</c> is running in another process or on another machine.</summary>
public partial class FlowEditorViewModel
{
    /// <summary>The canvas shows a run that lives elsewhere. Editing and local runs are off until Detach.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocked))]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    private bool _isRemote;

    /// <summary>"Remote · full-sdlc · ws://127.0.0.1:5001/events" while attached.</summary>
    [ObservableProperty] private string _remoteTitle = string.Empty;

    /// <summary>Address typed in the Attach flyout.</summary>
    [ObservableProperty] private string _remoteUrl = "ws://127.0.0.1:";

    /// <summary>Token typed in the Attach flyout (never saved).</summary>
    [ObservableProperty] private string _remoteToken = string.Empty;

    private Func<Task>? _remoteCancel;
    private string? _beforeRemote;
    private bool _dirtyBeforeRemote;
    private RemoteRunClient? _remoteClient;

    partial void OnIsRemoteChanged(bool value) => RerunFromHereCommand.NotifyCanExecuteChanged();

    /// <summary>Normalises what the user typed: ws:// or wss:// (http/https are mapped), and the /events path when missing.</summary>
    public static bool TryParseRemoteUrl(string? text, out Uri uri, out string error)
    {
        uri = null!;
        error = string.Empty;
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0) { error = "Enter the address that 'aiagent serve' printed."; return false; }
        if (!value.Contains("://", StringComparison.Ordinal)) value = "ws://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed)) { error = "That is not a valid address."; return false; }
        var scheme = parsed.Scheme.ToLowerInvariant() switch
        {
            "http" => "ws",
            "https" => "wss",
            var s => s
        };
        if (scheme is not ("ws" or "wss")) { error = "Use a ws:// or wss:// address."; return false; }
        var builder = new UriBuilder(parsed) { Scheme = scheme };
        if (builder.Port == (scheme == "ws" ? 80 : 443) && !parsed.IsDefaultPort) builder.Port = parsed.Port;
        if (string.IsNullOrEmpty(builder.Path) || builder.Path == "/") builder.Path = RemoteProtocol.EventsPath;
        uri = builder.Uri;
        return true;
    }

    /// <summary>Connects to a served run and mirrors its events on the canvas until it finishes or the user detaches.</summary>
    [RelayCommand]
    public async Task AttachRemoteAsync()
    {
        if (IsRunning || IsReplay || IsRemote) return;
        if (!TryParseRemoteUrl(RemoteUrl, out var uri, out var urlError)) { StatusText = urlError; return; }

        StatusText = "Connecting…";
        RemoteRunClient client;
        using (var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            try { client = await RemoteRunClient.ConnectAsync(uri, RemoteToken.Trim(), connectTimeout.Token); }
            catch (OperationCanceledException) { StatusText = "Could not connect: timed out."; return; }
            catch (RemoteAgentException ex) { StatusText = ex.Message; return; }
            catch (Exception ex) { StatusText = "Could not connect: " + ex.Message; return; }
        }

        _beforeRemote = JsonSerializer.Serialize(ToDefinition(), SdlcPipelineLoader.FileJsonOptions);
        _dirtyBeforeRemote = IsDirty;
        _remoteClient = client;
        _remoteCancel = () => client.CancelRunAsync();
        LoadDefinition(client.Hello.Definition);
        ResetRunState();
        RunTask = client.Hello.Task;
        RemoteTitle = $"Remote · {client.Hello.Flow} · {uri.Authority}";
        IsRemote = true;
        IsRunning = true;
        _runCts = new CancellationTokenSource();
        StatusText = $"Attached to '{client.Hello.Flow}' at {uri.Authority}.";
        try
        {
            await foreach (var evt in client.EventsAsync(_runCts.Token))
                ApplyRunEvent(evt);
            if (IsRunning && !StatusText.StartsWith("Flow ", StringComparison.Ordinal))
                StatusText = "The remote run ended or the connection was lost.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Detached.";
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Remote run connection failed");
            StatusText = $"Remote connection failed: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            _remoteCancel = null;
            ClearPendingQuestions();
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    /// <summary>Leaves the remote run and brings back the flow that was on the canvas before.</summary>
    [RelayCommand]
    public async Task ExitRemoteAsync()
    {
        if (!IsRemote) return;
        _runCts?.Cancel(); // ends the event loop; the loop's finally clears pending questions
        _remoteCancel = null;
        var client = _remoteClient;
        _remoteClient = null;
        if (client != null) await client.DisposeAsync();

        IsRemote = false;
        RemoteTitle = string.Empty;
        ResetRunState();
        if (_beforeRemote != null)
        {
            var before = JsonSerializer.Deserialize<SdlcPipelineDefinition>(_beforeRemote, SdlcPipelineLoader.FileJsonOptions);
            if (before != null) LoadDefinition(before);
            IsDirty = _dirtyBeforeRemote;
        }
        _beforeRemote = null;
        StatusText = "Detached from the remote run.";
    }

    private void ClearPendingQuestions()
    {
        foreach (var tcs in _approvals.Values) tcs.TrySetResult(false);
        _approvals.Clear();
        _gateFeedback.Clear();
        foreach (var choice in _humanChoices.Values) choice.TrySetResult(new FlowHumanChoice(null, null, Stop: true));
        _humanChoices.Clear();
        foreach (var n in Nodes)
        {
            n.IsAwaitingApproval = false;
            n.IsAwaitingGate = false;
            n.IsAwaitingChoice = false;
        }
        IsEnteringGateNote = false;
        GateNote = string.Empty;
    }

    /// <summary>Somebody else (another client, the server's timeout) answered a question: stop showing it as open.</summary>
    private void ApplyRemoteResolved(RemoteRequestResolvedEvent resolved)
    {
        if (string.IsNullOrEmpty(resolved.NodeId) || FindNode(resolved.NodeId) is not { } node) return;
        _approvals.Remove(node.Id);
        _gateFeedback.Remove(node.Id);
        _humanChoices.Remove(node.Id);
        node.IsAwaitingApproval = false;
        node.IsAwaitingGate = false;
        node.IsAwaitingChoice = false;
        if (ReferenceEquals(node, SelectedNode)) OnPropertyChanged(nameof(SelectedOutput));
    }
}
