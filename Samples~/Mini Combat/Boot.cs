using DxMessaging.Core.Extensions;
using UnityEngine;

public sealed class Boot : MonoBehaviour
{
    [SerializeField]
    private Player player;

    [SerializeField]
    private Enemy enemy;

    [SerializeField]
    private UIOverlay uiOverlay;

    private GameObject createdPlayer;
    private GameObject createdEnemy;
    private GameObject createdUiOverlay;

    private void Awake()
    {
        player = GetOrCreate(player, "Player", out createdPlayer);
        enemy = GetOrCreate(enemy, "Enemy", out createdEnemy);
        uiOverlay = GetOrCreate(uiOverlay, "UI Overlay", out createdUiOverlay);
    }

    private void Start()
    {
        var settings = new VideoSettingsChanged(1920, 1080);
        settings.Emit();

        var heal = new Heal(10);
        heal.EmitComponentTargeted(player);

        enemy.ApplyDamage(5);
    }

    private void OnDestroy()
    {
        DestroyOwned(createdPlayer);
        DestroyOwned(createdEnemy);
        DestroyOwned(createdUiOverlay);
        createdPlayer = null;
        createdEnemy = null;
        createdUiOverlay = null;
    }

    private static T GetOrCreate<T>(T component, string objectName, out GameObject created)
        where T : Component
    {
        created = null;
        if (component != null)
        {
            return component;
        }

        created = new GameObject(objectName);
        return created.AddComponent<T>();
    }

    private static void DestroyOwned(GameObject ownedObject)
    {
        if (ownedObject == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            Destroy(ownedObject);
        }
        else
        {
            DestroyImmediate(ownedObject);
        }
    }
}
