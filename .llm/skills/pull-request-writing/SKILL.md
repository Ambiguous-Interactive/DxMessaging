---
name: pull-request-writing
description: "How to title and describe a DxMessaging pull request: short Simplified Technical English, one idea per sentence, and a body that answers why the problem mattered, what the change does, and how we know it is correct. Bans jargon, filler, status narration, and session diaries. Use when opening or editing a pull request, writing its title, rewriting a description a reviewer found hard to read, or deciding what evidence belongs in the body."
metadata:
  category: "process"
  tags: "pull-request, writing, simplified-technical-english, ste, review, communication"
---

# Pull Request Writing

A pull request is read by a person who was not in the session. They read the title in a list of
titles, and the body once, to decide whether to trust the change. Write for that reader.

The whole body is written in Simplified Technical English (STE): short sentences, common words,
one idea per sentence, active voice.

## When to use

- Opening a pull request, or editing its title or description.
- A reviewer says a description is long, unclear, or hard to follow.
- Deciding which measurements, tables, or logs belong in the body.
- Writing the pull request that closes several issues at once.

## Rules

### Title

- One line, at most 72 characters, plain sentence case.
- Say what the change does, in the words a user would use. Not the mechanism.
- Start with a verb: `Fix`, `Add`, `Remove`, `Speed up`, `Stop`.
- No conventional-commit prefixes, no issue numbers, no branch names, no "and" chains that
  join three unrelated things. If the title needs three clauses, lead with the one a reader
  cares about most and let the body carry the rest.

Good: `Keep wrapped Flow Graph rows inside their own box`
Bad: `fix(editor): DxMessagingFlowGraphWindow flexWrap container height resolution on 2021.3`

### Body shape

Three short sections, in this order, and nothing else by default:

1. **Why** - what went wrong for a user, or what they could not do. One short paragraph.
1. **What changed** - what the code does now. A short list if there is more than one part.
1. **How we know** - the test, the measurement, or the run that proves it.

Add a `Closes #NNN` line for each issue the change closes. Add a fourth section only when the
change leaves something open that a reader must know about.

### Length

- Aim for 200 words. Stop at 400.
- One pull request that closes several issues gets one `Why / What changed / How we know` block
  per issue, each still short. It does not get a longer preamble.
- Move deep evidence to the issue or to a `progress/` record and link it. The body carries the
  verdict, not the working.

### Simplified Technical English

- One idea per sentence. Under 20 words.
- Active voice, present tense: "The row now grows", not "The row will have been grown".
- One word per meaning. Pick `fix` or `repair`, not both, and use it everywhere.
- Use the plainest word that is still exact: `use` not `utilize`, `so` not `hence`, `before`
  not `prior to`, `now` not `at this time`.
- Expand an abbreviation on first use unless it names a file, a type, or a workflow.
- No nested clauses, no dashes carrying a second thought, no sentence that needs re-reading.
- ASCII only, exactly as in [documentation-style](../documentation-style/SKILL.md).

### Banned

- Filler and marketing: `comprehensive`, `robust`, `seamless`, `leverage`, `delve`, `simply`,
  `just`, `note that`, `it is worth noting`.
- Status narration: `As requested`, `Per the previous session`, `This PR does the following`.
- Session diaries: what was tried and rejected, how long something took, which agent did what.
  A rejected approach belongs in the issue only when the next person would otherwise retry it.
- Restating the diff. The file list is already on the page.
- Emoji, headings deeper than `###`, and tables with one row.

## Verification

Before you open or update the pull request, read the title and body once as a stranger:

- Does the title alone say what changed?
- Does the body say why it mattered before it says what you did?
- Is there a sentence over 20 words, or a word a new contributor would look up?
- Is there a claim with no evidence behind it?

Fix what fails. Do not add words to fix it.

## See Also

- [Pull Request Examples](./references/pull-request-examples.md) - a rewrite, an issue-closing
  body, and the fourth-section case.
- [documentation-style](../documentation-style/SKILL.md) - the ASCII and banned-phrase rules
  this skill inherits.
- [changelog-management](../changelog-management/SKILL.md) - what the same change owes
  `CHANGELOG.md`, which is written for users, not reviewers.
