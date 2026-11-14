---
name: wiki-mcp
description: 'Query and keep the Westgate wiki, the agents'' knowledge base, through the `westgate-wiki` MCP server. Use first whenever you need to know how a system, mechanic, command, service or data format works, before you read repo docs or grep for it. Use whenever a change is one a player, developer or operator could notice (a mechanic, command, system, setup step, data format, third-party asset), whether or not an article covers it yet, to update, create or retire the article in the same effort. Use too whenever a wiki page, index or lint result looks disorganized, hard to navigate or off the Wiki Organization law: agents maintain the wiki and fix it on sight.'
---

# Westgate wiki through MCP

> _The wiki remembers so the next agent doesn't have to: read it first, check it, write back what it lacked._

The wiki is the agents' knowledge base, kept the way an LLM wiki is kept: a
catalog (`wiki_index`), a change log (`wiki_log`), a health check
(`wiki_lint`) and lean reads (`wiki_read` outline and section) exist so an
agent queries it first, verifies what it read, and leaves it truer than it
found it. It is the project's **source of record** (sow-platform ADR-0136):
decisions, standards, design notes and system docs live here and in no
repository. A repository keeps only its READMEs, agent guides and skills. A
change that adds, alters or removes something a reader could notice updates the
wiki in the same effort. The
project is semi open source, so technical documentation is public as well.

The `westgate-wiki` server (`https://westgate.pw/mcp`) acts as the member who
connected it, with that member's own forum privileges. The first call opens a
browser login on the forum; the member picks a tier on the consent page.

| Tier | Allows | Pick it when |
|---|---|---|
| 1 Read | search, list, read, history, revision | checking what the wiki says |
| 2 Read and write | also create, edit, revert | the effort updates documentation (the usual case) |
| 3 Read, write, tombstone and move | also tombstone, restore, move | the effort retires or relocates articles |

Ask for the lowest tier the task needs. A call above the tier comes back as a
`scope` refusal: tell the member to reconnect with a higher tier.

## Branches

