# Concepts

[Documentation Home](../index.md) | [Getting Started](../getting-started/getting-started.md)

---

This section explains the core concepts behind DxMessaging. **Concepts** are the foundational ideas and mental models that inform how you design and structure your messaging --
understanding these will help you make better architectural decisions and avoid common pitfalls.

## Start Here

- **[Mental Model](mental-model.md)** -- How to think about DxMessaging. Covers the philosophy, the three message types with analogies, tokens and lifecycle, and when to use what.
  Read this first.

## Core Concepts

- **[Message Types](message-types.md)** -- The three categories (Untargeted, Targeted, Broadcast) with code examples and decision guides.

- **[Targeting and Context](targeting-and-context.md)** -- How DxMessaging uses GameObjects and Components as message context, and the role of `InstanceId`.

- **[Listening Patterns](listening-patterns.md)** -- All the ways to receive messages: targeted, untargeted, broadcast, and "without targeting/source" patterns.

- **[Interceptors and Ordering](interceptors-and-ordering.md)** -- Control message flow with priorities, post-processors, and interceptors.

## Quick Reference

| Concept                                  | One-Line Summary                      |
| ---------------------------------------- | ------------------------------------- |
| [Mental Model](mental-model.md)          | Philosophy and first principles       |
| [Message Types](message-types.md)        | The three message categories          |
| [Targeting](targeting-and-context.md)    | GameObjects and Components as context |
| [Listening](listening-patterns.md)       | Ways to receive messages              |
| [Ordering](interceptors-and-ordering.md) | Priority and interception             |

## Related Sections

- [Getting Started](../getting-started/index.md) -- Hands-on walkthrough
- [Guides](../guides/patterns.md) -- Practical patterns and recipes
- [Reference](../reference/reference.md) -- API documentation
