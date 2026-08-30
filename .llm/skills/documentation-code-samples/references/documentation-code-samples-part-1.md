## Overview

Continuation extracted from `documentation-code-samples.md` to keep files within the repository line-budget policy.

## Solution

## Testing Considerations

### Verifying Code Samples Compile

Extract and compile samples in CI where possible:

```csharp
[Test]
public void DocumentedDamageMessagePatternCompiles()
{
    MessageBus messageBus = new MessageBus();
    MessageRegistrationToken token = messageBus.CreateRegistrationToken();

    int damageReceived = 0;
    MessageRegistrationHandle handle = token.RegisterUntargeted<SimpleUntargetedMessage>(
        (in SimpleUntargetedMessage msg) => damageReceived++
    );

    SimpleUntargetedMessage message = new SimpleUntargetedMessage();
    messageBus.UntargetedEmit(ref message);

    Assert.AreEqual(1, damageReceived, "Handler should have been invoked exactly once");

    handle.Dispose();
    token.Dispose();
}
```

### Documentation Review Checklist

- [ ] All code samples paste into a real project without errors
- [ ] API method names match the actual codebase
- [ ] Fast handler signatures use `in TMessage` pattern
- [ ] Version annotations are accurate
- [ ] Links to related docs work

## See Also

- [Documentation Updates](../../documentation-style/references/documentation-updates.md)
- [Documentation Style Guide](../../documentation-style/references/documentation-style-guide.md)
- [Documentation Update Workflow](../../documentation-style/references/documentation-update-workflow.md)
- [Link Quality Guidelines](../../markdown-authoring/references/link-quality-guidelines.md)

## References

- [Unity Best Practice Guides](https://docs.unity3d.com/Manual/best-practice-guides.html)

## Changelog

| Version | Date       | Changes                                                  |
| ------- | ---------- | -------------------------------------------------------- |
| 1.1.0   | 2026-01-22 | Added guidance on code fence languages for anti-patterns |
| 1.0.0   | 2026-01-22 | Initial version                                          |

## Related Links

- [Documentation Code Samples](./documentation-code-samples.md)
