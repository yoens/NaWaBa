using System.Collections.Generic;
using UnityEngine;

// Materials for objects made from code (snowballs, goal / checkpoint flags ...).
// GameObject.CreatePrimitive gives Unity's built-in default material, which a URP *build*
// draws pink (it works in the editor only). These copies come from a URP Lit material in
// Resources, so the shader is always included in the build.
public static class RuntimeMaterials
{
    private const string TemplatePath = "NaWaBaRuntimeLit";
    private static Material template;
    private static readonly Dictionary<Color, Material> cache = new Dictionary<Color, Material>();

    public static Material Template
    {
        get
        {
            if (template == null) template = Resources.Load<Material>(TemplatePath);
            if (template == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                template = new Material(shader) { name = "RuntimeLit (fallback)" };
                Debug.LogWarning("RuntimeMaterials: Resources/NaWaBaRuntimeLit.mat is missing; using Shader.Find.");
            }
            return template;
        }
    }

    // A shared material of this colour (do not change it afterwards; ask for another colour).
    public static Material Lit(Color color)
    {
        if (cache.TryGetValue(color, out Material material) && material != null) return material;
        material = new Material(Template) { name = "Runtime_" + ColorUtility.ToHtmlStringRGB(color) };
        material.color = color;
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        cache[color] = material;
        return material;
    }

    // Give a primitive (or any renderer) the URP material of this colour.
    public static void Paint(Renderer renderer, Color color)
    {
        if (renderer != null) renderer.sharedMaterial = Lit(color);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        template = null;
        cache.Clear();
    }
}
