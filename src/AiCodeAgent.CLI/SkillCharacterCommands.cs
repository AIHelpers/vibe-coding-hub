using System.CommandLine;
using System.Diagnostics;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Characters;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Context;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiCodeAgent.CLI;

/// <summary>
/// <c>skills …</c> and <c>characters …</c> commands: manage the local skills
/// registry and the characters that use those skills, without starting a chat.
/// </summary>
public static class SkillCharacterCommands
{
    /// <summary>Registries for one working directory (global + that project's .aiagent folder).</summary>
    internal sealed record Registries(SkillRegistry Skills, CharacterRegistry Characters, RolePresetLoader Presets)
    {
        public SkillMaintenance Maintenance => new(Skills, Characters);
    }

    internal static async Task<Registries> OpenAsync(string? dir)
    {
        var workdir = Path.GetFullPath(dir ?? Directory.GetCurrentDirectory());
        var config = new ConfigurationService();
        try { await config.LoadAsync(); } catch { /* defaults are fine for listing */ }
        var presets = new RolePresetLoader(NullLogger<RolePresetLoader>.Instance);
        var skills = new SkillRegistry(
            NullLogger<SkillRegistry>.Instance,
            projectSkillsDir: SkillRegistry.GetDefaultProjectSkillsDir(workdir),
            overrides: config.Config.Agent.SkillOverrides);
        var characters = new CharacterRegistry(
            presets,
            NullLogger<CharacterRegistry>.Instance,
            projectCharactersDir: CharacterRegistry.GetDefaultProjectCharactersDir(workdir));
        return new Registries(skills, characters, presets);
    }

    public static void AddCommands(RootCommand root)
    {
        root.AddCommand(BuildSkillsCommand());
        root.AddCommand(BuildCharactersCommand());
    }

    // ============================== skills ==============================

    private static Command BuildSkillsCommand()
    {
        var dirOption = new Option<string?>("--dir", "Project directory (default: current directory)");
        var projectOption = new Option<bool>("--project", "Use the project scope (.aiagent/skills) instead of global (~/.aiagent/skills)");
        var cmd = new Command("skills", "Manage the local skills registry (Markdown SKILL.md files)");
        cmd.AddGlobalOption(dirOption);

        // skills list
        var list = new Command("list", "List skills");
        var allOption = new Option<bool>("--all", "Include manual-only, hidden and invalid skills");
        var tagOption = new Option<string?>("--tag", "Only skills with this tag");
        list.AddOption(allOption);
        list.AddOption(tagOption);
        list.SetHandler(async (dir, all, tag) =>
        {
            var r = await OpenAsync(dir);
            var skills = all ? await r.Skills.ListAllAsync() : await r.Skills.ListAsync();
            if (!string.IsNullOrEmpty(tag))
                skills = skills.Where(s => s.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)).ToList();
            if (skills.Count == 0)
            {
                Console.WriteLine("No skills. Create one with: skills new <name> --description \"...\"");
                return;
            }
            Console.WriteLine($"{"NAME",-28} {"SCOPE",-8} {"TOKENS",7}  DESCRIPTION");
            foreach (var s in skills)
            {
                var flags = (s.DisableModelInvocation ? " [manual]" : "") + (!s.IsValid ? " [invalid]" : "") + (s.ShadowsGlobal ? " [overrides global]" : "");
                Console.WriteLine($"{s.Name,-28} {s.Scope.ToString().ToLowerInvariant(),-8} {s.EstimatedTokens,7}  {Truncate(s.Description, 60)}{flags}");
            }
            Console.WriteLine($"\nGlobal:  {r.Skills.GlobalSkillsDirectory}\nProject: {r.Skills.ProjectSkillsDirectory}");
        }, dirOption, allOption, tagOption);

