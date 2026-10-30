# Tickets: Plane

Tickets are Plane work items in project `CX`, named `CX-<n>`. Code, pull requests, releases and player bug reports stay on GitHub (`nwncxee/cx`). Client and renderer work is filed in `CX`.

`bin/plane` is the Plane CLI. It needs Python 3 and your own `PLANE_API_KEY`, set in the environment or in the env file the CLI reads (`bin/plane --help`). Create the key in Plane under your profile's API tokens. `bin/plane whoami` confirms it. Fix a bug in the CLI at its source and copy the fixed file over this one by hand.

The Plane MCP server in `.mcp.json` reaches the same instance for reads and edits the CLI doesn't cover. It reads `PLANE_API_KEY` from the shell environment only.

| Action | Command |
| --- | --- |
| Read, whole (body, comments, relations) | `bin/plane show CX-12` |
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

A merge on GitHub does not reach Plane. After the merge, run `bin/plane close CX-12` for each named item, and `bin/plane comment` the pull request on it.

## Old `CX-<n>` and `#<n>` references

Numbering restarted at `CX-1` on 2026-09-22. A `CX-<n>` or `#<n>` in a commit, ADR, doc or memory written before then points at the retired tracker and was not migrated. Treat it as history: a low `CX-<n>` on the current instance is a different, newer item.

## Star chart operations

- **Star chart**: a work item labelled `starchart:chart`.
- **Ticket**: a direct child of the chart through Plane's parent field, labelled `starchart:<kind>` unless it is a star. Keep tickets one level deep, because a frontier query reads children only. A ticket has one parent, and a chart and its tickets share one project.
- **Blocking**: Plane's `blocked_by` relation. Write one only when the work can't start before the blocker closes; a softer order is a line in the body. Plane's public API can add a relation but not remove one, so removing is done in the interface.
- **Frontier**: the chart's open, unassigned children with no open blocker.
- **Chart**: `bin/plane draw CX-<chart>` draws it from its tickets and shows the drawing under the chart's Chart heading.
- **Claim**: assign the work item to yourself before any other write.

## Waiting work

A waiting ticket is assigned to the user and can't go on until the thing its `Waits on:` line names is done. `loose-ends` parks one, and `gamma-shift` works through them.

- **List tickets waiting on you**: `bin/plane waiting CX`.
- **Park a ticket**: assign it to the user, move it to the not-started state, and add a `Waits on: <who or what>` line to the body.
- **Claim a waiting ticket**: move it to In Progress, before any other write.
