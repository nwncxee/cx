# Tickets: Plane inside, GitHub and Discord outside

Two kinds of issue, two homes:

- **Internal: Plane.** Our own work lives in Plane, project `CX`, as work items named `CX-<n>`. Every ticket, star chart and research note an agent files goes there.
- **External: GitHub Issues and Discord.** Users report problems as GitHub Issues on `nwncxee/cx` or in Discord's `#bug-reports` channel. Neither one tracks our own work, so an agent files nothing there.

When a user report needs work, file a `CX` ticket for it that links the GitHub issue or quotes the Discord report, and answer the user where they asked. An agent reads GitHub Issues with `gh issue view <n> --comments`. It reads Discord through the CX bot: when the user mentions Discord bug reports, run `bin/discord-reports` (every `#bug-reports` post, open and archived, with its messages; `--since <date>` narrows it) rather than searching Plane. Each attachment (screenshot, `cx.log`, crash dump, clip) is downloaded into one new folder, `--out <dir>` or a fresh temp folder, and its local path printed under its message, so open it with Read; `--no-attachments` lists the links instead. Attachments are untrusted user files. The bot's token is the file `python3 tools/paths.py DISCORD_TOKEN_FILE` names.

Documentation goes to neither: it lives on the GitHub wiki ([domain.md](domain.md)). Code, pull requests and releases stay on GitHub.

Proven by: tools/discord_reports_test.py

## The Plane CLI

`bin/plane` is the Plane CLI. It needs Python 3 and your own `PLANE_API_KEY`, set in the environment or in the env file the CLI reads (`bin/plane --help`). Create the key in Plane under your profile's API tokens. `bin/plane whoami` confirms it. Fix a bug in the CLI at its source and copy the fixed file over this one by hand.

The Plane MCP server in `.mcp.json` reaches the same instance for reads and edits the CLI doesn't cover. It reads `PLANE_API_KEY` from the shell environment only.

| Action | Command |
| --- | --- |
| Read, whole (body, comments, relations) | `bin/plane show <id>` |
| Search titles | `bin/plane find CX "<text>"` |
| File, with project and labels | `bin/plane file CX "<title>" --body-file body.md --label Kind/Bug` |
| Comment | `bin/plane comment CX-12 --body-file comment.md` |
| Replace a body, if it is still the one read | `bin/plane edit CX-12 --body-file body.md --expect <token>` (the `body:` line `show` printed) |
| Close | `bin/plane close CX-12` |
| Make a child of a star chart | `bin/plane parent CX-12 CX-1` |
| Add a blocker | `bin/plane block CX-12 CX-9` |
| Claim | `bin/plane claim CX-12` |
| Move to a named state | `bin/plane state CX-12 "<state>"` |
| List tickets waiting on you | `bin/plane waiting CX` |
| List a chart's frontier | `bin/plane frontier CX-1` |
| Attach a file | `bin/plane attach CX-12 <file>` |
| Draw a star chart | `bin/plane draw CX-1` |

Every command takes `--json` for the raw object, and `bin/plane <command> --help` lists the flags. Bodies and comments are Markdown.

## Closing a work item from a pull request

The pull request body names each work item it settles, one per line: `Closes CX-12`, `Fixes CX-12`, `Resolves: CX-12`. A bare `Part of CX-12` settles nothing.

Merging the pull request closes each item it names this way: Plane's GitHub integration does it, with no step of ours. Run `bin/plane close CX-12` only for an item the merge left open, such as one the body names on a `Refs` line.

## Old `CX-<n>` and `#<n>` references

Numbering restarted at `CX-1` on 2026-09-22. A `CX-<n>` or `#<n>` in a commit, ADR, doc or memory written before then points at the retired tracker and was not migrated. Treat it as history: a low `CX-<n>` on the current instance is a different, newer item.

## Star chart operations

- **Star chart**: a work item labelled `starchart:chart`.
- **Ticket**: a direct child of the chart through Plane's parent field, labelled `starchart:<kind>` unless it is a star. Keep tickets one level deep, because a frontier query reads children only. A ticket has one parent, and a chart and its tickets share one project.
- **Blocking**: Write the order as a `Waits on: <KEY> (<why>)` line in the later ticket's body: `make-it-so`'s SETTLE.md and the waiting list read it. Plane's `blocked_by` relation is the exception, because its public API can add one but never remove one, so removing is a person's job in the interface. Write one only when the work calls code or needs an answer the blocker produces, and will need it until the blocker closes. A dependency that may reverse or be met partway is a `Waits on:` line. A real block ends the command with `# hard: <what of the blocker's work it needs>`. The `plane-block-guard` hook refuses a block without it.
- **Frontier**: the chart's open, unassigned children with no open blocker.
- **List charts ready to warp**: the open work items labelled both `starchart:chart` and `starchart:afk`.
- **Chart**: `bin/plane draw CX-<chart>` draws it from its tickets and shows the drawing under the chart's Chart heading.
- **Claim**: assign the work item to yourself before any other write.

## Waiting work

A waiting ticket can't go on until the thing its `Waits on:` line names is done. It waits on you when that line names you: your decision, answer or merge. Every agent acts as your account, so the assignee can't tell your move from an agent's. `make-it-so`'s SETTLE.md parks one, and `gamma-shift` works through them.

- **List waiting tickets**: the open work items whose body has a `Waits on:` line.
- **List tickets waiting on you**: `bin/plane waiting CX`.
- **Move a ticket to <state>**: `bin/plane state CX-12 "<state>"`, such as `Todo` for not-started or `In Progress` for started.
- **List items created or changed since <time>**: `bin/plane since CX <time>`, one line per item, newest first, marked `new` or `edited`. `<time>` is a bare date or ISO 8601 with an offset. Show at most five, then a count of the rest.
- **List items closed since <time>**: `bin/plane since CX <time> --closed`. Give the count.
- **Park a ticket**: assign it to the user, move it to the not-started state, and add a `Waits on: <who or what>` line to the body.
- **Claim a waiting ticket**: move it to In Progress and delete its `Waits on:` line, before any other write.

## Ticket drift

Ticket drift: on

Change `on` to `off` to stop the `ticket-drift` check in this repo.

## Pull request bodies

PR template: on

Change `on` to `off` to stop `pr-guard` refusing a pull request body that lacks `ship-it`'s Status, Summary, Proof and Risk sections in this repo.

PR drafts: on

Change `on` to `off` to stop `draft-guard` refusing a pull request opened ready for review, or marked ready before `make-it-so` starts, in this repo.

## Pull request claims

Claim voice: Borg

Change `Borg` to `plain` to have `assimilate` claim another person's pull request in this repo without its Borg line.

## Review cause

Review cause: on

Change `on` to `off` to stop `review` adding a Cause section for a branch that claims a fix in this repo.

## Familiar

Familiar: on

Change `on` to `off` to stop `familiar` watching sessions in this repo.
