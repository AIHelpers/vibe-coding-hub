using AiCodeAgent.App.ViewModels;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Providers;

namespace AiCodeAgent.App.Tests.Setup;

/// <summary>E3-S2: the wizard's state machine, validation and what it writes to config.json.</summary>
public sealed class SetupWizardViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wizard-" + Guid.NewGuid().ToString("N"));
    private readonly string _project;

    public SetupWizardViewModelTests()
    {
        Directory.CreateDirectory(_dir);
        _project = Path.Combine(_dir, "my-project");
        Directory.CreateDirectory(_project);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private sealed class FakeProbe : ILocalProviderProbe
    {
        public List<DetectedProvider> Found { get; } = new();
        public Task<IReadOnlyList<DetectedProvider>> ProbeAsync(IEnumerable<ProbeTarget>? targets = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DetectedProvider>>(Found.ToList());
    }

    private sealed class FakeTester : IConnectionTester
    {
        public Func<string, ProviderConfiguration, ConnectionTestResult> Respond { get; set; } =
            (_, _) => Ok("m1", "m1", "m2");
        public List<(string Key, ProviderConfiguration Config)> Calls { get; } = new();

        public Task<ConnectionTestResult> TestAsync(string providerKey, ProviderConfiguration config, CancellationToken cancellationToken = default)
        {
            Calls.Add((providerKey, config));
            return Task.FromResult(Respond(providerKey, config));
        }
    }

    private static ConnectionTestResult Ok(string defaultModel, params string[] ids) =>
        new(true, ids.Select(i => new ModelOption(i, null, false)).ToList(), defaultModel, $"Connected. {ids.Length} models available.");

    private static ConnectionTestResult Fail(string message) => new(false, Array.Empty<ModelOption>(), null, message);

    private static DetectedProvider Ollama(params string[] models) =>
        new("ollama", "Ollama", "http://localhost:11434", models);

    private async Task<(SetupWizardViewModel Vm, FakeProbe Probe, FakeTester Tester, ConfigurationService Config)> StartAsync(
        Func<Task<string?>>? pick = null, Func<Task<string?>>? sample = null, params DetectedProvider[] detected)
    {
        var config = new ConfigurationService(_dir);
        await config.LoadAsync();
        var probe = new FakeProbe();
        probe.Found.AddRange(detected);
        var tester = new FakeTester();
        var vm = new SetupWizardViewModel(config, probe, tester, pick, sample);
        await vm.InitializeAsync();
        return (vm, probe, tester, config);
    }

    // ---- step 1 ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DetectedOllama_IsListedFirst_Marked_AndPreselected()
    {
        var (vm, _, _, _) = await StartAsync(detected: Ollama("llama3.1:latest", "qwen2.5-coder:7b"));

        Assert.Equal("ollama", vm.Sources[0].Key);
        Assert.True(vm.Sources[0].IsDetected);
        Assert.Equal("Detected ✓ (free, private)", vm.Sources[0].Badge);
        Assert.Same(vm.Sources[0], vm.SelectedSource);
        Assert.Contains("2 models", vm.Sources[0].Description);
        Assert.Equal(WizardStep.Source, vm.Step);
        Assert.False(vm.IsDetecting);
    }

    [Fact]
    public async Task NothingDetected_OffersCloudAndCustom_ThenTheLocalServersThatAreNotRunning()
    {
        var (vm, _, _, _) = await StartAsync();

        Assert.Equal(new[] { "openai", "anthropic", "custom", "ollama", "lmstudio" }, vm.Sources.Select(s => s.Key));
        Assert.All(vm.Sources.Where(s => s.IsLocal), s => Assert.Equal("Not running", s.Badge));
        Assert.Equal("openai", vm.SelectedSource!.Key);
    }

    [Fact]
    public async Task BothLocalServersDetected_OllamaComesFirst_AndNeitherIsListedTwice()
    {
        var lm = new DetectedProvider("lmstudio", "LM Studio", "http://localhost:1234", new[] { "qwen" });
        var (vm, _, _, _) = await StartAsync(detected: new[] { Ollama("llama3.1"), lm });

        Assert.Equal(new[] { "ollama", "lmstudio", "openai", "anthropic", "custom" }, vm.Sources.Select(s => s.Key));
    }

    // ---- step 2 ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Next_FromADetectedLocalServer_TestsItAtOnce_AndPreselectsItsFirstModel()
    {
        var (vm, _, tester, _) = await StartAsync(detected: Ollama("llama3.1:latest", "qwen2.5-coder:7b"));
        tester.Respond = (_, _) => Ok("llama3.1:latest", "llama3.1:latest", "qwen2.5-coder:7b");

        await vm.NextCommand.ExecuteAsync(null);

        Assert.Equal(WizardStep.Connect, vm.Step);
        var call = Assert.Single(tester.Calls);
        Assert.Equal("ollama", call.Key);
        Assert.Equal("http://localhost:11434", call.Config.BaseUrl);
        Assert.True(vm.TestSucceeded);
        Assert.Equal("llama3.1:latest", vm.SelectedModel!.Id);
        Assert.True(vm.NextCommand.CanExecute(null));
        Assert.False(vm.NeedsApiKey);
    }

    [Fact]
    public async Task CloudSource_NeedsAKey_BeforeAnythingIsSentToTheProvider()
    {
        var (vm, _, tester, _) = await StartAsync();
        await vm.NextCommand.ExecuteAsync(null);

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.Empty(tester.Calls);
        Assert.Equal("Paste your OpenAI API key first.", vm.Message);
        Assert.True(vm.NeedsApiKey);
        Assert.False(vm.NextCommand.CanExecute(null));
    }

    [Fact]
    public async Task WrongKey_ShowsTheProvidersErrorInPlainWords_AndBlocksNext()
    {
        var (vm, _, tester, _) = await StartAsync();
        tester.Respond = (_, _) => Fail("401 Unauthorized: check the key.");
        await vm.NextCommand.ExecuteAsync(null);
        vm.ApiKey = "sk-wrong";

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.False(vm.TestSucceeded);
        Assert.Equal("401 Unauthorized: check the key.", vm.Message);
        Assert.Empty(vm.Models);
        Assert.False(vm.NextCommand.CanExecute(null));
        Assert.Equal("sk-wrong", tester.Calls.Single().Config.ApiKey);
    }

    [Fact]
    public async Task GoodKey_ListsModels_PreselectsTheDefault_AndAllowsNext()
    {
        var (vm, _, tester, _) = await StartAsync();
        tester.Respond = (_, _) => Ok("gpt-5.6-terra", "gpt-5.6-sol", "gpt-5.6-terra");
        await vm.NextCommand.ExecuteAsync(null);
        vm.ApiKey = "  sk-good  ";

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.True(vm.TestSucceeded);
        Assert.True(vm.HasModels);
        Assert.Equal("gpt-5.6-terra", vm.SelectedModel!.Id);
        Assert.Equal("sk-good", tester.Calls.Single().Config.ApiKey); // trimmed
        Assert.True(vm.NextCommand.CanExecute(null));
    }

    [Fact]
    public async Task ChangingTheKey_AfterASuccessfulTest_RequiresTestingAgain()
    {
        var (vm, _, _, _) = await StartAsync();
        await vm.NextCommand.ExecuteAsync(null);
        vm.ApiKey = "sk-good";
        await vm.TestConnectionCommand.ExecuteAsync(null);
        Assert.True(vm.NextCommand.CanExecute(null));

        vm.ApiKey = "sk-other";

        Assert.False(vm.TestSucceeded);
        Assert.Empty(vm.Models);
        Assert.False(vm.NextCommand.CanExecute(null));
    }

    [Fact]
    public async Task ARetiredModel_CannotBeTheChoice()
    {
        var (vm, _, tester, _) = await StartAsync();
        tester.Respond = (_, _) => new ConnectionTestResult(true, new[] { new ModelOption("gpt-3.5-turbo", null, true) }, "gpt-3.5-turbo", "Connected.");
        await vm.NextCommand.ExecuteAsync(null);
        vm.ApiKey = "sk-good";

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.True(vm.TestSucceeded);
        Assert.False(vm.NextCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("http://localhost:8000", "http://localhost:8000")]
    [InlineData("  http://localhost:8000/ ", "http://localhost:8000")]
    [InlineData("https://api.example.com/v1/", "https://api.example.com")]
    [InlineData("HTTP://Host:1234/V1", "HTTP://Host:1234")]
    public void CustomUrl_IsNormalized(string typed, string expected)
    {
        Assert.True(SetupWizardViewModel.TryNormalizeUrl(typed, out var url));
        Assert.Equal(expected, url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("localhost:1234")]
    [InlineData("ftp://host")]
    [InlineData("not a url")]
    public void CustomUrl_RejectsWhatIsNotAnHttpAddress(string typed) =>
        Assert.False(SetupWizardViewModel.TryNormalizeUrl(typed, out _));

    [Fact]
    public async Task CustomSource_NeedsAnAddress_ButNoKey()
    {
        var (vm, _, tester, _) = await StartAsync();
        vm.SelectedSource = vm.Sources.Single(s => s.IsCustom);
        await vm.NextCommand.ExecuteAsync(null);

        await vm.TestConnectionCommand.ExecuteAsync(null);
        Assert.Empty(tester.Calls);
        Assert.Contains("server address", vm.Message);

        vm.CustomUrl = "http://localhost:8000/v1/";
        await vm.TestConnectionCommand.ExecuteAsync(null);

        var call = Assert.Single(tester.Calls);
        Assert.Equal("custom", call.Key);
        Assert.Equal("http://localhost:8000", call.Config.BaseUrl);
        Assert.True(vm.NeedsUrl);
        Assert.True(vm.TestSucceeded);
    }

    // ---- navigation ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Back_GoesOneStepBack_AndIsUnavailableOnTheFirstStep()
    {
        var (vm, _, _, _) = await StartAsync(detected: Ollama("llama3.1"));
        Assert.False(vm.CanGoBack);

        await vm.NextCommand.ExecuteAsync(null);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardStep.Project, vm.Step);
        Assert.True(vm.IsLastStep);

        vm.BackCommand.Execute(null);
        Assert.Equal(WizardStep.Connect, vm.Step);
        vm.BackCommand.Execute(null);
        Assert.Equal(WizardStep.Source, vm.Step);
        Assert.False(vm.CanGoBack);
    }

    [Fact]
    public async Task StepTitles_CountToThree()
    {
        var (vm, _, _, _) = await StartAsync(detected: Ollama("llama3.1"));

        Assert.StartsWith("Step 1 of 3", vm.StepTitle);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.StartsWith("Step 2 of 3", vm.StepTitle);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.StartsWith("Step 3 of 3", vm.StepTitle);
    }

    // ---- step 3 ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PermissionModes_DefaultToAsk_AndEachHasAOneLineExplanation()
    {
        var (vm, _, _, _) = await StartAsync();

        Assert.Equal("Ask", vm.SelectedPermission.Mode);
        Assert.Equal(new[] { "Ask", "AutoEdit", "Plan", "FullAuto" }, vm.PermissionChoices.Select(p => p.Mode));
        Assert.All(vm.PermissionChoices, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Description));
            Assert.DoesNotContain("\n", p.Description);
        });
    }

    [Fact]
    public async Task RecentFolders_ListOnlyFoldersThatStillExist()
    {
        var config = new ConfigurationService(_dir);
        await config.LoadAsync();
        config.Config.Ui.RecentFolders = new List<string> { Path.Combine(_dir, "deleted"), _project };
        var vm = new SetupWizardViewModel(config, new FakeProbe(), new FakeTester());

        Assert.Equal(new[] { _project }, vm.RecentFolders);
        Assert.True(vm.HasRecentFolders);
    }

    [Fact]
    public async Task Browse_UsesThePicker_AndSampleIsOfferedOnlyWhenThereIsOne()
    {
        var (vm, _, _, _) = await StartAsync(pick: () => Task.FromResult<string?>(_project));
        Assert.False(vm.CanTrySample);

        await vm.BrowseCommand.ExecuteAsync(null);
        Assert.Equal(_project, vm.ProjectPath);

        var (withSample, _, _, _) = await StartAsync(sample: () => Task.FromResult<string?>(_project));
        Assert.True(withSample.CanTrySample);
        await withSample.TrySampleCommand.ExecuteAsync(null);
        Assert.Equal(_project, withSample.ProjectPath);
    }

    [Fact]
    public async Task AFolderThatDoesNotExist_BlocksFinish()
    {
        var (vm, _, _, _) = await StartAsync(detected: Ollama("llama3.1"));
        await vm.NextCommand.ExecuteAsync(null);
        await vm.NextCommand.ExecuteAsync(null);

        vm.ProjectPath = Path.Combine(_dir, "no-such-folder");

        Assert.False(vm.NextCommand.CanExecute(null));
        await vm.FinishCommand.ExecuteAsync(null);
        Assert.Equal("That folder doesn't exist.", vm.Message);
    }

    // ---- finishing -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Finish_WithOllama_WritesAWorkingConfig()
    {
        var (vm, _, tester, _) = await StartAsync(detected: Ollama("llama3.1:latest"));
        tester.Respond = (_, _) => Ok("llama3.1:latest", "llama3.1:latest");
        SetupWizardResult? result = null;
        vm.Completed += (_, r) => result = r;
        await vm.NextCommand.ExecuteAsync(null);
        await vm.NextCommand.ExecuteAsync(null);
        vm.ProjectPath = _project;

        await vm.NextCommand.ExecuteAsync(null); // Finish

        Assert.NotNull(result);
        Assert.Equal(("ollama", "llama3.1:latest", _project, "Ask"), (result!.ProviderKey, result.Model, result.ProjectPath, result.PermissionMode));

        var reloaded = new ConfigurationService(_dir);
        await reloaded.LoadAsync();
        Assert.Equal("ollama", reloaded.Config.DefaultProvider);
        Assert.Equal("llama3.1:latest", reloaded.Config.Providers["ollama"].DefaultModel);
        Assert.Equal("http://localhost:11434", reloaded.Config.Providers["ollama"].BaseUrl);
        Assert.True(reloaded.Config.Ui.SetupWizardDone);
        Assert.Equal(_project, reloaded.Config.Agent.WorkingDirectory);
        Assert.Equal(_project, reloaded.Config.Ui.RecentFolders[0]);
        Assert.Equal("Ask", reloaded.Config.Agent.DefaultPermissionMode);
    }

    [Fact]
    public async Task Finish_WithACloudKey_SavesTheKey_AndTheChosenModelAndMode()
    {
        var (vm, _, tester, config) = await StartAsync();
        vm.SelectedSource = vm.Sources.Single(s => s.Key == "anthropic");
        tester.Respond = (_, _) => Ok("claude-sonnet-5-5", "claude-sonnet-5-5", "claude-haiku-5-5");
        await vm.NextCommand.ExecuteAsync(null);
        vm.ApiKey = "sk-ant-secret";
        await vm.TestConnectionCommand.ExecuteAsync(null);
        vm.SelectedModel = vm.Models.Single(m => m.Id == "claude-haiku-5-5");
        await vm.NextCommand.ExecuteAsync(null);
        vm.ProjectPath = _project;
        vm.SelectedPermission = vm.PermissionChoices.Single(p => p.Mode == "AutoEdit");

        await vm.NextCommand.ExecuteAsync(null);

        Assert.Equal("sk-ant-secret", config.Config.Providers["anthropic"].ApiKey); // plain in memory: the app uses it right away
        var onDisk = File.ReadAllText(Path.Combine(_dir, "config.json"));
        Assert.DoesNotContain("sk-ant-secret", onDisk);

        var reloaded = new ConfigurationService(_dir);
        await reloaded.LoadAsync();
        Assert.Equal("anthropic", reloaded.Config.DefaultProvider);
        Assert.Equal("claude-haiku-5-5", reloaded.Config.Providers["anthropic"].DefaultModel);
        Assert.Equal("sk-ant-secret", reloaded.Config.Providers["anthropic"].ApiKey);
        Assert.Equal("AutoEdit", reloaded.Config.Agent.DefaultPermissionMode);
    }

    [Fact]
    public async Task Finish_WithoutAProject_KeepsTheCurrentWorkingFolder()
    {
        var (vm, _, _, config) = await StartAsync(detected: Ollama("llama3.1"));
        config.Config.Agent.WorkingDirectory = _project;
        vm.ProjectPath = string.Empty;
        await vm.NextCommand.ExecuteAsync(null);
        await vm.NextCommand.ExecuteAsync(null);
        SetupWizardResult? result = null;
        vm.Completed += (_, r) => result = r;

        await vm.NextCommand.ExecuteAsync(null);

        Assert.Null(result!.ProjectPath);
        Assert.Equal(_project, config.Config.Agent.WorkingDirectory);
    }

    [Fact]
    public async Task Skip_MarksTheWizardDone_WithoutChangingTheProvider()
    {
        var (vm, _, _, config) = await StartAsync(detected: Ollama("llama3.1"));
        var providerBefore = config.Config.DefaultProvider;
        var skipped = false;
        vm.Skipped += (_, _) => skipped = true;

        await vm.SkipCommand.ExecuteAsync(null);

        Assert.True(skipped);
        var reloaded = new ConfigurationService(_dir);
        await reloaded.LoadAsync();
        Assert.True(reloaded.Config.Ui.SetupWizardDone);
        Assert.Equal(providerBefore, reloaded.Config.DefaultProvider);
    }

    [Fact]
    public async Task TheWizardNeverComesBackOnItsOwn_OnceCompleted()
    {
        var (vm, _, _, config) = await StartAsync(detected: Ollama("llama3.1"));
        await vm.NextCommand.ExecuteAsync(null);
        await vm.NextCommand.ExecuteAsync(null);
        await vm.NextCommand.ExecuteAsync(null);

        var reloaded = new ConfigurationService(_dir);
        await reloaded.LoadAsync();

        var show = await AiCodeAgent.App.Services.FirstRunDetector.ShouldShowAsync(reloaded.Config, reloaded.ConfigFileExisted, new FakeProbe());
        Assert.False(show);
    }
}
