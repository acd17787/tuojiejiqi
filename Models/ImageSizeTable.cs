using System;
using System.Collections.Generic;
using System.Linq;

namespace AIRenderer.Models
{
    /// <summary>
    /// 出图尺寸表：照抄 APIYI 官逆 -vip 线（gpt-image-2.5-vip）文档的固定档位
    /// （docs.apiyi.com → GPT-Image-2.5-VIP → Supported sizes，10 比例 × 3 档）。
    /// 原型只外露 5 个常用比例，其余 5 个（2:3 / 3:4 / 4:5 / 5:4 / 21:9）按产品决定不展示。
    ///
    /// 这 15 个档位同时满足官方 relay 的自定义尺寸约束：
    /// 两边都是 16 的倍数 / 比例 ≤ 3:1 / 最大边 ≤ 3840 / 总像素 0.65–8.3MP，
    /// 因此开启蒙版自动切到官方模型（支持精确 inpainting）时，同一套尺寸可以原样复用。
    /// </summary>
    public static class ImageSizeTable
    {
        public const string RatioAuto = "auto";
        public const string FastSizeText = "自适应（约 1.5K）";

        public static readonly IReadOnlyList<string> SizeKeys = new[] { "1K", "2K", "4K" };

        /// <summary>外露给用户的比例（不含「原图」）</summary>
        public static readonly IReadOnlyList<string> Ratios = new[] { "1:1", "4:3", "3:2", "16:9", "9:16" };

        private static readonly Dictionary<string, Dictionary<string, (int W, int H)>> Table =
            new Dictionary<string, Dictionary<string, (int, int)>>
            {
                ["1:1"] = new Dictionary<string, (int, int)>
                {
                    ["1K"] = (1280, 1280), ["2K"] = (2048, 2048), ["4K"] = (2880, 2880)
                },
                ["4:3"] = new Dictionary<string, (int, int)>
                {
                    ["1K"] = (1280, 960), ["2K"] = (2048, 1536), ["4K"] = (3312, 2480)
                },
                ["3:2"] = new Dictionary<string, (int, int)>
                {
                    ["1K"] = (1280, 848), ["2K"] = (2048, 1360), ["4K"] = (3520, 2336)
                },
                ["16:9"] = new Dictionary<string, (int, int)>
                {
                    ["1K"] = (1280, 720), ["2K"] = (2048, 1152), ["4K"] = (3840, 2160)
                },
                ["9:16"] = new Dictionary<string, (int, int)>
                {
                    ["1K"] = (720, 1280), ["2K"] = (1152, 2048), ["4K"] = (2160, 3840)
                }
            };

        /// <summary>该档位的真实像素；未知比例/尺寸回落到 1:1 的 1K</summary>
        public static (int W, int H) Pixels(string ratioKey, string sizeKey)
        {
            var ratio = string.IsNullOrWhiteSpace(ratioKey) ? Ratios[0] : ratioKey;
            var size = string.IsNullOrWhiteSpace(sizeKey) ? "1K" : sizeKey;
            if (Table.TryGetValue(ratio, out var bySize) &&
                bySize.TryGetValue(size, out var px))
                return px;
            return Table["1:1"]["1K"];
        }

        public static string PixelsText(string ratioKey, string sizeKey)
        {
            var (w, h) = Pixels(ratioKey, sizeKey);
            return $"{w}×{h}";
        }

        public static string MegapixelsText(string ratioKey, string sizeKey)
        {
            var (w, h) = Pixels(ratioKey, sizeKey);
            return (w * (long)h / 1e6).ToString("0.0") + "MP";
        }

        /// <summary>
        /// 「原图」：按原图长宽比吸附到最接近的外露档位（对数距离，比例才可比）。
        /// </summary>
        public static string SnapRatio(double aspect)
        {
            if (double.IsNaN(aspect) || double.IsInfinity(aspect) || aspect <= 0)
                return Ratios[0];

            var best = Ratios[0];
            var bestDistance = double.MaxValue;
            foreach (var key in Ratios)
            {
                var parts = key.Split(':');
                var target = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture) /
                             double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                var distance = Math.Abs(Math.Log(aspect / target));
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = key;
                }
            }
            return best;
        }

        /// <summary>把设置里的比例（可能是 auto）解析成实际档位</summary>
        public static string Resolve(string ratioKey, double sourceAspect)
            => string.IsNullOrWhiteSpace(ratioKey) || ratioKey == RatioAuto
                ? SnapRatio(sourceAspect)
                : (Ratios.Contains(ratioKey) ? ratioKey : SnapRatio(sourceAspect));

        /// <summary>悬停提示：档位 + 当前尺寸下的像素 + 约束</summary>
        public static string Tooltip(string ratioKey, string sizeKey)
            => $"{ratioKey} · {PixelsText(ratioKey, sizeKey)}（{sizeKey}）· 边长 16 倍数 · 比例 ≤ 3:1";
    }
}