        // skills show <name>
        var show = new Command("show", "Show a skill's metadata and content");
        var showName = new Argument<string>("name");
        show.AddArgument(showName);
        show.SetHandler(async (dir, name) =>
        {
            var r = await OpenAsync(dir);
            var s = await r.Skills.GetAsync(name);
            if (s == null) { Fail($"Skill '{name}' not found."); return; }
            Console.WriteLine($"Name:        {s.Name}");
            Console.WriteLine($"Scope:       {s.Scope.ToString().ToLowerInvariant()}{(s.ShadowsGlobal ? " (overrides a global skill)" : "")}");
            Console.WriteLine($"File:        {s.FilePath}");
            Console.WriteLine($"Description: {s.Description}");
            if (s.Tags.Count > 0) Console.WriteLine($"Tags:        {string.Join(", ", s.Tags)}");
            if (s.AllowedTools.Count > 0) Console.WriteLine($"Tools:       {string.Join(", ", s.AllowedTools)}");
            Console.WriteLine($"Tokens:      ~{s.EstimatedTokens}");
            var refs = await r.Characters.FindSkillReferencesAsync(s.Name);
            Console.WriteLine($"Used by:     {(refs.Count == 0 ? "-" : string.Join(", ", refs.Select(x => x.CharacterId + (x.Pinned ? " (pinned)" : ""))))}");
            PrintProblems(s.ValidationErrors, s.Warnings);
            Console.WriteLine("\n" + await r.Skills.LoadAsync(s.Name));
        }, dirOption, showName);

        // skills new <name>
        var create = new Command("new", "Create a skill");
        var newName = new Argument<string>("name", "kebab-case name, e.g. release-notes");
        var descOption = new Option<string>("--description", "When the agent should use this skill") { IsRequired = true };
        var tagsOption = new Option<string[]>("--tag", "Tag (repeatable)") { AllowMultipleArgumentsPerToken = true };
        var manualOption = new Option<bool>("--manual", "Manual-only: hidden from the model, invoked with /skill");
        var editOption = new Option<bool>("--edit", "Open the new file in $EDITOR");
        create.AddArgument(newName);
        create.AddOption(descOption);
        create.AddOption(tagsOption);
        create.AddOption(manualOption);
        create.AddOption(projectOption);
        create.AddOption(editOption);
        create.SetHandler(async (string? dir, string name, string description, string[] tags, bool manual, bool project, bool edit) =>
        {
            var r = await OpenAsync(dir);
            await Run(async () =>
            {
                var s = await r.Skills.CreateAsync(new SkillDraft
                {
                    Name = name,
                    Description = description,
                    Tags = tags ?? Array.Empty<string>(),
                    DisableModelInvocation = manual
                }, project ? SkillScope.Project : SkillScope.Global);
                Console.WriteLine($"Created {s.Scope.ToString().ToLowerInvariant()} skill '{s.Name}': {s.FilePath}");
                if (edit) OpenEditor(s.FilePath!);
            });
        }, dirOption, newName, descOption, tagsOption, manualOption, projectOption, editOption);

        // skills edit <name>
        var editCmd = new Command("edit", "Open a skill in $EDITOR and validate it afterwards");
        var editName = new Argument<string>("name");
        editCmd.AddArgument(editName);
        editCmd.SetHandler(async (dir, name) =>
        {
            var r = await OpenAsync(dir);
            var path = r.Skills.GetSkillPath(name);
            if (path == null) { Fail($"Skill '{name}' not found."); return; }
            if (!OpenEditor(path)) return;
            r.Skills.Refresh();
            var s = await r.Skills.GetAsync(name);
            if (s != null) PrintProblems(s.ValidationErrors, s.Warnings);
        }, dirOption, editName);

        // skills rename <old> <new>
        var rename = new Command("rename", "Rename a skill and update every character that uses it");
        var oldArg = new Argument<string>("old");
        var newArg = new Argument<string>("new");
        rename.AddArgument(oldArg);
        rename.AddArgument(newArg);
        rename.SetHandler(async (dir, oldName, newName2) =>
        {
            var r = await OpenAsync(dir);
            await Run(async () =>
            {
                var result = await r.Maintenance.RenameAsync(oldName, newName2);
                Console.WriteLine($"Renamed '{oldName}' -> '{result.Skill.Name}'. Updated {result.CharactersUpdated} character file(s).");
            });
        }, dirOption, oldArg, newArg);

