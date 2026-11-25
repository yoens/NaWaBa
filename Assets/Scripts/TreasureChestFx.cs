using UnityEngine;

// "Treasure found" effect on a chest: the chest hops, gold coins burst out of it and float
// around it spinning, a warm light comes up. Used by the last game of a region (stage 4's exit
// chest, the mirror cave's treasure). The coins are children placed by the builder (their
// placed position is where they float); they stay hidden until Open().
public class TreasureChestFx : MonoBehaviour
{
    [SerializeField] private Transform chest;
    [SerializeField] private Transform[] coins = new Transform[0];
    [SerializeField] private Light glow;
    [SerializeField, Min(0.1f)] private float burstTime = 0.7f;
    [SerializeField] private float glowIntensity = 4f;
    [SerializeField] private float coinSpin = 240f;

    private Vector3 chestScale;
    private Vector3[] coinHome;
    private float t = -1f;

    public bool IsOpen => t >= 0f;

    public void Configure(Transform chestModel, Transform[] coinModels, Light light)
    {
        chest = chestModel;
        coins = coinModels;
        glow = light;
    }

    private void Awake()
    {
        if (chest != null) chestScale = chest.localScale;
        coinHome = new Vector3[coins.Length];
        for (int i = 0; i < coins.Length; i++)
        {
            if (coins[i] == null) continue;
            coinHome[i] = coins[i].localPosition;
            coins[i].gameObject.SetActive(false);
        }
        if (glow != null)
        {
            glow.intensity = 0f;
            glow.enabled = false;
        }
    }

    public void Open()
    {
        if (IsOpen) return;
        t = 0f;
        foreach (Transform coin in coins) if (coin != null) coin.gameObject.SetActive(true);
        if (glow != null) glow.enabled = true;
        Place(0f);
    }

    private void Update()
    {
        if (t < 0f) return;
        t += Time.deltaTime;
        Place(t);
    }

    private void Place(float time)
    {
        float k = Mathf.Clamp01(time / burstTime);
        // The chest hops twice and settles.
        if (chest != null)
        {
            float hop = time < 1.2f ? Mathf.Abs(Mathf.Sin(time * Mathf.PI / 0.6f)) * (1f - time / 1.2f) : 0f;
            chest.localScale = chestScale * (1f + hop * 0.22f);
        }
        if (glow != null) glow.intensity = glowIntensity * (k + 0.15f * Mathf.Sin(time * 4f) * k);

        // Coins fly from the chest lid to their place on an arc, then bob and spin.
        Vector3 lid = Vector3.up * 0.8f;
        for (int i = 0; i < coins.Length; i++)
        {
            if (coins[i] == null) continue;
            Vector3 home = coinHome[i];
            float delay = (i % 5) * 0.06f;
            float c = Mathf.Clamp01((time - delay) / burstTime);
            float e = 1f - (1f - c) * (1f - c);
            Vector3 p = Vector3.Lerp(lid, home, e) + Vector3.up * (Mathf.Sin(c * Mathf.PI) * 1.2f);
            if (c >= 1f) p = home + Vector3.up * (Mathf.Sin(time * 2.2f + i) * 0.15f);
            coins[i].localPosition = p;
            coins[i].Rotate(0f, coinSpin * Time.deltaTime, 0f, Space.World);
        }
    }
}
