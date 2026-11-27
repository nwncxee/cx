# Domain docs

## Where things go

| What | Where |
| --- | --- |
| Documentation, for players, server owners and contributors | The GitHub wiki, `https://github.com/nwncxee/cx/wiki` |
| Our own work: tickets, star charts, research | Plane, project `CX` ([issue-tracker.md](issue-tracker.md)) |
| Reports from users | GitHub Issues on `nwncxee/cx`, and Discord's `#bug-reports` ([issue-tracker.md](issue-tracker.md)) |
| Code, pull requests, releases | GitHub, `nwncxee/cx` |

The GitHub wiki is a git repository, `git@github.com:nwncxee/cx.wiki.git`. Clone it, edit its Markdown pages, then commit and push to `master`. Its pages are flat, so a group of pages shares a name prefix and an index page that links each one. This repo's wiki is never the `westgate-wiki` MCP server: that wiki belongs to another project.

When a change is one a player, server owner or contributor could notice, update its wiki page in the same effort, and name the page in the pull request body.

## Read first

Read these before exploring the code, when they exist:

- `GLOSSARY.md` at the root.
- Each decision record in `docs/adr/` that touches the area.

Use the glossary's words in tickets, tests, names and wiki pages. A word the glossary lacks is either invented (pick the glossary's word) or a gap (raise it with the user, and write the settled word into the glossary). When your work contradicts a decision record, say so and name it before you go on.

## Settings

ADR format: decision-record

Todo file: on

The `.todo` file at the root holds small items, one per line, too small for a ticket. Delete an item's line once it is done.

Chart cap: 8

Sketches: docs/sketches/

Found work check: on

Patch-tell hint: on

Write `Patch-tell hint: off` to stop the reminder when an edit to a CI workflow adds a patch tell: a retry, a sleep, `continue-on-error`, a switched-off step, a skipped test or a raised timeout.

Law proofs: on

Write `Law proofs: off` to stop `law-lint.sh` listing rule lines that name no `Proven by:` check, and to stop deathmatch and make-it-so running a Laws line.

Post-mortems: the GitHub wiki, one page per incident named `Post-mortem-YYYY-MM-DD-<slug>`, each linked from the `Post-mortems` page

Write where this repo keeps post-mortems (a folder or a wiki namespace), or `Post-mortems: off` to stop `post-mortem` writing them and its check.

Pre-mortems: on

Write `Pre-mortems: off` to stop the Foreseen check before a star chart is filed.

Checklist lint: full

Write `Checklist lint: basic` to keep only the person, kit and proof checks on over-to-you pages.
