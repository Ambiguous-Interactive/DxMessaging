using DxMessaging.Core;
using DxMessaging.Core.Messages;
using DxMessaging.Unity;
using UnityEngine;

public sealed class UIOverlay : MessageAwareComponent
{
    [SerializeField]
    private Rect overlayRect = new Rect(24f, 24f, 320f, 92f);

    [SerializeField]
    private string resolutionText = "Resolution: waiting for settings";

    [SerializeField]
    private string damageText = "Damage: no events observed";

    [SerializeField]
    private long totalDamageObserved;

    protected override void RegisterMessageHandlers()
    {
        base.RegisterMessageHandlers();
        _ = Token.RegisterUntargeted<VideoSettingsChanged>(OnSettingsChanged);
        _ = Token.RegisterBroadcastWithoutSource<TookDamage>(OnAnyDamage);
    }

    private void OnGUI()
    {
        GUI.Box(overlayRect, "Combat Feed");
        GUI.Label(
            new Rect(overlayRect.x + 12f, overlayRect.y + 24f, overlayRect.width - 24f, 24f),
            resolutionText
        );
        GUI.Label(
            new Rect(overlayRect.x + 12f, overlayRect.y + 52f, overlayRect.width - 24f, 24f),
            damageText
        );
    }

    private void OnSettingsChanged(ref VideoSettingsChanged message)
    {
        resolutionText = $"Resolution: {message.width} x {message.height}";
    }

    private void OnAnyDamage(ref InstanceId source, ref TookDamage message)
    {
        int damage = Mathf.Max(0, message.amount);
        totalDamageObserved += damage;
        damageText = $"Damage: {source} dealt {damage} (total {totalDamageObserved})";
    }
}
