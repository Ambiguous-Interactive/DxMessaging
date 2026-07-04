# DxMessaging Asset Store Listing Draft

## Short Description

Synchronous, type-safe messaging for decoupled Unity systems.

## Description

DxMessaging provides a small message bus for Unity projects that need systems to
communicate without direct scene references. Define typed message structs,
register handlers through lifecycle-owned tokens or `MessageAwareComponent`, and
send through one of three routes:

- Untargeted announcements for global events.
- Targeted commands for a specific GameObject or component.
- Broadcast facts from a known source.

The package includes editor diagnostics for recent emissions, registration
topology, trace evidence, and `MessageAwareComponent` lifecycle warnings.

## Key Features

- Type-safe message contracts with source-generator helpers.
- Lifecycle-managed registrations and cleanup.
- Untargeted, Targeted, and Broadcast routing.
- Message Monitor, Flow Graph, and Inspector overlay editor tools.
- Zero-allocation steady-state dispatch path.
- MIT license and no runtime dependencies.

## Publishing Notes

Do not claim Asset Store availability until Unity review approves the listing.
Use the generated `asset-store-submission` artifact for each version; it contains
the release payloads, checksums, media, and upload checklists.
