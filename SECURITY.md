# Security Policy

## Reporting a Vulnerability

If you discover a security vulnerability in AI Code Agent, please report it responsibly.

**DO NOT open a public GitHub issue for security vulnerabilities.**

Instead, please email security concerns to the maintainers privately. Include:
- Description of the vulnerability
- Steps to reproduce
- Potential impact
- Suggested fix (if any)

## API Key Safety

This project requires API keys for AI providers (OpenAI, Anthropic, etc.). 

**Never commit API keys to the repository.**

The `.gitignore` file excludes:
- `config.json` (contains encrypted API keys)
- `.env` files
- `appsettings.Development.json`
- `appsettings.Local.json`

### Best Practices for Users

1. **Use environment variables** instead of config files when possible:
   ```bash
   export AIAGENT_OPENAI_API_KEY=sk-...
   ```

2. **Never share your config.json** file - it contains your API keys

3. **Rotate compromised keys immediately** if you suspect they've been exposed

4. **Use read-only mode** (`--readonly`) when you want to prevent the agent from making changes

## Tool Safety

The agent has several built-in safety mechanisms:

- **Read-only mode**: Prevents write operations and mutating commands
- **Allowed paths**: Restricts file access to specific directories
- **Dangerous command detection**: Blocks commands like `rm -rf`, `format`, `dd`, etc.
- **Git operation restrictions**: Only read operations allowed in read-only mode

### Command Execution

The `execute_command` tool blocks these dangerous commands by default:
- `rm`, `rmdir`, `del` (file deletion)
- `format`, `dd`, `mkfs`, `fdisk` (disk operations)
- `shutdown`, `reboot`, `halt` (system power)
- `sudo`, `su` (privilege escalation)
- `chmod`, `chown` (permission changes)

## Supported Versions

| Version | Supported          |
|---------|--------------------|
| 1.0.x   | :white_check_mark: |
| < 1.0   | :x:                |