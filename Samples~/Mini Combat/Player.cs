using DxMessaging.Core.Messages;
using DxMessaging.Unity;
using UnityEngine;

public sealed class Player : MessageAwareComponent
{
    private int _hp = 100;

    protected override void RegisterMessageHandlers()
    {
        base.RegisterMessageHandlers();
        _ = Token.RegisterComponentTargeted<Heal>(this, OnHeal);
    }

    private void OnHeal(in Heal m)
    {
        _hp += m.amount;
        Debug.Log($"Player healed: +{m.amount}, HP={_hp}");
    }
}
