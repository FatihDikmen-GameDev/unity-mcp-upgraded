using System;
using System.Collections.Generic;
using System.IO;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Reconstructs a uGUI hierarchy from a reference mockup PNG plus a set of source sprite
    /// PNGs by template-matching each sprite against the reference, then emitting a
    /// <c>manage_canvas</c> layout spec to instantiate it.
    ///
    /// Algorithm: normalized cross-correlation on luminance (shape-only match — tinted instances
    /// still match) with alpha masking (transparent sprite pixels are ignored). Multi-pass:
    /// stride-4 coarse scan, stride-1 refine on top candidates. Non-max suppression detects
    /// multiple instances of the same sprite. Per match, recovers the tint by averaging
    /// reference/sprite pixel ratios.
    ///
    /// v1 limitations: single sprite per region (no compositing detection), 1:1 scale only
    /// (no multi-scale search), no text detection (TMP placeholders left empty for the user).
    /// </summary>
    [McpForUnityTool("match_ui_layout", AutoRegister = false, Group = "ui")]
    public static class MatchUiLayout
    {
        public static object HandleCommand(JObject @params)
        {
            if (@params == null) return new ErrorResponse("Parameters cannot be null.");
            string action = @params["action"]?.ToString()?.ToLowerInvariant();
            try
            {
                switch (action)
                {
                    case "match": return Match(@params);
                    case "build_layout": return BuildLayout(@params);
                    default: return new ErrorResponse($"Unknown action '{action}'. Valid: match, build_layout.");
                }
            }
            catch (Exception e)
            {
                McpLog.Error($"[MatchUiLayout] '{action}' failed: {e}");
                return new ErrorResponse($"Internal error in '{action}': {e.Message}");
            }
        }

        // ---------- match ----------

        private static object Match(JObject @params)
        {
            string refPath = @params["reference_image"]?.ToString();
            if (string.IsNullOrEmpty(refPath))
                return new ErrorResponse("'reference_image' is required (Assets-relative or absolute path).");

            var spritesToken = @params["sprites"] as JArray;
            if (spritesToken == null || spritesToken.Count == 0)
                return new ErrorResponse("'sprites' list is required (list of Assets-relative or absolute sprite paths).");

            float minConfidence = @params["min_confidence"]?.ToObject<float>() ?? 0.85f;
            int maxMatchesPerSprite = @params["max_matches_per_sprite"]?.ToObject<int>() ?? 32;
            int coarseStride = Mathf.Max(1, @params["coarse_stride"]?.ToObject<int>() ?? 4);

            Texture2D reference = LoadReadable(refPath);
            if (reference == null)
                return new ErrorResponse($"Reference image not found or unreadable: '{refPath}'.");

            Color32[] refPixelsTopDown = ToTopDownPixels(reference, out int rw, out int rh);
            byte[] refLum = ToLuminance(refPixelsTopDown);

            var allMatches = new List<object>();
            int spriteIndex = 0;
            foreach (var sToken in spritesToken)
            {
                string sp = sToken.ToString();
                Texture2D sprite = LoadReadable(sp);
                if (sprite == null)
                {
                    McpLog.Warn($"[MatchUiLayout] Sprite not found / unreadable: '{sp}' — skipping.");
                    spriteIndex++;
                    continue;
                }

                if (sprite.width > reference.width || sprite.height > reference.height)
                {
                    McpLog.Warn($"[MatchUiLayout] Sprite '{sp}' larger than reference — skipping.");
                    spriteIndex++;
                    continue;
                }

                Color32[] sprPixelsTopDown = ToTopDownPixels(sprite, out int sw, out int sh);
                byte[] sprLum = ToLuminance(sprPixelsTopDown);
                byte[] sprAlpha = ToAlpha(sprPixelsTopDown);
                // Match signal = alpha-weighted luminance. Captures the SHAPE (via alpha gradient)
                // for monochrome sprites and the INTERIOR DETAIL (via luminance) for textured ones.
                // Tinted instances still match because we correlate against reference luminance,
                // not the source RGB — the tint just scales the absolute luminance, NCC is invariant.
                byte[] sprSignal = MultiplySignals(sprAlpha, sprLum);

                var matches = FindSpriteInImage(
                    refLum, rw, rh, refPixelsTopDown,
                    sprSignal, sw, sh, sprPixelsTopDown, sprAlpha,
                    minConfidence, maxMatchesPerSprite, coarseStride);

                foreach (var m in matches)
                {
                    allMatches.Add(new
                    {
                        sprite = sp,
                        x = m.x,
                        y = m.y,
                        width = sw,
                        height = sh,
                        tint = new[] { m.tint.r, m.tint.g, m.tint.b, m.tint.a },
                        confidence = m.confidence,
                    });
                }
                spriteIndex++;
            }

            return new SuccessResponse(
                $"Found {allMatches.Count} match(es) across {spritesToken.Count} sprite(s).",
                new
                {
                    matches = allMatches,
                    reference_width = rw,
                    reference_height = rh,
                });
        }

        // ---------- build_layout ----------

        private static object BuildLayout(JObject @params)
        {
            var matchesArr = @params["matches"] as JArray;
            if (matchesArr == null || matchesArr.Count == 0)
                return new ErrorResponse("'matches' array is required (output of action='match').");

            int refW = @params["reference_width"]?.ToObject<int>() ?? 1920;
            int refH = @params["reference_height"]?.ToObject<int>() ?? 1080;
            string parent = @params["parent"]?.ToString();
            string rootName = @params["root_name"]?.ToString() ?? "MatchedLayout";

            // Sort by area descending so larger sprites become parents in a future enhancement.
            // For v1 they're all siblings under the root.
            var sorted = new List<JObject>();
            foreach (var t in matchesArr) if (t is JObject jo) sorted.Add(jo);
            sorted.Sort((a, b) =>
            {
                int areaA = (a["width"]?.ToObject<int>() ?? 0) * (a["height"]?.ToObject<int>() ?? 0);
                int areaB = (b["width"]?.ToObject<int>() ?? 0) * (b["height"]?.ToObject<int>() ?? 0);
                return areaB.CompareTo(areaA);
            });

            var children = new JArray();
            int idx = 0;
            foreach (var m in sorted)
            {
                float x = m["x"].ToObject<float>();
                float y = m["y"].ToObject<float>();
                float w = m["width"].ToObject<float>();
                float h = m["height"].ToObject<float>();
                string spritePath = m["sprite"]?.ToString();
                var tint = m["tint"] as JArray;

                // Reference pixel coords (origin top-left, Y down) -> canvas anchored coords
                // (origin center, Y up) with reference size as the canvas's reference resolution.
                float anchoredX = (x + w * 0.5f) - refW * 0.5f;
                float anchoredY = -((y + h * 0.5f) - refH * 0.5f);

                children.Add(new JObject
                {
                    ["name"] = $"el_{idx:D2}_{System.IO.Path.GetFileNameWithoutExtension(spritePath)}",
                    ["rect"] = new JObject
                    {
                        ["anchor"] = "center",
                        ["anchored_position"] = new JArray(anchoredX, anchoredY),
                        ["size_delta"] = new JArray(w, h),
                    },
                    ["image"] = new JObject
                    {
                        ["sprite"] = spritePath,
                        ["color"] = tint ?? new JArray(1, 1, 1, 1),
                        ["type"] = "Simple",
                    },
                });
                idx++;
            }

            var layout = new JObject
            {
                ["name"] = rootName,
                ["rect"] = new JObject
                {
                    ["anchor"] = "stretch",
                    ["anchor_min"] = new JArray(0, 0),
                    ["anchor_max"] = new JArray(1, 1),
                    ["pivot"] = new JArray(0.5, 0.5),
                    ["anchored_position"] = new JArray(0, 0),
                    ["size_delta"] = new JArray(0, 0),
                },
                ["children"] = children,
            };

            var canvasParams = new JObject
            {
                ["action"] = "build",
                ["layout"] = layout,
            };
            if (!string.IsNullOrEmpty(parent)) canvasParams["parent"] = parent;
            return ManageCanvas.HandleCommand(canvasParams);
        }

        // ---------- core algorithm ----------

        private struct MatchResult
        {
            public int x, y;
            public Color tint;
            public float confidence;
        }

        private static List<MatchResult> FindSpriteInImage(
            byte[] refLum, int rw, int rh, Color32[] refPixels,
            byte[] sprSignal, int sw, int sh, Color32[] sprPixels, byte[] sprAlpha,
            float minConfidence, int maxMatches, int coarseStride)
        {
            // Precompute sprite signal statistics over the FULL bounding box (no alpha gating —
            // transparent pixels naturally contribute 0 to the alpha-weighted signal, which is
            // what we want to correlate against the reference's local background).
            int n = sprSignal.Length;
            double sprSum = 0;
            for (int i = 0; i < n; i++) sprSum += sprSignal[i];
            double sprMean = sprSum / n;
            double sprVar = 0;
            for (int i = 0; i < n; i++) { double d = sprSignal[i] - sprMean; sprVar += d * d; }
            double sprNorm = Math.Sqrt(sprVar);
            if (sprNorm < 1e-6) return new List<MatchResult>();

            int maxX = rw - sw;
            int maxY = rh - sh;

            // Pass 1: coarse stride scan
            var candidates = new List<(int x, int y, float score)>();
            for (int y = 0; y <= maxY; y += coarseStride)
            {
                for (int x = 0; x <= maxX; x += coarseStride)
                {
                    float score = (float)NccAt(refLum, rw, x, y, sprSignal, sw, sh, sprMean, sprNorm);
                    if (score >= minConfidence * 0.92f)
                    {
                        candidates.Add((x, y, score));
                    }
                }
            }

            // Pass 2: refine each candidate at stride 1 within ±coarseStride
            var refined = new List<MatchResult>();
            foreach (var c in candidates)
            {
                int x0 = Math.Max(0, c.x - coarseStride);
                int y0 = Math.Max(0, c.y - coarseStride);
                int x1 = Math.Min(maxX, c.x + coarseStride);
                int y1 = Math.Min(maxY, c.y + coarseStride);
                int bestX = c.x, bestY = c.y;
                float bestScore = -2f;
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        float s = (float)NccAt(refLum, rw, x, y, sprSignal, sw, sh, sprMean, sprNorm);
                        if (s > bestScore) { bestScore = s; bestX = x; bestY = y; }
                    }
                }
                if (bestScore >= minConfidence)
                    refined.Add(new MatchResult { x = bestX, y = bestY, confidence = bestScore });
            }

            // Non-max suppression: keep best score per overlapping region
            refined.Sort((a, b) => b.confidence.CompareTo(a.confidence));
            var kept = new List<MatchResult>();
            int minSeparation = Math.Min(sw, sh) / 2;
            foreach (var m in refined)
            {
                bool tooClose = false;
                foreach (var k in kept)
                {
                    if (Math.Abs(m.x - k.x) < minSeparation && Math.Abs(m.y - k.y) < minSeparation)
                    {
                        tooClose = true;
                        break;
                    }
                }
                if (!tooClose)
                {
                    kept.Add(m);
                    if (kept.Count >= maxMatches) break;
                }
            }

            // Recover tint per kept match (still uses alpha so we only average opaque pixels)
            for (int i = 0; i < kept.Count; i++)
            {
                var m = kept[i];
                m.tint = RecoverTint(refPixels, rw, m.x, m.y, sprPixels, sw, sh, sprAlpha);
                kept[i] = m;
            }

            return kept;
        }

        private static double NccAt(
            byte[] refLum, int rw, int rx, int ry,
            byte[] sprSignal, int sw, int sh,
            double sprMean, double sprNorm)
        {
            int n = sw * sh;
            // First pass: reference patch mean over the FULL sprite bounding box
            double refSum = 0;
            for (int y = 0; y < sh; y++)
            {
                int refOff = (ry + y) * rw + rx;
                for (int x = 0; x < sw; x++) refSum += refLum[refOff + x];
            }
            double refMean = refSum / n;

            // Second pass: numerator + reference variance
            double num = 0, refVar = 0;
            for (int y = 0; y < sh; y++)
            {
                int refOff = (ry + y) * rw + rx;
                int sprOff = y * sw;
                for (int x = 0; x < sw; x++)
                {
                    double r = refLum[refOff + x] - refMean;
                    double s = sprSignal[sprOff + x] - sprMean;
                    num += r * s;
                    refVar += r * r;
                }
            }
            double refNorm = Math.Sqrt(refVar);
            if (refNorm < 1e-6) return 0;
            return num / (refNorm * sprNorm);
        }

        private static Color RecoverTint(
            Color32[] refPixels, int rw, int rx, int ry,
            Color32[] sprPixels, int sw, int sh, byte[] sprAlpha)
        {
            // Average ratio of reference / sprite for R, G, B over opaque sprite pixels.
            double rSum = 0, gSum = 0, bSum = 0;
            int n = 0;
            for (int y = 0; y < sh; y++)
            {
                int sprRow = y * sw;
                int refRow = (ry + y) * rw + rx;
                for (int x = 0; x < sw; x++)
                {
                    if (sprAlpha[sprRow + x] < 200) continue; // require fully-opaque
                    Color32 sCol = sprPixels[sprRow + x];
                    Color32 rCol = refPixels[refRow + x];
                    if (sCol.r < 8 && sCol.g < 8 && sCol.b < 8) continue;
                    rSum += sCol.r > 0 ? (double)rCol.r / sCol.r : 1.0;
                    gSum += sCol.g > 0 ? (double)rCol.g / sCol.g : 1.0;
                    bSum += sCol.b > 0 ? (double)rCol.b / sCol.b : 1.0;
                    n++;
                }
            }
            if (n == 0) return Color.white;
            float rr = Mathf.Clamp01((float)(rSum / n));
            float gg = Mathf.Clamp01((float)(gSum / n));
            float bb = Mathf.Clamp01((float)(bSum / n));
            return new Color(rr, gg, bb, 1f);
        }

        // ---------- helpers ----------

        /// <summary>
        /// Convert a Unity Texture2D (stored bottom-up) into a flat Color32[] in TOP-DOWN order
        /// so subsequent index math matches screen-pixel conventions (y=0 at top).
        /// </summary>
        private static Color32[] ToTopDownPixels(Texture2D tex, out int w, out int h)
        {
            w = tex.width; h = tex.height;
            var src = tex.GetPixels32();
            var dst = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                int srcRow = (h - 1 - y) * w;
                int dstRow = y * w;
                Array.Copy(src, srcRow, dst, dstRow, w);
            }
            return dst;
        }

        private static byte[] ToLuminance(Color32[] pxTopDown)
        {
            var lum = new byte[pxTopDown.Length];
            for (int i = 0; i < pxTopDown.Length; i++)
            {
                Color32 c = pxTopDown[i];
                lum[i] = (byte)(0.299f * c.r + 0.587f * c.g + 0.114f * c.b);
            }
            return lum;
        }

        private static byte[] ToAlpha(Color32[] pxTopDown)
        {
            var a = new byte[pxTopDown.Length];
            for (int i = 0; i < pxTopDown.Length; i++) a[i] = pxTopDown[i].a;
            return a;
        }

        /// <summary>
        /// Alpha-weighted luminance: signal[i] = (alpha[i] / 255) * lum[i]. This encodes both
        /// the sprite shape (via alpha) and any internal luminance variation (via lum). Tinted
        /// reference instances correlate cleanly via NCC because the SHAPE pattern is preserved
        /// regardless of tint color.
        /// </summary>
        private static byte[] MultiplySignals(byte[] alpha, byte[] lum)
        {
            var s = new byte[alpha.Length];
            for (int i = 0; i < alpha.Length; i++) s[i] = (byte)((alpha[i] * lum[i] + 127) / 255);
            return s;
        }

        private static Texture2D LoadReadable(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string assetPath = path;
            if (System.IO.Path.IsPathRooted(path))
            {
                string projRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "..")).Replace('\\', '/');
                string normalized = System.IO.Path.GetFullPath(path).Replace('\\', '/');
                if (normalized.StartsWith(projRoot)) assetPath = normalized.Substring(projRoot.Length).TrimStart('/');
            }

            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (tex != null)
            {
                EnsureAnalysisReady(assetPath);
                tex = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                return tex;
            }

            // Fall back to loading from disk (rare — when the file isn't an imported asset)
            string fullPath = System.IO.Path.IsPathRooted(path)
                ? path
                : System.IO.Path.Combine(Application.dataPath, "..", path);
            if (!System.IO.File.Exists(fullPath)) return null;
            byte[] bytes = System.IO.File.ReadAllBytes(fullPath);
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            t.LoadImage(bytes);
            return t;
        }

        /// <summary>
        /// Set TextureImporter settings so GetPixels32() returns pixel-exact source data:
        /// readable, no NPOT rescale, no compression, no mipmaps. Only re-saves if any setting
        /// was wrong — important for performance because SaveAndReimport is expensive.
        /// </summary>
        private static void EnsureAnalysisReady(string assetPath)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null) return;

            bool dirty = false;
            if (!importer.isReadable) { importer.isReadable = true; dirty = true; }
            if (importer.npotScale != TextureImporterNPOTScale.None) { importer.npotScale = TextureImporterNPOTScale.None; dirty = true; }
            if (importer.textureCompression != TextureImporterCompression.Uncompressed) { importer.textureCompression = TextureImporterCompression.Uncompressed; dirty = true; }
            if (importer.mipmapEnabled) { importer.mipmapEnabled = false; dirty = true; }
            if (dirty) importer.SaveAndReimport();
        }
    }
}