        // skills rm <name>
        var rm = new Command("rm", "Delete a skill");
        var rmName = new Argument<string>("name");
        var yesOption = new Option<bool>("--yes", "Do not ask for confirmation");
        var keepRefsOption = new Option<bool>("--keep-references", "Leave the skill listed in characters (shown as a dangling reference)");
        var globalOption = new Option<bool>("--global", "Delete the global copy even if a project copy exists");
        rm.AddArgument(rmName);
        rm.AddOption(yesOption);
        rm.AddOption(keepRefsOption);
        rm.AddOption(projectOption);
        rm.AddOption(globalOption);
        rm.SetHandler(async (string? dir, string name, bool yes, bool keepRefs, bool project, bool global) =>
        {
            var r = await OpenAsync(dir);
            SkillScope? scope = project ? SkillScope.Project : global ? SkillScope.Global : null;
            var s = await r.Skills.GetAsync(name);
            if (s == null) { Fail($"Skill '{name}' not found."); return; }
            var refs = await r.Characters.FindSkillReferencesAsync(name);
            if (!yes)
            {
                var usedBy = refs.Count == 0 ? "" : $" It is used by {refs.Count} character reference(s): {string.Join(", ", refs.Select(x => x.CharacterId).Distinct())}.";
                if (!Confirm($"Delete skill '{name}' ({(scope ?? s.Scope).ToString().ToLowerInvariant()})?{usedBy}")) return;
            }
            var result = await r.Maintenance.DeleteAsync(name, scope, removeReferences: !keepRefs);
            Console.WriteLine(result.Deleted
                ? $"Deleted '{name}'. Removed it from {result.CharactersUpdated} character file(s)."
                : $"Nothing deleted for '{name}'.");
        }, dirOption, rmName, yesOption, keepRefsOption, projectOption, globalOption);

        // skills doctor
        var doctor = new Command("doctor", "Check skills and characters for problems");
        doctor.SetHandler(async dir =>
        {
            var r = await OpenAsync(dir);
            var checks = await SkillDoctor.DiagnoseAsync(r.Skills, r.Characters);
            foreach (var c in checks)
            {
                var tag = c.Status switch { DoctorStatus.Ok => "OK  ", DoctorStatus.Warning => "WARN", _ => "FAIL" };
                Console.WriteLine($"  {tag}  {c.Name,-28} {c.Detail}");
                if (!string.IsNullOrEmpty(c.FixHint)) Console.WriteLine($"        Fix: {c.FixHint}");
            }
            if (checks.Any(c => c.Status == DoctorStatus.Error)) Environment.ExitCode = 1;
        }, dirOption);

