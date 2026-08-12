using DxMessaging.Core.Attributes;

[DxBroadcastMessage]
[DxAutoConstructor]
public readonly partial struct ButtonClicked
{
    public readonly string id;
}
