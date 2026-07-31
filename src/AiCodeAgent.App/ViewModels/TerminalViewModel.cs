using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;

namespace AiCodeAgent.App.ViewModels;

public partial class TerminalEntry
{
    public string Text { get; set; } = string.Empty;
    public bool IsError { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Type { get; set; } = "info"; // info, command, output, error, system
}

public partial class TerminalViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private double _terminalHeight = 200;

    public ObservableCollection<TerminalEntry> Entries { get; } = new();

    private Process? _currentProcess;

    public TerminalViewModel()
    {
        // Add welcome message
        Entries.Add(new TerminalEntry
        {
            Text = "Terminal ready. Tool output will appear here.",
            Type = "system",
            Timestamp = DateTime.Now
        });
    }

    [RelayCommand]
    private void ToggleVisibility()
    {
        IsVisible = !IsVisible;
    }

    [RelayCommand]
    private void Clear()
    {
        Entries.Clear();
        Entries.Add(new TerminalEntry
        {
            Text = "Terminal cleared.",
            Type = "system",
            Timestamp = DateTime.Now
        });
    }

    public void AppendOutput(string text, string type = "output", bool isError = false)
    {
        Entries.Add(new TerminalEntry
        {
            Text = text,
            Type = type,
            IsError = isError,
            Timestamp = DateTime.Now
        });
    }

    public void AppendCommand(string command)
    {
        Entries.Add(new TerminalEntry
        {
            Text = $"> {command}",
            Type = "command",
            Timestamp = DateTime.Now
        });
    }

    public async Task RunCommandAsync(string command, string workingDirectory)
    {
        if (IsRunning) return;

        IsRunning = true;
        AppendCommand(command);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{command}\"",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };

            using var process = new Process { StartInfo = psi };
            _currentProcess = process;

            process.OutputDataReceived += (sender, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data))
                {
                    AppendOutput(args.Data);
                }
            };

            process.ErrorDataReceived += (sender, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data))
                {
                    AppendOutput(args.Data, "error", true);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();

            AppendOutput($"Process exited with code {process.ExitCode}", "system");
        }
        catch (Exception ex)
        {
            AppendOutput($"Error: {ex.Message}", "error", true);
        }
        finally
        {
            IsRunning = false;
            _currentProcess = null;
        }
    }

    public void Cancel()
    {
        try
        {
            _currentProcess?.Kill(entireProcessTree: true);
            AppendOutput("Command cancelled.", "system");
        }
        catch { }
    }
}