- **Look up**: you need to know how something works. Run [Look up](#look-up).
- **Change**: the effort changes something a reader could notice. Run
  [Workflow](#workflow).

An effort can take both: a look-up first, then the change. Every branch
also runs [Maintain](#maintain): you are the wiki's maintainer, not a visitor.

## Maintain

Agents own the wiki's organization and ergonomics in full. No person audits
it after you, so what you pass and leave broken stays broken.

1. **Fix what you read.** On every page and index a look-up or a change reads,
   fix each breach of the Wiki Organization law you see: a dead link, a page
   missing from its index or under the wrong kind heading (W11), a card off
   the W20 shape, a summary or first sentence that is false or missing (W17),
   no Related heading (W15), a page in the wrong home (W8). Done when each
   page you read meets the law, or its fix is a person's and is in your report.
2. **Lint where you were.** `wiki_lint` each namespace you read or wrote in,
   never `""`. Fix every row in the pages that namespace holds. Done when it
   shows no row you could fix.
3. **Kill a returning row.** A lint row the law does not count as a breach,
   or one that comes back after a fix, means the check (the wiki plugin, in
   `sow-nodebb`) or the law is wrong. Fix whichever is wrong in the same
   effort: a pull request for the check, a `wiki_replace` for the law. Done
   when the next lint of that namespace shows only new work.
4. **Report** in one line per namespace: what you fixed, and each ask only a
   person can answer (delete a namespace, change the Main Page).

## Look up

1. **Pillars, then catalog.** Read For Staff :: Pillars and the pages it
   lists that touch the work: they bind every design and every change. Then
   `wiki_index` on the namespace that owns the subject (see
   [Where an article lives](#where-an-article-lives)), or `wiki_search` on its
   in-game or service name. On an effort that spans sessions, also run
   `wiki_log` with `since` set to seven days back, to see what other sessions
   changed. Done when you hold the tid of each article that covers the
   question, or both calls came back without one.
2. **Lean read.** `wiki_read` with `format: "outline"`, then again with
   `section` set to the heading that answers. Read a whole body only to edit
   it. Done when you hold the section that answers, or the outline shows the
   article does not.
3. **Verify.** The article is the record. Check each claim your work rests on
   against the code and data it describes, in this session. Where they
   disagree, the mismatch is a bug in one of them, never a reason to trust the
   code: fix whichever is wrong in this effort, or file the gap on the effort's
   chart. Done when each claim is confirmed or its mismatch is fixed or filed.
4. **File back.** A wrong claim: correct the article in this effort, through
   [Workflow](#workflow) step 4. An answer the wiki lacked that the next reader
   would need: add it to the article that owns the subject. Done when the
   wiki holds what you verified, or the PR body names the gap and why it
   waits.

## Workflow

1. **List** what the change adds, alters or removes that a reader could
   notice. A change with nothing observable (a refactor, a test) has an empty
   list and ends here.
2. **Find** the article for each item: `wiki_search` on its in-game or service
   name, then `wiki_list_namespace` on the namespace that owns it (see
   [Where an article lives](#where-an-article-lives)). An item has no article
   only when both come back without it.
3. **Read** each found article with `wiki_read` and keep its `revisionId`.
4. **Write** now, one branch per item, before the change merges: publish at
   once, so the wiki never falls behind the code, and let the user edit it
   later.
   - found, still true in part → `wiki_edit` with the full HTML body and the
     `revisionId` from step 3;
   - missing → `wiki_create` in the owning namespace, then link it from the
     nearest related article;
   - the thing it describes is gone → edit the article to say so, or
     `wiki_tombstone` it (tier 3) when nothing of it remains.
5. **Verify** with `wiki_read`: each body holds the change.
6. **Lint** with `wiki_lint` on each namespace you wrote in, never on `""`
   (the whole wiki's answer overflows a tool result). Fix every broken link and
   orphan in an article you created or edited. Fix
   every path rule breach (W1–W7) in those namespaces, then run
   `wiki_list_namespace` on `""` and move every root stray (W1) to its home.

Done when every item from step 1 is described by an article in the namespace
that owns it, `wiki_lint` shows no broken link or orphan in those articles,
and the PR body lists them by title.

## Where an article lives

The wiki article For Staff :: Standards :: Wiki Organization is the law for
where an article goes and what shape it takes; read it before any
`wiki_create`, `wiki_move` or title change. Its rules are numbered W1 to W20,
and a finding names its number. This table summarizes it, and a repository's
agent guide points here rather than restating it. A title is short, two to
five words, with no repository name, path or ticket key.

- **Path rules (W1–W7) are fixed on sight, without asking**, in any namespace
  you list, not only in what you wrote: a root stray (W1), a path over four
  parts (W2), a title with two `::` (W3), a namespace under four standalone
  pages (W4), a namespace that is one master page (W5), a missing or stray
  index article (W6), a title that repeats its path (W7). A fix only a person
  can make (create or delete a namespace) goes in the report as an ask.
- **File by first match (W8).** The first yes in that page's ordered
  questions is the home. Two homes fitting means the page lacks a tie-break:
  add it there, then file.
- **Page rules (W10, W11, W13–W20) are fixed on every page you read or
  edit**, and on the index you reached it from. A page holds one Diátaxis kind (tutorial, how-to guide, reference,
  explanation) and sits under that kind's heading in its namespace's index
  article (W10, W11); a topic heading may sit one level below a kind heading,
  and a subpage gets its own card (W20). A how-to title starts with a verb (W13). Link a term at
  its first mention, with the target's title as the link text (W14). The page
  ends with a Related heading of two to four links (W15), each about this
  page's subject or the reader's next step, never a sibling copied from a
  template.
- **A move or rename breaks links (W12).** The old address redirects readers,
  but every link still names it. Before it, `wiki_search` the old path; after it, edit every linking article
  to the new `url`, and `wiki_lint` both namespaces. A master page carries its
  subpages when it moves. A `Parent :: Child` page
  moved into a namespace named Parent doubles its path until its title drops
  `Parent ::`.
- **A wiki manager makes a namespace**, with `wiki_create_namespace` when
  you are connected as one, and sets its read permission before anything
  moves in: a new namespace under For Staff is public until then. Deleting
  one takes a person: ask for it.

| Namespace | Holds |
|---|---|
| Documentation | Technical documentation of the server and its content: services, plugins, hooks, console commands, data formats. Public on purpose. |
| For Staff | Hidden, visible to staff (developers and dungeon masters): operations by role, staff tooling, work in progress, spoilers and DM notes. Infrastructure decisions, runbooks and deploy notes stay in sow-platform (ADR-0136). Credentials and secrets never go on the wiki at all. |
| For Staff / Decisions | A namespace: every project decision is an article in it, titled by its topic, listed under its area's section of the Decisions index article. The citation (`sow-platform ADR-0074`) opens the body in bold, so a search for it finds the page. A new decision starts here. Before you write or change a decision page, `wiki_read` the namespace's index article, Decisions: it holds the page format and when an old page is amended, superseded or retired. A new page that replaces an old one in full names the old citation in its Consequences, and the old page is then tombstoned (tier 3). |
| Guides | Walkthroughs that help a player do something. |
| Systems | Player-facing mechanics, with the class, race, feat, skill, spell and item type articles in their own child namespaces. |
| Lore | The setting's prose. |
| Credits | A namespace of attribution, one page per category: Code, Tools, Art, Audio, Fonts, Infrastructure, Contributors, Special Thanks, and Assets split by kind under its own subpages. Every third-party asset, model, script, library or pack the change brings in gets a row in the matching article's table. Add a row, and create no article for a single source. |
| Frequently Asked Questions | One article. Add a question once players ask it repeatedly. |

Root articles: Main Page, Community Charter, Setting Overview, The Team, and
Gameplay Changes. Gameplay Changes summarises how the server differs from
vanilla Neverwinter Nights, so a change to a core rule updates its entry
there too. The others change only when the member asks.

## Rule pages are written by hand

No generator writes to the wiki (GAME-2700). The class, race, feat, skill,
spell and item type articles under Systems are written by hand, and the
effort that changes a rule updates its article. An article may state a value
its table holds; server code still reads it from the table (ADR-0009).

## Writing rules

Everyone can read Documentation, Guides, Systems, Lore, Credits and the FAQ.

- Write for a reader who has only the wiki: name systems by their in-game or
  service names.
- Link to other articles with the `url` a search or read returned.
- Leave out file paths, repository names, `.md` files, and any mention of
  agents, skills, or tooling. For Staff may discuss tooling, and still
  carries no local path or secret.
- Spoilers stay in For Staff. Public articles describe what
  a player can learn in play.
- The body is HTML: `h2`/`h3`, `p`, `ul`/`ol`/`li`, `table`, `pre`/`code`,
  `strong`/`em`, `a`. It is sanitized on save, so re-read after writing when
  markup matters.

## Refusals

| Reason | Do |
|---|---|
| `conflict` | The article changed since your read, or someone has it open in the editor. Read again, redo the change on the new body, retry once. If someone holds it open, tell the member who. |
| `rate-limited` | Wait `retryAfter` seconds, then continue. |
| `privilege` | The member lacks the forum privilege. Report it; a higher tier does not help. |
| `protected-page` | Index pages and the homepage are never tombstoned, moved or renamed by an agent. A person does it in the browser. |
| `scope` | See the tier table above. |
| `not-found`, `validation` | The message names the bad input. Search again for the path or tid, or fix the title or body, and retry. |
