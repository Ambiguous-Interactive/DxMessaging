# Pull Request Examples

Worked examples for [pull-request-writing](../SKILL.md). Each one shows a real failure mode and
the shorter version that replaces it.

## A rewrite

### Before

> ## Summary
>
> This PR provides a comprehensive fix for the long-standing layout issue in the Flow Graph
> details pane, which has been observed to manifest on Unity 2021.3 LTS but not on newer
> editors. As requested in the previous session, I first investigated whether the root cause
> was related to `align-content` resolution semantics, then determined through experimentation
> that the actual issue is that Yoga does not derive the cross-axis extent of a multi-line flex
> container from its flex lines on that version. Note that this was not caught earlier because
> the local MCP verification loop only runs against a single host editor (6000.4.6f1). Ten
> separate container declarations were audited and it was determined that all of them are
> potentially affected, and so a shared helper was leveraged to seamlessly address the entire
> class of problem in one place, rather than redesigning each container individually.

Everything wrong with it: 130 words with no verdict, three abandoned investigation steps, a
banned word in almost every sentence, and no evidence.

### After

> ## Why
>
> On Unity 2021.3, a wrapping row keeps the height of one line. The extra lines draw outside
> the row, on top of the block beneath it. The Flow Graph details pane has ten such rows, so a
> long type name or a deep hierarchy path can hide the next block.
>
> ## What changed
>
> All twelve wrapping containers in the editor now go through
> `DxMessagingEditorTheme.ApplyContentSizedWrap`. It measures the children and gives the
> container that height. An editor that already sizes the container correctly is left alone.
>
> ## How we know
>
> A probe pins a wrapping row to one line's height, which is what 2021.3 does on its own. The
> unfixed probe leaves its children outside the row. The fixed probe does not. A source scan
> fails if any editor file turns wrapping on by hand again.

95 words. Same change, and now a reviewer can check it.

## Closing several issues in one pull request

One `Why / What changed / How we know` block per issue. No shared preamble.

> Closes #440, #336.
>
> ## Wrapped rows draw outside their box (#440)
>
> **Why.** On Unity 2021.3 a wrapping row keeps one line's height...
>
> **What changed.** ...
>
> **How we know.** ...
>
> ## The MSVC gate passes a compiler that cannot run (#336)
>
> **Why.** ...

Use `###`-free bold labels when the blocks are short. The reader scans for the issue heading,
then reads three sentences.

## When a fourth section is right

Add one only when the reader must act on it, or must not be surprised by it:

- **What is still open.** Name the part the change does not fix, and the issue that tracks it.
- **Risk.** Name the case you could not test, and why.

Do not add a fourth section for what you considered and dropped. That is a session diary.

## Evidence: what goes in the body

Keep the verdict. Move the working.

| Evidence                                   | Where it goes                   |
| ------------------------------------------ | ------------------------------- |
| "793 tests pass, 0 fail"                   | Body                            |
| The full test list                         | Nowhere                         |
| "EditMode step drops from 113.8s to 36.3s" | Body                            |
| The per-run table it came from             | The issue                       |
| "Reproduced on run 31764457664"            | Body, with the link             |
| Console output, stack traces, raw logs     | The issue, in a collapsed block |

The rule behind the table: a number a reviewer would check belongs in the body. The data you
derived it from belongs where it can be re-derived.

## Titles

| Bad                                                                                      | Why                                           | Good                                        |
| ---------------------------------------------------------------------------------------- | --------------------------------------------- | ------------------------------------------- |
| `fix: layout`                                                                            | Says nothing                                  | `Keep wrapped editor rows inside their box` |
| `Refactor DxMessagingFlowGraphWindow flexWrap handling`                                  | Names the mechanism, not the effect           | `Keep wrapped editor rows inside their box` |
| `Fix #440`                                                                               | The reader cannot see the issue from the list | `Keep wrapped editor rows inside their box` |
| `Fix the wrap bug, add MSVC launch probe, close 5 measurement issues, update agent docs` | Four clauses                                  | `Keep wrapped editor rows inside their box` |

The last one is the common case for a session that closes several issues. Lead with the change
a user would notice. The body lists the rest.
