using System;
using System.Collections.Generic;
using System.Text;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Builds runtime uGUI hierarchies (Canvas / RectTransform / Image / TextMeshProUGUI) from a declarative spec.
    /// Distinct from <see cref="ManageUI"/>, which targets UI Toolkit (UXML/USS/VisualElement).
    /// </summary>
    [McpForUnityTool("manage_canvas", AutoRegister = false, Group = "ui")]
    public static class ManageCanvas
    {
        private static readonly Dictionary<string, (Vector2 min, Vector2 max, Vector2 pivot)> AnchorPresets = new(StringComparer.OrdinalIgnoreCase)
        {
            { "top-left",        (new Vector2(0, 1),    new Vector2(0, 1),    new Vector2(0, 1)) },
            { "top-center",      (new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 1)) },
            { "top-right",       (new Vector2(1, 1),    new Vector2(1, 1),    new Vector2(1, 1)) },
            { "middle-left",     (new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0.5f)) },
            { "middle-center",   (new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f)) },
            { "center",          (new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f)) },
            { "middle-right",    (new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(1, 0.5f)) },
            { "bottom-left",     (new Vector2(0, 0),    new Vector2(0, 0),    new Vector2(0, 0)) },
            { "bottom-center",   (new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0)) },
            { "bottom-right",    (new Vector2(1, 0),    new Vector2(1, 0),    new Vector2(1, 0)) },
            { "stretch-top",     (new Vector2(0, 1),    new Vector2(1, 1),    new Vector2(0.5f, 1)) },
            { "stretch-bottom",  (new Vector2(0, 0),    new Vector2(1, 0),    new Vector2(0.5f, 0)) },
            { "stretch-left",    (new Vector2(0, 0),    new Vector2(0, 1),    new Vector2(0, 0.5f)) },
            { "stretch-right",   (new Vector2(1, 0),    new Vector2(1, 1),    new Vector2(1, 0.5f)) },
            { "stretch-horizontal", (new Vector2(0, 0.5f), new Vector2(1, 0.5f), new Vector2(0.5f, 0.5f)) },
            { "stretch-vertical",   (new Vector2(0.5f, 0), new Vector2(0.5f, 1), new Vector2(0.5f, 0.5f)) },
            { "stretch",         (new Vector2(0, 0),    new Vector2(1, 1),    new Vector2(0.5f, 0.5f)) },
            { "stretch-all",     (new Vector2(0, 0),    new Vector2(1, 1),    new Vector2(0.5f, 0.5f)) },
        };

        private static Type s_tmpType;
        private static Type TmpTextType => s_tmpType ??= Type.GetType("TMPro.TextMeshProUGUI, Unity.TextMeshPro");

        public static object HandleCommand(JObject @params)
        {
            if (@params == null)
                return new ErrorResponse("Parameters cannot be null.");

            string action = @params["action"]?.ToString()?.ToLowerInvariant();
            if (action != "build")
                return new ErrorResponse($"Unknown action: '{action}'. Valid actions: build.");

            JObject layout = @params["layout"] as JObject;
            if (layout == null)
                return new ErrorResponse("'layout' is required (object describing the hierarchy).");

            string parentRef = @params["parent"]?.ToString() ?? layout["parent"]?.ToString();
            GameObject parentGO = ResolveParent(parentRef, out string parentError);
            if (parentError != null)
                return new ErrorResponse(parentError);

            try
            {
                var nodes = new List<object>();
                GameObject root = BuildNode(layout, parentGO?.transform, nodes);
                Undo.RegisterCreatedObjectUndo(root, "Build UI Layout");
                Selection.activeGameObject = root;
                EditorUtility.SetDirty(root);

                return new SuccessResponse(
                    $"Built layout '{root.name}' with {nodes.Count} node(s).",
                    new
                    {
                        root = new
                        {
                            name = root.name,
                            instanceID = root.GetInstanceID(),
                            path = GetGameObjectPath(root)
                        },
                        nodes
                    });
            }
            catch (Exception e)
            {
                McpLog.Error($"[ManageCanvas] build failed: {e}");
                return new ErrorResponse($"Failed to build layout: {e.Message}");
            }
        }

        private static GameObject BuildNode(JObject spec, Transform parent, List<object> registry)
        {
            string name = spec["name"]?.ToString() ?? "UI Element";

            var go = new GameObject(name, typeof(RectTransform));
            if (parent != null)
                go.transform.SetParent(parent, false);

            ConfigureRectTransform(go.GetComponent<RectTransform>(), spec["rect"] as JObject);

            if (spec["image"] is JObject imageSpec)
                ConfigureImage(go, imageSpec);

            if (spec["text"] is JObject textSpec)
                ConfigureText(go, textSpec);

            registry.Add(new
            {
                name = go.name,
                instanceID = go.GetInstanceID(),
                path = GetGameObjectPath(go)
            });

            if (spec["children"] is JArray children)
            {
                foreach (var child in children)
                {
                    if (child is JObject childSpec)
                        BuildNode(childSpec, go.transform, registry);
                }
            }

            return go;
        }

        private static void ConfigureRectTransform(RectTransform rt, JObject spec)
        {
            if (spec == null) return;

            string anchor = spec["anchor"]?.ToString();
            if (!string.IsNullOrEmpty(anchor) && AnchorPresets.TryGetValue(anchor, out var preset))
            {
                rt.anchorMin = preset.min;
                rt.anchorMax = preset.max;
                rt.pivot = preset.pivot;
            }

            if (TryReadVector2(spec["anchor_min"], out var aMin)) rt.anchorMin = aMin;
            if (TryReadVector2(spec["anchor_max"], out var aMax)) rt.anchorMax = aMax;
            if (TryReadVector2(spec["pivot"], out var pv)) rt.pivot = pv;

            if (TryReadVector2(spec["anchored_position"], out var ap)) rt.anchoredPosition = ap;
            if (TryReadVector2(spec["size_delta"], out var sd)) rt.sizeDelta = sd;

            if (spec["rotation"] != null)
            {
                try { rt.localEulerAngles = new Vector3(0, 0, spec["rotation"].ToObject<float>()); }
                catch { }
            }

            if (TryReadVector2(spec["scale"], out var sc))
                rt.localScale = new Vector3(sc.x, sc.y, 1f);
        }

        private static void ConfigureImage(GameObject go, JObject spec)
        {
            var img = go.GetComponent<Image>() ?? go.AddComponent<Image>();

            string spritePath = spec["sprite"]?.ToString();
            if (!string.IsNullOrEmpty(spritePath))
            {
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(spritePath);
                if (sprite != null) img.sprite = sprite;
                else McpLog.Warn($"[ManageCanvas] Sprite not found at '{spritePath}' (on '{go.name}').");
            }

            if (TryReadColor(spec["color"], out var color)) img.color = color;

            string type = spec["type"]?.ToString();
            if (!string.IsNullOrEmpty(type) && Enum.TryParse<Image.Type>(type, true, out var imgType))
                img.type = imgType;

            if (spec["raycast_target"] != null)
            {
                try { img.raycastTarget = spec["raycast_target"].ToObject<bool>(); } catch { }
            }
            if (spec["preserve_aspect"] != null)
            {
                try { img.preserveAspect = spec["preserve_aspect"].ToObject<bool>(); } catch { }
            }
        }

        private static void ConfigureText(GameObject go, JObject spec)
        {
            var tmpType = TmpTextType;
            if (tmpType == null)
            {
                McpLog.Warn($"[ManageCanvas] TextMeshPro (Unity.TextMeshPro) not available — text node '{go.name}' skipped.");
                return;
            }

            var tmp = go.GetComponent(tmpType);
            if (tmp == null) tmp = go.AddComponent(tmpType);

            SetReflectProp(tmp, "text", spec["text"]?.ToString());
            if (spec["font_size"] != null)
            {
                try { SetReflectProp(tmp, "fontSize", spec["font_size"].ToObject<float>()); } catch { }
            }
            if (TryReadColor(spec["color"], out var color)) SetReflectProp(tmp, "color", color);

            string alignment = spec["alignment"]?.ToString();
            if (!string.IsNullOrEmpty(alignment))
            {
                var alignType = Type.GetType("TMPro.TextAlignmentOptions, Unity.TextMeshPro");
                if (alignType != null)
                {
                    try { SetReflectProp(tmp, "alignment", Enum.Parse(alignType, alignment, true)); } catch { }
                }
            }

            string fontStyle = spec["font_style"]?.ToString();
            if (!string.IsNullOrEmpty(fontStyle))
            {
                var styleType = Type.GetType("TMPro.FontStyles, Unity.TextMeshPro");
                if (styleType != null)
                {
                    try { SetReflectProp(tmp, "fontStyle", Enum.Parse(styleType, fontStyle, true)); } catch { }
                }
            }

            string fontAsset = spec["font_asset"]?.ToString();
            if (!string.IsNullOrEmpty(fontAsset))
            {
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(fontAsset);
                if (asset != null) SetReflectProp(tmp, "font", asset);
            }

            if (spec["raycast_target"] != null)
            {
                try { SetReflectProp(tmp, "raycastTarget", spec["raycast_target"].ToObject<bool>()); } catch { }
            }
        }

        private static GameObject ResolveParent(string parentRef, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(parentRef)) return null;

            if (int.TryParse(parentRef, out int id))
            {
                var obj = EditorUtility.InstanceIDToObject(id) as GameObject;
                if (obj == null) error = $"No GameObject with instance ID {id}.";
                return obj;
            }

            var found = GameObject.Find(parentRef);
            if (found == null) error = $"Parent GameObject not found: '{parentRef}'.";
            return found;
        }

        private static string GetGameObjectPath(GameObject go)
        {
            if (go == null) return null;
            var sb = new StringBuilder(go.name);
            var t = go.transform.parent;
            while (t != null)
            {
                sb.Insert(0, t.name + "/");
                t = t.parent;
            }
            return sb.ToString();
        }

        private static bool TryReadVector2(JToken token, out Vector2 v)
        {
            v = Vector2.zero;
            if (token is JArray arr && arr.Count >= 2)
            {
                try
                {
                    v = new Vector2(arr[0].ToObject<float>(), arr[1].ToObject<float>());
                    return true;
                }
                catch { }
            }
            return false;
        }

        private static bool TryReadColor(JToken token, out Color c)
        {
            c = Color.white;
            if (token is JArray arr && arr.Count >= 3)
            {
                try
                {
                    float a = arr.Count >= 4 ? arr[3].ToObject<float>() : 1f;
                    c = new Color(arr[0].ToObject<float>(), arr[1].ToObject<float>(), arr[2].ToObject<float>(), a);
                    return true;
                }
                catch { }
            }
            return false;
        }

        private static void SetReflectProp(object obj, string propName, object value)
        {
            if (obj == null || value == null) return;
            var prop = obj.GetType().GetProperty(propName);
            if (prop == null || !prop.CanWrite) return;
            try { prop.SetValue(obj, value); }
            catch { /* type mismatch — ignore so partial spec still applies */ }
        }
    }
}
