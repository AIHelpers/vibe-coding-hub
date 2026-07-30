.PHONY: build run publish test

build:
	dotnet build AiCodeAgent.slnx

run:
	dotnet run --project src/AiCodeAgent.CLI -- chat

run-ollama:
	dotnet run --project src/AiCodeAgent.CLI -- chat --provider ollama

run-prompt:
	dotnet run --project src/AiCodeAgent.CLI -- run "Explain the codebase structure"

publish-all:
	dotnet publish src/AiCodeAgent.CLI -c Release -r win-x64 -o ./publish/win
	dotnet publish src/AiCodeAgent.CLI -c Release -r linux-x64 -o ./publish/linux
	dotnet publish src/AiCodeAgent.CLI -c Release -r osx-arm64 -o ./publish/macos

test:
	dotnet test

config-openai:
	dotnet run --project src/AiCodeAgent.CLI -- config set-key openai $(KEY)

config-anthropic:
	dotnet run --project src/AiCodeAgent.CLI -- config set-key anthropic $(KEY)