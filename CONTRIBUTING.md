# Contributing to AI Code Agent

Thank you for your interest in contributing! This document outlines the process for contributing to the project.

## Getting Started

1. **Fork** the repository on GitHub
2. **Clone** your fork locally:
   ```bash
   git clone https://github.com/your-username/AiCodeAgent.git
   cd AiCodeAgent
   ```
3. **Build** the project to verify everything works:
   ```bash
   dotnet build AiCodeAgent.slnx
   ```

## Development Workflow

1. **Create a branch** for your feature or bugfix:
   ```bash
   git checkout -b feature/my-feature
   # or
   git checkout -b fix/issue-123
   ```

2. **Make your changes** following the coding standards below

3. **Test your changes**:
   ```bash
   dotnet build AiCodeAgent.slnx
   dotnet test
   ```

4. **Commit with clear messages**:
   ```bash
   git commit -m "Add support for Azure OpenAI provider"
   git commit -m "Fix: context trimming losing system prompt"
   ```

5. **Push and create a Pull Request**:
   ```bash
   git push origin feature/my-feature
   ```
   Then open a PR on GitHub with a clear description of your changes.

## Coding Standards

### C# Style
- Use **file-scoped namespaces** (`namespace Foo;`)
- Use **nullable reference types** (enabled in all projects)
- Use **implicit usings** (enabled in all projects)
- Use **records** for immutable data models
- Use **async/await** for all I/O operations
- Use **`IAsyncEnumerable<T>`** for streaming operations

### Project Structure
- **AiCodeAgent.Core** - Interfaces, models, and shared logic (no dependencies)
- **AiCodeAgent.Providers** - AI provider implementations
- **AiCodeAgent.Tools** - Tool implementations (inherit `BaseTool`)
- **AiCodeAgent.Context** - Context management implementations
- **AiCodeAgent.CLI** - Entry point and UI only

### Adding a New Tool

1. Create a class inheriting from `BaseTool`:
   ```csharp
   public class MyTool : BaseTool
   {
       public MyTool(ILogger<MyTool> logger) : base(logger) { }
       
       public override string Name => "my_tool";
       public override string Description => "What this tool does";
       public override ToolDefinition Definition => new() { ... };
       
       public override async Task<ToolResult> ExecuteAsync(
           ToolCall call, AgentExecutionContext context)
       {
           // Implementation
       }
   }
   ```

2. Register it in `Program.cs`:
   ```csharp
   builder.Services.AddSingleton<ITool, MyTool>();
   ```

### Adding a New Provider

1. Inherit from `BaseHttpProvider` (for HTTP-based APIs) or implement `IAiProvider` directly
2. Implement `CompleteAsync` and `StreamAsync` methods
3. Register in `Program.cs` `RegisterProvider` method

## Security Considerations

- **Never commit API keys** or secrets
- **Never log sensitive data** (API keys, user content)
- **Validate all file paths** against `AllowedPaths`
- **Check `IsReadOnly`** before any write operation
- **Test dangerous command detection** when modifying shell tools

## Reporting Issues

When reporting bugs, please include:
- OS and .NET version
- Steps to reproduce
- Expected vs actual behavior
- Relevant logs (with API keys redacted)

## License

By contributing, you agree that your contributions will be licensed under the MIT License.