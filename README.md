# Copilot Chatbot (WPF)

Desktop chat client for GitHub Copilot built with WPF and .NET 9 on Windows.

It provides a tabbed chat UI, session restore, model selection, reasoning effort controls, permission prompts, local slash shortcuts, MCP/agent/skill visibility, encrypted local settings, and rich Markdown/HTML rendering through WebView2.

## Features

- Multi-tab chat sessions with per-tab system prompts
- Saved chat sessions restored on startup, with debounced background persistence
- Per-tab status indicators:
  - Busy spinner while Copilot is thinking or running tools
  - Typing indicator while a response is streaming
  - Unread marker and bold title for background responses
- Live model discovery from GitHub Copilot SDK
- Per-chat model and reasoning effort selection for models that support it
- Chat navigation controls:
  - Scroll to top and bottom
  - Jump to previous and next user question
- Turn-based response display, where all responses for a prompt are grouped under the user message
- Activity bar for transient Copilot status such as reasoning, tool execution, background agent progress, and shortcut activity
- Tool and permission workflow:
  - Folder access rules (read/read-write)
  - Allowed tools and hosts lists
  - Saved permission rules
  - Per-command shell approvals for Copilot SDK shell permission requests
  - Memory permission toggle through `/memory`
- GitHub token and user secrets with show/hide controls
- Optional settings password for AES-256 encryption of sensitive settings
- User secrets mapped to environment variables for Copilot CLI and MCP servers
- Configurable working directory for Copilot CLI execution
- MCP server, agent, and skill capability view
- Local slash shortcuts for inspecting or changing runtime state
- Extra agent and skill folder configuration
- Scheduled automation tasks with cron expressions, manual runs, run history, optional pre/post commands, Copilot steps, and file/HTTP/named-pipe handoff
- Light, dark, and follow-the-sun theme options
- Optional debug logging
- Markdown rendering, collapsible message cards, response pop-out windows, embedded HTML previews, and iframe preview pop-out windows
- Custom application icon and Windows `.ico` packaging

## Tech Stack

- .NET 9 (`net9.0-windows`)
- WPF
- GitHub.Copilot.SDK
- Microsoft.Web.WebView2
- Markdig
- WPF-UI

## Prerequisites

- Windows 10/11
- .NET 9 SDK
- GitHub account with Copilot access
- A GitHub token configured in-app (the app UI references a token with `copilot` scope)

## Getting Started

1. Clone the repository.
2. Restore and build:

```powershell
dotnet restore .\CopilotChatbot.sln
dotnet build .\CopilotChatbot.sln -c Debug
```

3. Run the app:

```powershell
dotnet run --project .\CopilotChatbot\CopilotChatbot.csproj
```

## First-Time Setup

Open **Settings** in the app and configure:

- GitHub token
- Optional settings password for encrypting saved GitHub credentials and user secrets
- Optional working directory (defaults to your user profile folder)
- Optional user secrets (`Name`, `Environment Variable`, `Value`)
- Optional permission defaults and allow-lists
- Optional default system prompt
- Optional extra agent and skill folders
- Preferred appearance/theme
- Optional debug logging

Then use **Refresh Models** to fetch available models from the Copilot runtime.

### Settings Password

If a settings password is configured, the app derives an AES-256 key from that password and a built-in salt. The key is kept only in memory for the current app session.

- The password itself is not written to `settings.json`.
- On startup, encrypted settings trigger a password prompt.
- If the password is wrong or cancelled, the app warns and starts with blank settings for that session.
- Clearing the settings password in Settings and saving writes the GitHub token and user secret values as plaintext.
- Existing legacy DPAPI-encrypted user secrets can still be read and are rewritten using the active save mode.

## Local Slash Shortcuts

Shortcuts are handled locally by the app and are not sent as chat prompts.

- `/mcp` - show registered MCP servers, their status, and reported tools.
- `/cwd` - show the effective Copilot CLI working directory.
- `/cwd <folder>` - set the Copilot CLI working directory. Quoted paths, relative paths, `.`, and `~` are supported.
- `/env` - show Copilot-relevant environment details. Token values are redacted.
- `/memory` - show the current long-term memory permission state.
- `/memory on` - approve memory permission requests automatically across sessions.
- `/memory off` - reject memory permission requests automatically across sessions.
- `/usage` - show the latest Copilot usage and quota snapshot (tokens, requests, remaining, reset date).

## Scheduled Tasks

Use **Scheduler** in the main window to create repeatable automation tasks.

Tasks can be manual-only or scheduled with a cron expression. Each task can run:

- An optional pre-command
- An optional Copilot prompt, either in a hidden one-shot session or bound to an existing chat tab by title
- An optional post-command
- An optional handoff to a file, HTTP endpoint, or named pipe

Task templates support values such as `{{pre_output}}`, `{{copilot_response}}`, `{{copilot_response_file}}`, `{{task_name}}`, `{{run_id}}`, and `{{timestamp}}`.

The scheduler keeps the latest run records per task and exposes them through the task history window.

## Configuration and Data Files

The app stores local configuration under:

- `%APPDATA%\CopilotChatbot\settings.json`
- `%APPDATA%\CopilotChatbot\chat-sessions.json.gz`
- `%APPDATA%\CopilotChatbot\scheduler-runs\...`

If debug logging is enabled, logs are written to:

- `%APPDATA%\CopilotChatbot\debug-YYYY-MM-DD.log`

Sensitive values in `settings.json` are encrypted only when a settings password is active. Otherwise, the GitHub token and user secret values are stored as plaintext.

