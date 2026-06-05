# Copilot Chatbot (WPF)

Desktop chat client for GitHub Copilot built with WPF and .NET 9 on Windows.

It provides a tabbed chat UI, session restore, model selection, reasoning effort controls, permission prompts, local slash shortcuts, MCP/agent/skill visibility, encrypted local settings, and rich Markdown/HTML rendering through WebView2.

## Features

- Multi-tab chat sessions with per-tab system prompts
- Two-level chat grouping: projects contain child sessions, while ungrouped sessions live under the Default project
- Saved chat sessions restored on startup, with debounced background persistence and default scroll-to-bottom positioning
- Icon-enhanced tab context menus for renaming, closing, moving sessions between projects, and reordering tabs
- Per-tab status indicators:
  - Busy spinner while Copilot is thinking or running tools
  - Typing indicator while a response is streaming
  - Input-required marker when Copilot is waiting for a permission, choice, or freeform response
  - Unread marker and bold title for background responses
- Live model discovery from GitHub Copilot SDK
- Per-chat model and reasoning effort selection for models that support it
- Chat navigation controls:
  - Scroll to top and bottom
  - Jump to previous and next user question
  - Expand or collapse all message articles
  - Optional per-session auto-collapse of the previous top-level article when sending a new prompt
- Turn-based response display, where all responses for a prompt are grouped under the user message
- Expanding any collapsed user turn opens the latest assistant response in that turn when the nested articles are all collapsed
- User messages include an inline copy button for copying the original prompt text to the Windows clipboard
- User and assistant message articles can be deleted from chat history, with user-message deletion removing the whole grouped turn
- Project-level tab groups can be collapsed and restored with their collapsed/expanded state
- Project tab groups and child session tabs can be moved to top, moved up/down, or moved to bottom within their current level
- Activity bar for transient Copilot status such as reasoning, tool execution, background agent progress, and shortcut activity
- Tool and permission workflow:
  - Folder access rules (read/read-write)
  - Allowed tools and hosts lists
  - Saved permission rules
  - Per-command shell approvals for Copilot SDK shell permission requests
  - Permission and user-input requests appear as interactive chat history articles instead of separate modal dialogs
  - Choice prompts can be answered by clicking a choice; choices are displayed one per line
  - Prompts that allow freeform input include an optional answer box and do not highlight a suggested choice as the default
  - Answered prompt articles disable their controls and collapse by default
  - Memory permission toggle through `/memory`
  - Agent selection through `/agent`
- GitHub token and user secrets with show/hide controls
- Optional settings password for AES-256 encryption of sensitive settings
- User secrets mapped to environment variables for Copilot CLI and MCP servers
- Configurable working directory for Copilot CLI execution
- MCP server, agent, and skill capability view
- Local slash shortcuts for inspecting or changing runtime state
- Extra agent and skill folder configuration
- Project headers toggle their child tab visibility when clicked
- Scheduled automation tasks with cron expressions, manual runs, run history, optional pre/post commands, Copilot steps, and file/HTTP/named-pipe handoff
- Light, dark, system, and follow-the-sun theme options, including themed capability dialogs and chat scrollbars
- Optional Windows tray notifications when Copilot needs user input
- Optional debug logging
- Markdown rendering, collapsible message cards, response pop-out windows, embedded HTML previews, iframe preview pop-out windows, and restored iframe preview heights
- Custom application icon and Windows `.ico` packaging

## Tech Stack

- .NET 9 (`net9.0-windows`)
- WPF
- GitHub.Copilot.SDK
- Microsoft.Web.WebView2
- Markdig
- WPF-UI
- Windows Forms `NotifyIcon` for tray notifications

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

To pass a Copilot session token for the current app run without saving it to settings:

```powershell
dotnet run --project .\CopilotChatbot\CopilotChatbot.csproj -- --gh-token ghp_XXXX
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
- Default auto-collapse behavior for new chat sessions
- Optional tray notifications for permission, choice, and feedback prompts
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
- `/agent` - start the tab's Copilot session if needed, show available user-invocable agents, enable or disable them for this chat, and select the current session's default agent from an in-chat prompt card.
- `/cwd` - show the effective Copilot CLI working directory.
- `/cwd <folder>` - set the Copilot CLI working directory. Quoted paths, relative paths, `.`, and `~` are supported.
- `/env` - show Copilot-relevant environment details. Token values are redacted.
- `/memory` - show the current long-term memory permission state.
- `/memory on` - approve memory permission requests automatically across sessions.
- `/memory off` - reject memory permission requests automatically across sessions.
- `/reset` - reset the current tab's Copilot conversation context while keeping the visible chat history.
- `/usage` - show the latest Copilot usage and quota snapshot (tokens, requests, remaining, reset date).

## In-Chat Prompts

Copilot permission requests and user-response requests are rendered directly inside the chat history as prompt articles.

- Permission prompts provide Deny, Allow once, Allow for session, and Save setting actions.
- Choice prompts can be answered by clicking a choice button, with each choice shown on its own line.
- Prompts that allow freeform input show an optional textbox alongside choice buttons.
- Agent prompts show an enabled-agent checklist and a default-agent dropdown; submitting applies the selection to the current Copilot session.
- When freeform input is allowed, suggested choices are not visually highlighted as the default response.
- Once answered, prompt controls are disabled, the submitted answer is recorded, and the article collapses by default.
- The tab shows an input-required marker while a prompt is waiting.
- If tray notifications are enabled in **Settings > Appearance**, Windows shows a tray balloon when input is required.

Unanswered prompt articles restored from a previous app run are marked expired because their original SDK request is no longer active.

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

Chat sessions are still saved as a flat session list for backward compatibility. Group-aware versions add project metadata, project order, per-session project ids, per-project collapsed state, per-session tab order, and per-session UI preferences such as previous-article auto-collapse; older app versions can ignore that metadata and open the same sessions as unrelated flat tabs.

## MCP Notes

- The app loads user MCP server config from:
  - `~/.copilot/mcp-config.json`
  - `%APPDATA%\GitHub Copilot\mcp-config.json`
  - `<working-dir>/.github/copilot/mcp.json`
  - `<working-dir>/.copilot/mcp-config.json`
- A default read-only GitHub MCP server is added when missing and is also available as a bundled fallback.
- Existing MCP server entries are not overwritten; server names are first-wins.
- The default GitHub MCP server uses `https://api.githubcopilot.com/mcp/readonly` and keeps `Authorization: Bearer $env:GITHUB_TOKEN` in config so the Copilot SDK/runtime can resolve the environment variable.
- The saved GitHub token is injected into the child process as `GITHUB_TOKEN`; `--gh-token` is used only for the Copilot session token.
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
  - To use a Copilot session token for one run, start with `--gh-token ghp_XXXX`.
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
- Input prompt does not seem active:
  - Look for the input-required marker on the tab.
  - Expand the latest prompt article in that chat tab.
  - Restored unanswered prompt articles are historical only and cannot be submitted after restart.
- Tray notifications do not appear:
  - Confirm **Settings > Appearance > Show tray notifications when Copilot needs a response** is enabled.
  - Check Windows notification/focus assist settings if the tray icon is visible but balloons are suppressed.
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
- `UseWindowsForms`: `true`
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
