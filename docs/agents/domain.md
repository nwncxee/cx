# Domain docs

Read these before exploring the code, when they exist:

- `GLOSSARY.md` at the root.
- Each decision record in `docs/adr/` that touches the area.

ADR format: decision-record

Todo file: on

Chart cap: 8

Found work check: on

Patch-tell hint: on

Write `Patch-tell hint: off` to stop the reminder when an edit to a CI workflow adds a patch tell: a retry, a sleep, `continue-on-error`, a switched-off step, a skipped test or a raised timeout.

Law proofs: on

Write `Law proofs: off` to stop `law-lint.sh` listing rule lines that name no `Proven by:` check, and to stop review and make-it-so running a Laws line.

Post-mortems: the wiki namespace For Staff :: Post-mortems

Write where this repo keeps post-mortems (a folder or a wiki namespace), or `Post-mortems: off` to stop `post-mortem` writing them and its check.

Pre-mortems: on

Write `Pre-mortems: off` to stop the Foreseen check before a star chart is filed.

Checklist lint: full

Write `Checklist lint: basic` to keep only the person, kit and proof checks on over-to-you pages.

The `.todo` file at the root holds small items, one per line, too small for a ticket. Delete an item's line once it is done.

Use the glossary's words in tickets, tests and names. A word the glossary lacks is either invented (pick the glossary's word) or a gap (raise it with the user, and write the settled word into the glossary). When your work contradicts a decision record, say so and name it before you go on.
