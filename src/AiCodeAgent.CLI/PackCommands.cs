using System.CommandLine;
using System.CommandLine.Invocation;
using System.Reflection;
using AiCodeAgent.Core.Packs;
using AiCodeAgent.Core.Updates;

namespace AiCodeAgent.CLI;

/// <summary>
/// <c>pack add | list | update | remove | sync</c> (E6-S1): install skills, characters and flows shared as a git repository.
/// Exit codes: 0 done, 1 failed, 3 bad arguments or nothing approved.
/// </summary>
public static class PackCommands
{
    public static void AddCommands(RootCommand root) => root.AddCommand(Build());

    internal static SemanticVersion? AppVersion()
    {
        var asm = typeof(PackCommands).Assembly;
        var text = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? asm.GetName().Version?.ToString();
        return SemanticVersion.TryParse(text, out var v) ? v : null;
    }

    private static Command Build()
    {
        var dirOption = new Option<string?>("--dir", "Project directory (default: current directory)");
        var yesOption = new Option<bool>(new[] { "--yes", "-y" }, "Install without asking (the preview is still printed)");
        var cmd = new Command("pack", "Install skill, character and flow packs from a git repository");
        cmd.AddGlobalOption(dirOption);

        // pack add <url>
        var add = new Command("add", "Install a pack: pack add https://github.com/org/dotnet-team-pack --ref v1.2.0");
        var urlArg = new Argument<string>("url", "Git URL of the pack repository (https, ssh or a local path)");
        var refOption = new Option<string?>("--ref", "Tag or commit to install (default: the newest commit of the default branch). Pin a tag for a reproducible install");
        var saveOption = new Option<bool>("--save", "Also record the pack in .aiagent/packs.json so teammates get it with 'pack sync'");
        var forceOption = new Option<bool>("--force", "Install even when the pack needs a newer app version");
        var quietOption = new Option<bool>("--no-files", "Do not list every file in the preview");
        add.AddArgument(urlArg);
        add.AddOption(refOption);
        add.AddOption(yesOption);
        add.AddOption(saveOption);
        add.AddOption(forceOption);
        add.AddOption(quietOption);
        add.SetHandler(async (InvocationContext ctx) =>
        {
            var p = ctx.ParseResult;
            var url = p.GetValueForArgument(urlArg);
            var gitRef = p.GetValueForOption(refOption);
            var dir = p.GetValueForOption(dirOption) ?? Directory.GetCurrentDirectory();
            var installer = new PackInstaller(new PackStore(), null, AppVersion());
            PackPreview? preview = null;
            try
            {
                Console.Error.WriteLine($"Fetching {url}{(gitRef != null ? " @ " + gitRef : "")} …");
                preview = await installer.PrepareAsync(url, gitRef, ctx.GetCancellationToken());
                Console.WriteLine(preview.Render(listFiles: !p.GetValueForOption(quietOption)));
                if (!preview.CanInstall)
                {
                    installer.Discard(preview);
                    ctx.ExitCode = 3;
                    return;
                }
                if (preview.Incompatible != null && !p.GetValueForOption(forceOption))
                {
                    installer.Discard(preview);
                    Console.Error.WriteLine("Not installed. Use --force to install it anyway.");
                    ctx.ExitCode = 3;
                    return;
                }
                if (!p.GetValueForOption(yesOption) && !Confirm("\nInstall this pack?"))
                {
                    installer.Discard(preview);
                    Console.Error.WriteLine("Nothing installed.");
                    ctx.ExitCode = 3;
                    return;
                }
                var installed = installer.Commit(preview, makeDefault: true, allowIncompatible: p.GetValueForOption(forceOption));
                Console.WriteLine($"Installed {installed.Name} {installed.RefLabel} → {installer.Store.DirectoryOf(installed)}");
                if (!installed.Pinned)
                    Console.WriteLine($"Note: it follows {(installed.Ref ?? "the default branch")}; 'pack update' moves it. Use --ref <tag> to pin a version.");
                if (p.GetValueForOption(saveOption))
                {
                    PackStore.SaveToProjectFile(dir, new PackReference { Name = installed.Name, Url = installed.Url, Ref = installed.Pinned ? installed.Ref : installed.Commit });
                    Console.WriteLine($"Recorded in {PackStore.ProjectFile(dir)}");
                }
            }
            catch (PackException ex)
            {
                if (preview != null) installer.Discard(preview);
                Console.Error.WriteLine(ex.Message);
                ctx.ExitCode = 1;
            }
        });

        // pack list
        var list = new Command("list", "List installed packs");
        list.SetHandler((string? dirValue) =>
        {
            var store = new PackStore();
            var dir = dirValue ?? Directory.GetCurrentDirectory();
            var installed = store.List();
            if (installed.Count == 0)
            {
                Console.WriteLine("No packs installed. Try: pack add <git-url> --ref <tag>");
                return;
            }
            var active = store.Sources(dir).Select(s => s.Root).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var declared = PackStore.ReadProjectFile(dir);
            Console.WriteLine($"{"NAME",-28} {"VERSION",-9} {"REF",-14} {"IN USE",-7} SOURCE");
            foreach (var pack in installed)
            {
                var inUse = active.Contains(store.DirectoryOf(pack)) ? "yes" : "";
                var note = pack.Pinned ? "" : " (follows " + (pack.Ref ?? "default branch") + ")";
                Console.WriteLine($"{pack.Name,-28} {pack.Version ?? "-",-9} {pack.RefLabel,-14} {inUse,-7} {pack.Url}{note}");
            }
            var missing = declared.Where(d => !installed.Any(i => string.Equals(i.Name, d.Name, StringComparison.OrdinalIgnoreCase))).ToList();
            if (missing.Count > 0)
                Console.WriteLine($"\nThis project declares {string.Join(", ", missing.Select(m => m.Name))} in .aiagent/packs.json: run 'pack sync'.");
        }, dirOption);

        // pack update [name]
        var update = new Command("update", "Move packs that follow a branch to its newest commit (packs pinned to a tag or commit stay)");
        var updateName = new Argument<string?>("name", () => null, "Pack to update (default: all)");
        update.AddArgument(updateName);
        update.SetHandler(async (InvocationContext ctx) =>
        {
            var installer = new PackInstaller(new PackStore(), null, AppVersion());
            var results = await installer.UpdateAsync(ctx.ParseResult.GetValueForArgument(updateName), ctx.GetCancellationToken());
            foreach (var r in results) Console.WriteLine((r.State == PackUpdateState.Failed ? "✕ " : r.State == PackUpdateState.Updated ? "✓ " : "· ") + r.Message);
            if (results.Any(r => r.State == PackUpdateState.Failed)) ctx.ExitCode = 1;
        });

        // pack remove <name>
        var remove = new Command("remove", "Remove a pack; its skills, characters and flows disappear");
        var removeName = new Argument<string>("name", "Pack name");
        var removeRef = new Option<string?>("--ref", "Remove only this installed ref (default: every ref)");
        remove.AddArgument(removeName);
        remove.AddOption(removeRef);
        remove.SetHandler((string name, string? gitRef) =>
        {
            var removed = new PackInstaller(new PackStore()).Remove(name, gitRef);
            if (removed.Count == 0)
            {
                Console.Error.WriteLine($"Pack '{name}' is not installed.");
                Environment.ExitCode = 1;
                return;
            }
            foreach (var p in removed) Console.WriteLine($"Removed {p.Name} {p.RefLabel}");
        }, removeName, removeRef);

        // pack sync
        var sync = new Command("sync", "Install the packs this project declares in .aiagent/packs.json, at exactly those refs");
        sync.AddOption(yesOption);
        sync.SetHandler(async (InvocationContext ctx) =>
        {
            var p = ctx.ParseResult;
            var dir = p.GetValueForOption(dirOption) ?? Directory.GetCurrentDirectory();
            if (PackStore.ReadProjectFile(dir).Count == 0)
            {
                Console.Error.WriteLine($"{PackStore.ProjectFile(dir)} declares no packs.");
                ctx.ExitCode = 3;
                return;
            }
            var yes = p.GetValueForOption(yesOption);
            var installer = new PackInstaller(new PackStore(), null, AppVersion());
            var results = await installer.SyncAsync(dir, preview =>
            {
                Console.WriteLine(preview.Render(listFiles: false));
                if (!preview.CanInstall) return false;
                // Hooks, MCP entries and scripts need a person's OK; a plain pack of skills and flows does not.
                return preview.Attention.Count == 0 || yes || Confirm("\nInstall this pack?");
            }, allowIncompatible: false, ctx.GetCancellationToken());
            foreach (var r in results) Console.WriteLine((r.Installed ? "✓ " : "· ") + r.Message);
            if (results.Any(r => !r.Installed && !r.Message.Contains("already installed"))) ctx.ExitCode = 1;
        });

        cmd.AddCommand(add);
        cmd.AddCommand(list);
        cmd.AddCommand(update);
        cmd.AddCommand(remove);
        cmd.AddCommand(sync);
        return cmd;
    }

    private static bool Confirm(string question)
    {
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("No terminal to ask on; run again with --yes after reading the preview.");
            return false;
        }
        Console.Write(question + " [y/N] ");
        var answer = Console.ReadLine();
        return answer != null && answer.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
    }
}
