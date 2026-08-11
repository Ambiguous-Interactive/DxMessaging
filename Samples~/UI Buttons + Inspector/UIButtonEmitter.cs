using DxMessaging.Core.Extensions;
using DxMessaging.Core.Messages;
using UnityEngine;

public sealed class UIButtonEmitter : MonoBehaviour
{
    private const string FallbackButtonId = "UnnamedButton";

    [SerializeField]
    private string buttonId = "ButtonA";

    [SerializeField]
    private bool showDemoButton = true;

    [SerializeField]
    private Rect demoButtonRect = new Rect(24, 24, 180, 44);

    private void OnGUI()
    {
        if (showDemoButton && GUI.Button(demoButtonRect, $"Emit {EffectiveButtonId}"))
        {
            Click();
        }
    }

    // A project using Unity UI can also bind this method to Button.onClick.
    public void Click()
    {
        string effectiveButtonId = EffectiveButtonId;
        var evt = new ButtonClicked(effectiveButtonId);
        evt.EmitGameObjectBroadcast(gameObject);

        // Also emit a targeted string message to this GameObject.
        var text = new StringMessage($"Clicked {effectiveButtonId}");
        text.EmitGameObjectTargeted(gameObject);
    }

    private string EffectiveButtonId =>
        string.IsNullOrWhiteSpace(buttonId) ? FallbackButtonId : buttonId;
}
