using DxMessaging.Core;
using DxMessaging.Core.Messages;
using DxMessaging.Unity;
using UnityEngine;

public sealed class MessagingObserver : MessageAwareComponent
{
    [SerializeField]
    private bool logTypedClicks = true;

    [SerializeField]
    private int typedClickCount;

    [SerializeField]
    private int acceptAllCount;

    [SerializeField]
    private string lastButtonId = "None";

    [SerializeField]
    private string lastObservedRoute = "None";

    protected override void RegisterMessageHandlers()
    {
        base.RegisterMessageHandlers();
        Token.DiagnosticMode = true;
        _ = Token.RegisterUntargeted<ButtonClicked>(OnButtonClicked);
        _ = Token.RegisterGlobalAcceptAll(OnAnyUntargeted, OnAnyTargeted, OnAnyBroadcast);
    }

    private void OnButtonClicked(ref ButtonClicked message)
    {
        typedClickCount++;
        lastButtonId = message.id;
        if (logTypedClicks)
        {
            Debug.Log($"Button clicked: {message.id}", this);
        }
    }

    private void OnAnyUntargeted(IUntargetedMessage message)
    {
        RecordObservedRoute($"Untargeted {message.MessageType.Name}");
    }

    private void OnAnyTargeted(InstanceId target, ITargetedMessage message)
    {
        RecordObservedRoute($"Targeted {message.MessageType.Name} to {target}");
    }

    private void OnAnyBroadcast(InstanceId source, IBroadcastMessage message)
    {
        RecordObservedRoute($"Broadcast {message.MessageType.Name} from {source}");
    }

    private void RecordObservedRoute(string route)
    {
        acceptAllCount++;
        lastObservedRoute = route;
    }
}
