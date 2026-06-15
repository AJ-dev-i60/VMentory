# Engineering Discussions

This is the shared, durable workspace for **architectural and implementation decisions** that
are bigger than a single code change — sequencing, trade-offs, "which approach," anything where
the reasoning needs to outlive a chat session and be visible to every agent and every codebase
worker.

It is deliberately **cross-cutting** (not under `docs/phase2/`) so it stays useful across phases.

## Who uses this
- The **engineering agent** (`.claude/agents/engineering.md`) drives discussions: it picks up open
  topics, analyzes them against the docs and the real codebase, and writes the reasoning + a
  recommendation here.
- **Any agent or general chat** reads `REGISTER.md` first to see what's open, in discussion, or
  decided — so nobody re-litigates a settled decision or builds against a stale assumption.
- The **documentation agent** folds *decided* outcomes back into the specs under `docs/phase2/`.
- The **human owner** makes the final call on anything marked `Awaiting decision`.

## Layout
```
docs/engineering/
├── README.md                ← this file (protocol)
├── REGISTER.md              ← the board: every topic + its status. Read this first.
└── discussions/
    └── NNNN-slug.md         ← one file per topic; moves through states, gets a Decision appended
```

One file per topic. It is the whole life of the discussion — context, options, analysis,
recommendation, and (once resolved) the decision and its consequences. We do **not** split
"discussion" and "decision" into separate files; the decision is a section appended to the same
file so the reasoning and the outcome never drift apart.

## States (set in the file's front line and in REGISTER.md)
- **Open** — raised, not yet analyzed.
- **In discussion** — being actively worked (engineering agent or a session).
- **Awaiting decision** — analysis done, recommendation made, needs the human owner to choose.
- **Decided** — outcome recorded in the Decision section. Downstream docs/code can rely on it.
- **Superseded** — replaced by a later decision (link it).

## Workflow
1. **Raise:** anyone (agent or human) adds a topic file from the template below and a row in
   `REGISTER.md` under **Open**. Number it next in sequence (`0001`, `0002`, …).
2. **Discuss:** the engineering agent (or a session) fills in Options, Trade-offs, and a
   Recommendation, grounding claims in real files (`path:line`) and the existing docs. Move to
   **In discussion** → **Awaiting decision**.
3. **Decide:** the human owner picks. The agent records it in the **Decision** section with the
   date, the choice, the rationale, and consequences. Move to **Decided**.
4. **Propagate:** the documentation agent updates the affected specs and notes the decision ID
   (`ENG-NNNN`) where the decision lands. Locked decisions in `docs/phase2/ARCHITECTURE.md` should
   cite the ENG-NNNN that produced them.

## Topic template
```markdown
# ENG-NNNN — <title>

**Status:** Open
**Raised:** YYYY-MM-DD by <agent/human>
**Affects:** <docs/files/milestones this decision constrains>
**Related:** <other ENG-NNNN, specs, skills>

## Context
<what's true now, why this is a question, what's already decided that bounds it>

## The question
<the specific fork, stated so a yes/no or A/B/C answer resolves it>

## Options
### A — <name>
<what it is> · **Pros** … · **Cons** …
### B — <name>
…

## Recommendation
<the engineering agent's reasoned pick, with the trade-off it accepts>

## Open sub-questions
<things that must also be answered, or that block this one>

## Decision
> _Pending._  <!-- filled on resolution: date · choice · rationale · consequences -->
```
