using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Tools.Base;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.Code;

public class DiagnosticsTool : BaseTool
{
    public DiagnosticsTool(ILogger<DiagnosticsTool> logger) : base(logger) { }

    public override string Name => "run_diagnostics";
    public override string Description =>
        "Run build and diagnostic checks on the project. " +
        "Detects compilation errors, warnings, test failures.";

    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new JsonSchema
        {
            Properties = new()
            {
                ["type"] = new()
                {
                    Type = "string",
                    Description = "Type of diagnostic",
                    Enum = ["build", "test", "lint", "format_check"]
                },
                ["path"] = new() { Type = "string", Description = "Project/solution path" },
                ["args"] = new() { Type = "string", Description = "Additional arguments" }
            },
            Required = ["type"]
        }
    };

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, AgentExecutionContext context)
    {
        var type = GetArg<string>(call, "type");
        var path = GetArg<string>(call, "path", ".");
        var args = GetArg<string>(call, "args", "");

        var resolvedPath = ResolvePath(path, context);
        var (command, detector) = DetectProjectType(resolvedPath, type, args);

        if (command == null)
            return Error($"Could not detect project type in: {path}");

        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "/bin/bash",
                    Arguments = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                        ? $"/c {command}"
                        : $"-c \"{command}\"",
                    WorkingDirectory = resolvedPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };

            process.Start();

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var stdout = await process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = await process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);

            var output = string.Join("\n", new[] { stdout, stderr }.Where(s => !string.IsNullOrEmpty(s)));
            var summary = ParseDiagnosticOutput(output, detector, process.ExitCode);

            return new ToolResult
            {
                ToolName = Name,
                Content = $"[{detector} {type}]\nExit code: {process.ExitCode}\n\n{summary}",
                IsError = process.ExitCode != 0
            };
        }
        catch (Exception ex)
        {
            return Error($"Diagnostic failed: {ex.Message}");
        }
    }

    private static (string? command, string detector) DetectProjectType(string path, string type, string extraArgs)
    {
        if (Directory.GetFiles(path, "*.csproj", SearchOption.AllDirectories).Length > 0 ||
            Directory.GetFiles(path, "*.sln").Length > 0)
        {
            return type switch
            {
                "build" => ($"dotnet build {extraArgs} 2>&1", "dotnet"),
                "test" => ($"dotnet test {extraArgs} 2>&1", "dotnet"),
                "format_check" => ("dotnet format --verify-no-changes 2>&1", "dotnet"),
                _ => ($"dotnet build 2>&1", "dotnet")
            };
        }

        if (File.Exists(Path.Combine(path, "package.json")))
        {
            return type switch
            {
                "build" => ($"npm run build {extraArgs} 2>&1", "npm"),
                "test" => ($"npm test {extraArgs} 2>&1", "npm"),
                "lint" => ("npm run lint 2>&1", "npm"),
                _ => ("npm run build 2>&1", "npm")
            };
        }

        if (Directory.GetFiles(path, "*.py").Length > 0)
        {
            return type switch
            {
                "test" => ($"python -m pytest {extraArgs} 2>&1", "python"),
                "lint" => ("python -m flake8 . 2>&1", "python"),
                _ => ("python -m py_compile *.py 2>&1", "python")
            };
        }

        return (null, "unknown");
    }

    private static string ParseDiagnosticOutput(string output, string detector, int exitCode)
    {
        if (exitCode == 0) return $"Passed\n{output}";

        var sb = new StringBuilder();
        sb.AppendLine("Failed");

        // Extract error summaries
        var errorLines = output.Split('\n')
            .Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                        l.Contains("Error", StringComparison.Ordinal) ||
                        l.Contains("FAILED", StringComparison.Ordinal))
            .Take(20);

        foreach (var line in errorLines)
            sb.AppendLine(line);

        sb.AppendLine("\nFull output:");
        sb.AppendLine(output.Length > 5000 ? output[^5000..] : output);

        return sb.ToString();
    }
}