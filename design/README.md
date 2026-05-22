# VMentory — Design

This folder is the shared workspace between the **design side** (a separate Claude session focused on UX/visual exploration) and the **codebase side** (the Claude session that implements changes in `wwwroot/index.html`). Nothing in this folder ships in the binary — only `wwwroot/**` is embedded.

## Folder layout

```
design/
├── README.md           ← this file (workflow + conventions)
├── STATUS.md           ← live board: what's proposed, in-progress, shipped
├── proposals/          ← markdown write-ups (the "why" behind a mockup)
├── mockups/            ← standalone HTML files, open directly in a browser
└── requests/
    ├── from-codebase/  ← codebase side drops UI questions / change asks here
    └── from-design/    ← design side drops change specs for implementation here
```

## Conventions

- **Mockups are standalone HTML.** No build step, no external deps — same constraint as the real app. Each file opens in the browser by double-clicking it. Reuse the design tokens from `wwwroot/index.html` (`--bg`, `--bg-card`, `--border`, `--text`, `--blue`, `--green`, etc.) so mockups feel native.
- **Filenames are dated + slugged.** `YYYY-MM-DD-short-name.html` or `.md`. Newest first.
- **Mockups use mock data hardcoded inline.** Don't wire to the real API.
- **STATUS.md is the source of truth** for what stage each proposal is at. Update it when you push or finish something.

## Workflow

### When the design side has a new idea
1. Add an HTML mockup in `mockups/` (and a markdown rationale in `proposals/` if there's a "why" worth writing down).
2. Open a request in `requests/from-design/` describing what should change in `wwwroot/index.html`, referencing the mockup. Use the template below.
3. Add a row to `STATUS.md` under **Proposed**.
4. Commit with a `design:` prefix (e.g. `design: add dense-list mockup for host overview`).

### When the codebase side picks up a request
1. Read the linked mockup + proposal. Open the mockup in a browser if needed.
2. Implement against `wwwroot/index.html`. The design tokens, animation timings, and component class names already in that file are the source of truth — extend them, don't replace.
3. Move the row in `STATUS.md` to **In progress** → **Shipped** (with commit SHA).
4. If the spec is ambiguous, **don't guess** — drop a question in `requests/from-codebase/` and pause that line of work. Commit with `design: question about X` so the design side sees it.
5. Commit implementation with the usual `feat:` / `fix:` / `refactor:` prefix (not `design:`).

### When the codebase side has a UI question or proposal
1. Drop a markdown file in `requests/from-codebase/` using the template below.
2. Add a row to `STATUS.md` under **Awaiting design**.
3. Don't block — keep working on other things. The design side will respond on their next pass.

## Templates

### `requests/from-design/YYYY-MM-DD-slug.md`
```markdown
# <change title>

**Mockup:** `design/mockups/<file>.html`
**Status:** Proposed
**Touches:** `wwwroot/index.html` (sections: <CSS class names or comment markers>)

## What to change
<plain-English description — what the user sees differently after this lands>

## Implementation notes
<gotchas, what to reuse, what NOT to break, accessibility requirements>

## Out of scope
<things the codebase side might be tempted to also change — explicitly listed>
```

### `requests/from-codebase/YYYY-MM-DD-slug.md`
```markdown
# <question or proposal>

**Status:** Awaiting design
**Triggered by:** <commit SHA or feature in flight>

## What I'm trying to do
<context — what implementation work prompted the question>

## The question / proposal
<specific ask — be concrete, link to lines in wwwroot/index.html if relevant>

## What I'd do if no answer
<your default — so design can just ack if it's fine>
```

## Hard rules

- **Don't edit `wwwroot/index.html` from the design side.** Mockups stay in `design/mockups/`. Implementation belongs to the codebase side.
- **Don't add a build step, framework, or external dependency.** Vanilla HTML/CSS/JS only — same constraint as the real app.
- **Don't delete other side's requests.** Mark them resolved in STATUS.md and leave the file as history.