        cmd.AddCommand(list);
        cmd.AddCommand(show);
        cmd.AddCommand(create);
        cmd.AddCommand(editCmd);
        cmd.AddCommand(doctor);
        cmd.AddCommand(rename);
        cmd.AddCommand(rm);
        return cmd;
    }

    // ============================== characters ==============================

    private static Command BuildCharactersCommand()
    {
        var dirOption = new Option<string?>("--dir", "Project directory (default: current directory)");
        var projectOption = new Option<bool>("--project", "Use the project scope (.aiagent/characters) instead of global (~/.aiagent/characters)");
        var cmd = new Command("characters", "Manage characters: agent personas with their own skills that run workflows");
        cmd.AddGlobalOption(dirOption);

        var list = new Command("list", "List characters (built-in and user-defined)");
        list.SetHandler(async dir =>
        {
            var r = await OpenAsync(dir);
            Console.WriteLine($"{"ID",-22} {"SCOPE",-8} {"BASE ROLE",-12} {"SKILLS",-30} DESCRIPTION");
            foreach (var c in await r.Characters.ListAsync())
            {
                var skills = c.Skills == null ? "(all)" : c.Skills.Count == 0 && c.PinnedSkills.Count == 0 ? "-" :
                    string.Join(", ", c.Skills.Concat(c.PinnedSkills.Select(p => p + "*")));
                var flags = (c.IsTemplate ? " [template]" : "") + (c.Extends != null ? $" [extends {c.Extends}]" : "") +
                            (!c.IsValid ? " [invalid]" : c.Overrides ? " [override]" : "");
                Console.WriteLine($"{c.Id,-22} {c.Scope.ToString().ToLowerInvariant(),-8} {c.BaseRole ?? "-",-12} {Truncate(skills, 30),-30} {Truncate(c.Description, 50)}{flags}");
            }
            Console.WriteLine("\n* = pinned (always in context)");
            Console.WriteLine($"Global:  {r.Characters.GlobalCharactersDirectory}\nProject: {r.Characters.ProjectCharactersDirectory}");
        }, dirOption);

        var show = new Command("show", "Show a character");
        var showId = new Argument<string>("id");
        show.AddArgument(showId);
        show.SetHandler(async (dir, id) =>
        {
            var r = await OpenAsync(dir);
            var c = await r.Characters.GetAsync(id);
            if (c == null) { Fail($"Character '{id}' not found."); return; }
            var preset = c.BaseRole == null ? null : r.Presets.GetPreset(c.BaseRole);
            var resolved = CharacterResolver.Apply(new AgentOptions(), c, preset);
            Console.WriteLine($"Character:   {c.Label} ({c.Id})");
            Console.WriteLine($"Scope:       {c.Scope.ToString().ToLowerInvariant()}{(c.Overrides ? " (overrides another definition)" : "")}");
            if (c.FilePath != null) Console.WriteLine($"File:        {c.FilePath}");
            Console.WriteLine($"Description: {c.Description}");
            Console.WriteLine($"Base role:   {c.BaseRole ?? "-"}");
            Console.WriteLine($"Model:       {c.Model ?? "(session default)"}");
            Console.WriteLine($"Permission:  {CharacterRegistry.FormatPermissionMode(resolved.PermissionMode)}");
            Console.WriteLine($"Tools:       {(resolved.EnabledTools.Count == 0 ? "(all)" : string.Join(", ", resolved.EnabledTools))}" +
                              (resolved.DisabledTools.Count > 0 ? $"  minus {string.Join(", ", resolved.DisabledTools)}" : ""));
            if (c.IsTemplate) Console.WriteLine("Template:    yes (a base for other characters)");
            if (c.Extends != null) Console.WriteLine($"Extends:     {string.Join(" → ", c.InheritanceChain.DefaultIfEmpty(c.Extends))}");
            PrintSkillSources(c);
            if (c.RemoveSkills.Count > 0) Console.WriteLine($"Removed:     {string.Join(", ", c.RemoveSkills)}");
            var children = (await r.Characters.ListAsync()).Where(x => string.Equals(x.Extends, c.Id, StringComparison.OrdinalIgnoreCase)).Select(x => x.Id).ToList();
            if (children.Count > 0) Console.WriteLine($"Used by:     {string.Join(", ", children)} (extend it)");
            var dangling = (await r.Maintenance.FindDanglingReferencesAsync()).Where(d => d.CharacterId == c.Id).ToList();
            PrintProblems(c.ValidationErrors, c.Warnings.Concat(dangling.Select(d => $"Skill '{d.SkillName}' does not exist.")).ToList());
            if (!string.IsNullOrWhiteSpace(c.Persona)) Console.WriteLine("\n" + c.Persona);
        }, dirOption, showId);

        var create = new Command("new", "Create a character");
        var idArg = new Argument<string>("id", "kebab-case id, e.g. alex-architect");
        var nameOption = new Option<string?>("--name", "Display name, e.g. \"Alex — Architect\"");
        var avatarOption = new Option<string?>("--avatar", "Emoji or initials");
        var descOption = new Option<string>("--description", () => "", "What this character is for");
        var roleOption = new Option<string?>("--base-role", "Role preset to build on (planner, implementer, reviewer, tester, deployer, or a custom preset)");
        var modelOption = new Option<string?>("--model", "Model override");
        var modeOption = new Option<string?>("--permission-mode", "ask | auto-edit | full-auto | plan");
        var skillOption = new Option<string[]>("--skill", "Skill to assign (repeatable)") { AllowMultipleArgumentsPerToken = true };
        var pinOption = new Option<string[]>("--pin", "Skill to pin (repeatable)") { AllowMultipleArgumentsPerToken = true };
        var editOption = new Option<bool>("--edit", "Open the new file in $EDITOR");
        var extendsOption = new Option<string?>("--extends", "Template (or other character) to inherit base role, persona and skills from");
        var templateOption = new Option<bool>("--template", "Create it as a template that other characters extend");
        foreach (var o in new Option[] { nameOption, avatarOption, descOption, roleOption, modelOption, modeOption, skillOption, pinOption, projectOption, editOption, extendsOption, templateOption })
            create.AddOption(o);
        create.AddArgument(idArg);
        create.SetHandler(async context =>
        {
            var p = context.ParseResult;
            var r = await OpenAsync(p.GetValueForOption(dirOption));
            await Run(async () =>
            {
                var modeText = p.GetValueForOption(modeOption);
                var mode = CharacterRegistry.ParsePermissionMode(modeText);
                if (!string.IsNullOrEmpty(modeText) && mode == null)
                    throw new SkillValidationException($"Unknown permission mode '{modeText}'.");
                var c = await r.Characters.CreateAsync(new CharacterDraft
                {
                    Id = p.GetValueForArgument(idArg),
                    DisplayName = p.GetValueForOption(nameOption),
                    Avatar = p.GetValueForOption(avatarOption),
                    Description = p.GetValueForOption(descOption) ?? "",
                    BaseRole = p.GetValueForOption(roleOption),
                    Model = p.GetValueForOption(modelOption),
                    PermissionMode = mode,
                    Skills = p.GetValueForOption(skillOption) ?? Array.Empty<string>(),
                    PinnedSkills = p.GetValueForOption(pinOption) ?? Array.Empty<string>(),
                    Extends = p.GetValueForOption(extendsOption),
                    IsTemplate = p.GetValueForOption(templateOption)
                }, p.GetValueForOption(projectOption) ? SkillScope.Project : SkillScope.Global);
                Console.WriteLine($"Created {c.Scope.ToString().ToLowerInvariant()} {(c.IsTemplate ? "template" : "character")} '{c.Id}': {c.FilePath}");
                PrintSkillSources(c);
                await WarnMissingSkillsAsync(r, c);
                if (p.GetValueForOption(editOption)) OpenEditor(c.FilePath!);
            });
        });

        var edit = new Command("edit", "Open a character in $EDITOR and validate it afterwards");
        var editId = new Argument<string>("id");
        edit.AddArgument(editId);
        edit.SetHandler(async (dir, id) =>
        {
            var r = await OpenAsync(dir);
            var c = await r.Characters.GetAsync(id);
            if (c == null) { Fail($"Character '{id}' not found."); return; }
            if (c.FilePath == null) { Fail($"'{id}' is built-in. Run 'characters assign {id} <skill>' or create '{id}.md' to customize it."); return; }
            if (!OpenEditor(c.FilePath)) return;
            r.Characters.Refresh();
            var after = await r.Characters.GetAsync(id);
            if (after != null) PrintProblems(after.ValidationErrors, after.Warnings);
        }, dirOption, editId);

        var rm = new Command("rm", "Delete a character file");
        var rmId = new Argument<string>("id");
        var yesOption = new Option<bool>("--yes", "Do not ask for confirmation");
        rm.AddArgument(rmId);
        rm.AddOption(yesOption);
        rm.AddOption(projectOption);
        rm.SetHandler(async (dir, id, yes, project) =>
        {
            var r = await OpenAsync(dir);
            var c = await r.Characters.GetAsync(id);
            if (c == null) { Fail($"Character '{id}' not found."); return; }
            if (c.IsBuiltIn) { Fail($"'{id}' is built-in and cannot be deleted."); return; }
            if (!yes && !Confirm($"Delete character '{id}' ({c.FilePath})?")) return;
            var deleted = await r.Characters.DeleteAsync(id, project ? SkillScope.Project : null);
            Console.WriteLine(deleted ? $"Deleted '{id}'." : $"Nothing deleted for '{id}'.");
        }, dirOption, rmId, yesOption, projectOption);

        var assign = new Command("assign", "Give skills to a character");
        var assignId = new Argument<string>("id");
        var assignSkills = new Argument<string[]>("skills") { Arity = ArgumentArity.OneOrMore };
        var pinnedOption = new Option<bool>("--pinned", "Pin: inject the full instructions on every turn instead of loading on demand");
        assign.AddArgument(assignId);
        assign.AddArgument(assignSkills);
        assign.AddOption(pinnedOption);
        assign.SetHandler(async (dir, id, skills, pinned) =>
        {
            var r = await OpenAsync(dir);
            await Run(async () =>
            {
                var c = await r.Characters.AssignSkillsAsync(id, skills, pinned);
                Console.WriteLine($"'{c.Id}' skills: {string.Join(", ", c.Skills ?? Array.Empty<string>())}" +
                                  (c.PinnedSkills.Count > 0 ? $"; pinned: {string.Join(", ", c.PinnedSkills)}" : ""));
                await WarnMissingSkillsAsync(r, c);
            });
        }, dirOption, assignId, assignSkills, pinnedOption);

        var unassign = new Command("unassign", "Remove skills from a character");
        var unassignId = new Argument<string>("id");
        var unassignSkills = new Argument<string[]>("skills") { Arity = ArgumentArity.OneOrMore };
        unassign.AddArgument(unassignId);
        unassign.AddArgument(unassignSkills);
        unassign.SetHandler(async (dir, id, skills) =>
        {
            var r = await OpenAsync(dir);
            await Run(async () =>
            {
                var c = await r.Characters.UnassignSkillsAsync(id, skills);
                Console.WriteLine($"'{c.Id}' skills: {string.Join(", ", c.Skills ?? Array.Empty<string>())}");
            });
        }, dirOption, unassignId, unassignSkills);

        var extend = new Command("extend", "Make a character extend a template (or 'none' to stop inheriting)");
        var extendId = new Argument<string>("id");
        var extendParent = new Argument<string>("template", "Template/character id, or none");
        extend.AddArgument(extendId);
        extend.AddArgument(extendParent);
        extend.SetHandler(async (dir, id, parent) =>
        {
            var r = await OpenAsync(dir);
            await Run(async () =>
            {
                var c = await r.Characters.SetExtendsAsync(id, parent.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : parent);
                Console.WriteLine(c.Extends == null ? $"'{c.Id}' no longer extends a template." : $"'{c.Id}' now extends '{c.Extends}'.");
                PrintSkillSources(c);
            });
        }, dirOption, extendId, extendParent);

        var template = new Command("template", "Mark a character as a template (on) or a regular character (off)");
        var templateId = new Argument<string>("id");
        var templateState = new Argument<string>("state", "on | off");
        template.AddArgument(templateId);
        template.AddArgument(templateState);
        template.SetHandler(async (dir, id, state) =>
        {
            var r = await OpenAsync(dir);
            await Run(async () =>
            {
                var on = state.Trim().ToLowerInvariant() is "on" or "true" or "yes";
                var c = await r.Characters.SetTemplateAsync(id, on);
                Console.WriteLine(c.IsTemplate ? $"'{c.Id}' is now a template." : $"'{c.Id}' is a regular character.");
            });
        }, dirOption, templateId, templateState);

        var addSkill = new Command("add-skill",
            "Add a skill to a character: pick it from the library, or create it on the spot when it does not exist yet");
        var addSkillId = new Argument<string>("id", "Character id");
        var addSkillName = new Argument<string>("skill", "Library skill name (kebab-case); created when missing and --description is given");
        var addDescOption = new Option<string?>("--description", "Create the skill with this description when it does not exist yet");
        var addInstrOption = new Option<string?>("--instructions", "Instructions (Markdown) for a newly created skill");
        var addPinnedOption = new Option<bool>("--pinned", "Pin: inject the full instructions on every turn instead of loading on demand");
        var addProjectOption = new Option<bool>("--project", "Create a new skill in the project library (.aiagent/skills) instead of global");
        addSkill.AddArgument(addSkillId);
        addSkill.AddArgument(addSkillName);
        foreach (var o in new Option[] { addDescOption, addInstrOption, addPinnedOption, addProjectOption })
            addSkill.AddOption(o);
        addSkill.SetHandler(async context =>
        {
            var p = context.ParseResult;
            var r = await OpenAsync(p.GetValueForOption(dirOption));
            await Run(async () =>
            {
                var description = p.GetValueForOption(addDescOption);
                var instructions = p.GetValueForOption(addInstrOption);
                var draft = string.IsNullOrWhiteSpace(description) && string.IsNullOrWhiteSpace(instructions)
                    ? null
                    : new SkillDraft { Description = description ?? string.Empty, Body = string.IsNullOrWhiteSpace(instructions) ? null : instructions };
                var pinned = p.GetValueForOption(addPinnedOption);
                var result = await r.Maintenance.AddSkillToCharacterAsync(
                    p.GetValueForArgument(addSkillId),
                    p.GetValueForArgument(addSkillName),
                    pinned,
                    draft,
                    p.GetValueForOption(addProjectOption) ? SkillScope.Project : SkillScope.Global);
                if (result.CreatedSkill)
                    Console.WriteLine($"Created {result.Skill.Scope.ToString().ToLowerInvariant()} skill '{result.Skill.Name}': {result.Skill.FilePath}");
                Console.WriteLine($"Added '{result.Skill.Name}' to '{result.Character.Id}'{(pinned ? " (pinned)" : "")}.");
                if (result.WasAllSkills)
                    Console.WriteLine($"  note: '{result.Character.Id}' could use every skill before; now it uses only the skills added to it.");
                PrintSkillSources(result.Character);
            });
        });

        cmd.AddCommand(list);
        cmd.AddCommand(show);
        cmd.AddCommand(create);
        cmd.AddCommand(edit);
        cmd.AddCommand(rm);
        cmd.AddCommand(assign);
        cmd.AddCommand(unassign);
        cmd.AddCommand(extend);
        cmd.AddCommand(template);
        cmd.AddCommand(addSkill);
        return cmd;
    }

    // ============================== helpers ==============================

    /// <summary>Print a character's effective skills grouped by where they come from.</summary>
    internal static void PrintSkillSources(CharacterInfo c)
    {
        if (c.Skills == null)
        {
            Console.WriteLine("Skills:      (all skills)");
        }
        else
        {
            Console.WriteLine($"Skills:      {(c.Skills.Count == 0 ? "-" : string.Join(", ", c.Skills))}");
            foreach (var group in c.Skills.GroupBy(x => c.SkillSources.TryGetValue(x, out var src) ? src : "own"))
                Console.WriteLine($"  {DescribeSource(group.Key) + ":",-36} {string.Join(", ", group)}");
        }
        if (c.PinnedSkills.Count > 0)
            Console.WriteLine($"Pinned:      {string.Join(", ", c.PinnedSkills.Select(x => Annotate(x, c.SkillSources)))}");
    }

    /// <summary>"own" or "template software-developer".</summary>
    internal static string DescribeSource(string source) =>
        source.StartsWith("template:", StringComparison.Ordinal) ? "template " + source["template:".Length..] : source;

    private static string Annotate(string skill, IReadOnlyDictionary<string, string> sources) =>
        sources.TryGetValue(skill, out var src) && src != "own" ? $"{skill} ({DescribeSource(src)})" : skill;

    private static async Task WarnMissingSkillsAsync(Registries r, CharacterInfo c)
    {
        foreach (var d in (await r.Maintenance.FindDanglingReferencesAsync()).Where(d => d.CharacterId == c.Id))
            Console.WriteLine($"  warning: skill '{d.SkillName}' does not exist yet (create it with: skills new {d.SkillName} --description \"...\")");
    }

    private static async Task Run(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (SkillValidationException ex)
        {
            foreach (var e in ex.Errors) Fail(e);
        }
    }

    private static void PrintProblems(IReadOnlyCollection<string> errors, IReadOnlyCollection<string> warnings)
    {
        foreach (var e in errors) Console.WriteLine($"  error: {e}");
        foreach (var w in warnings) Console.WriteLine($"  warning: {w}");
    }

    private static void Fail(string message)
    {
        Console.Error.WriteLine(message);
        Environment.ExitCode = 1;
    }

    private static bool Confirm(string question)
    {
        if (Console.IsInputRedirected) { Fail(question + " Re-run with --yes to confirm."); return false; }
        Console.Write(question + " [y/N] ");
        var answer = Console.ReadLine();
        return answer != null && answer.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Open <paramref name="path"/> in $VISUAL/$EDITOR (notepad on Windows) and wait. Returns false when no editor could be started.</summary>
    internal static bool OpenEditor(string path)
    {
        var editor = Environment.GetEnvironmentVariable("VISUAL") ?? Environment.GetEnvironmentVariable("EDITOR")
                     ?? (OperatingSystem.IsWindows() ? "notepad" : null);
        if (string.IsNullOrWhiteSpace(editor))
        {
            Console.WriteLine($"Set $EDITOR to edit from here, or open: {path}");
            return false;
        }
        try
        {
            var parts = editor.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var psi = new ProcessStartInfo(parts[0]) { UseShellExecute = false };
            if (parts.Length > 1) psi.Arguments = parts[1] + " ";
            psi.Arguments += $"\"{path}\"";
            using var process = Process.Start(psi);
            process?.WaitForExit();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not start editor '{editor}': {ex.Message}. Open: {path}");
            return false;
        }
    }

    private static string Truncate(string text, int max) =>
        string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..(max - 1)] + "…";
}