## MCP Notes

- The app loads user MCP server config from:
  - `~/.copilot/mcp-config.json`
  - `%APPDATA%\GitHub Copilot\mcp-config.json`
  - `<working-dir>/.github/copilot/mcp.json`
  - `<working-dir>/.copilot/mcp-config.json`
- A default read-only GitHub MCP server is added when missing and is also available as a bundled fallback.
- Existing MCP server entries are not overwritten; server names are first-wins.
- The default GitHub MCP server uses `https://api.githubcopilot.com/mcp/readonly` and keeps `Authorization: Bearer $env:GITHUB_TOKEN` in config so the Copilot SDK/runtime can resolve the environment variable.
- The configured working directory is where Copilot CLI runs.
- Project-level Copilot/MCP config is typically resolved relative to the working directory.
- Agents and skills are loaded from the default Copilot locations plus any extra folders configured in Settings.

Default agent and skill locations shown by the app:

- `~/.copilot/agents`
- `<working-dir>/.github/agents`
- `~/.copilot/skills`
- `<working-dir>/.github/skills`

## Project Structure

- `CopilotChatbot/` - WPF application
- `CopilotChatbot/Services/` - runtime services (Copilot client, rendering, settings, logging)
- `CopilotChatbot/Services/LocalShortcutService.cs` - local slash shortcut registry and handlers
- `CopilotChatbot/Models/` - settings and chat data models
- `CopilotChatbot/Assets/` - application icon and bundled MCP server metadata
- `CopilotChatbot/MainWindow.*` - main chat UI and interaction logic
- `CopilotChatbot/ChatTabContent.*` - per-tab chat input, status, and navigation controls
- `CopilotChatbot/SettingsWindow.*` - settings UI and persistence wiring
- `CopilotChatbot/SettingsPasswordWindow.*` - startup unlock prompt for encrypted settings
- `CopilotChatbot/SchedulerWindow.*` - scheduled task editor and manual run controls
- `CopilotChatbot/SchedulerHistoryWindow.*` - scheduled task run history viewer
- `CopilotChatbot/ResponseWindow.*` - full response pop-out viewer
- `CopilotChatbot/IframePreviewWindow.*` - embedded HTML iframe pop-out viewer
- `.github/workflows/dotnet-desktop.yml` - GitHub Actions desktop publish and release workflow
- `.github/workflows/dotnet-build.yaml` - manual GitHub Actions solution build workflow

## Troubleshooting

- No models appear:
  - Verify token and Copilot entitlement.
  - Open Settings and confirm Working Directory is valid.
  - Click **Refresh Models** again.
- MCP tools do not appear:
  - Run `/mcp` to inspect the registered MCP servers and tools.
  - Run `/cwd` to confirm project-level MCP config is being resolved from the expected folder.
- Authentication or connection failures:
  - Re-check token value in Settings.
  - If settings are encrypted, restart and confirm the startup settings password unlocks successfully.
  - Confirm network/proxy restrictions do not block Copilot CLI.
- Memory requests are rejected:
  - Run `/memory` to check the current state.
  - Run `/memory on` if you want Copilot memory writes/votes to be approved automatically.
- Web content not rendering:
  - Ensure WebView2 runtime is available and updated on the machine.
- UI slows while responses stream:
  - Chat updates and session saves are throttled, but very large HTML previews can still be expensive to render in WebView2.
  - Use the iframe pop-out button for large embedded previews.
- Scheduled task does not run:
  - Confirm the task is enabled and the cron expression is valid.
  - Use **Run Now** to separate scheduler timing problems from command or Copilot failures.
  - Check the task history window for captured output and errors.
- Manual GitHub Actions run does not start:
  - Confirm Actions are enabled for the repository.
  - Confirm the workflow exists on the default branch at `.github/workflows/dotnet-build.yaml`.
  - Check GitHub Status if the UI reports that the workflow could not be queued.

## Build Configuration

Current project defaults (from `CopilotChatbot.csproj`):

- `TargetFramework`: `net9.0-windows`
- `RuntimeIdentifier`: `win-x64`
- `UseWPF`: `true`
- `SelfContained`: `false`
- `ApplicationIcon`: `Assets\AppIcon.ico`

## GitHub Actions

The repository includes two Windows GitHub Actions workflows.

### Desktop Publish Workflow

`.github/workflows/dotnet-desktop.yml`:

- Runs on pushes to `main`
- Runs on pull requests targeting `main`
- Restores and publishes `CopilotChatbot/CopilotChatbot.csproj`
- Uses .NET 9 on `windows-latest`
- Publishes a self-contained single-file `win-x64` build
- Uploads `CopilotChatbot-win-x64.zip` as a build artifact
- Creates a release named `Build <run_number>` with tag `build-<run_number>` on `main`

### Manual Build Workflow

`.github/workflows/dotnet-build.yaml`:

- Can be started manually from the GitHub Actions UI
- Restores and builds `CopilotChatbot.sln`
- Uses .NET 9 on `windows-latest`

Build versioning uses:

- `VERSION_PREFIX` repository variable, defaulting to `1.0`
- `github.run_number` as the final build number

For example, with `VERSION_PREFIX=1.0`, workflow runs produce versions like:

- `1.0.1`
- `1.0.2`
- `1.0.3`

Set the prefix in:

```text
Repository Settings > Secrets and variables > Actions > Variables
Name: VERSION_PREFIX
Value: 1.0
```

## License

This project uses an MIT-style non-commercial license. See `LICENSE`.

Note: this is not the standard OSI MIT License because commercial use is restricted.
