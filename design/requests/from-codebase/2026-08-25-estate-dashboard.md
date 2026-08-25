# Request: design pass on the Estate dashboard + action tracker (ENG-0015)

**From:** codebase · **Date:** 2026-08-25 · **Shipped interim:** yes (branch `feat/eng-0015-estate-dashboard`)

## What exists

Three new views in `wwwroot/index.html`, all under the `// ── Estate dashboard + action tracker` marker,
CSS in the `/* ── Estate dashboard + action tracker (ENG-0015) ── */` block:

1. **Estate** (`vEstate`, now the default view). Five summary tiles — hardware OK/warn/crit, open
   actions by priority, guests running/total (live vs recorded), next site visit (items + parts +
   next window), backup coverage — then machine cards in two sections (Servers, Network). A card
   shows: hardware status dot, name, kind badge (PVE/HV/BARE/STOR/NET), role, model/address/mgmt,
   non-OK subsystem chips, up to three faults, guests count with a `live`/`static` source badge, open
   action count coloured by worst priority.
2. **Machine detail** (`vMachine`, `#machine/<key>`). Facts strip, notes, hardware panel (all
   subsystems + faults), open actions with tick-off, guests table with the static-list caveat.
3. **Actions** (`vActions`, `#actions`). Filter chips with counts, a "Bring / At each machine" box
   on the Site-visit and Purchases filters, then rows grouped Critical → High → Medium → Low. A row:
   checkbox, `#ref`, title + group, badges (class, downtime, window, "waits on N", "N/M VMs",
   status, monitor-raised, machine chips). Expanded: why / how / done-when / bring list / waits-on
   (with link + "new prerequisite") / must-be-done-before / "new follow-up"; blast radius per machine
   with VM chips; schedule inputs; notes + add; status buttons; edit.
4. **New / edit action modal** (`#m-action`).

## What design owns

- **Two severity axes on one page.** Hardware health (OK/Warning/Critical — green/yellow/red dot)
  and action priority (Critical/High/Medium/Low — red/yellow/blue/grey pill) share colours but mean
  different things. Decide whether that is fine or whether priority should take a distinct treatment.
- **The site-visit checklist.** The "Bring" box is a first pass. The owner's stated need
  (2026-08-03) is "combine all of these while you're there" + "this requires purchasing the
  following" — a printable one-page visit sheet grouped by machine, with the parts list, would be
  the finished form.
- **Density.** Action rows carry a lot of badges; the expanded body is a two-column grid that
  collapses at 900px. Review on a laptop width.
- **Static vs live guests.** The `static` badge and the "a claim about the past" caveat matter —
  every Hyper-V host is static until ENG-0013 lands. Keep it visible, make it calmer.

## Out of scope

Data model, API, and the seed. The hardware fault taxonomy is the monitor's, not ours.
