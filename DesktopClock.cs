using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
// 避免和 System.IO.Path 冲突：把"Path"显式指向 WPF 的形状类
using Path = System.Windows.Shapes.Path;

namespace DesktopClock {

    // ------------------------------------------------------------ 天气获取
    // 全自动：用 wttr.in 单接口即可拿到 IP 定位 + 城市 + 温度 + 状态（j1 格式 JSON）。
    // 注意：wttr.in 对 User-Agent 敏感——浏览器 UA 才返回 JSON，否则返回 HTML。
    // WebClient 默认 UA 是 MSIE 7.0，必须显式设成 Chrome UA。
    internal static class Weather {
        public static string City = "";      // 城市（尽量中文）
        public static string Temp = "-";     // 温度
        public static string Status = "-";   // 英文状态（wttr.in 原始，用于图标匹配）
        public static string StatusCn = "-"; // 中文状态（用于显示）
        public static string Icon = "";      // 图标字符
        public static bool Loaded = false;   // 是否成功拉过至少一次
        public static string LastError = ""; // 最近一次拉取错误（空=没出错）

        public static void FetchAsync(Action onDone, string url, string cityName = null) {
            var th = new Thread(() => {
                // diag：全链路诊断。每步失败原因都记录，最终一次性显示，
                // 避免"静默吞掉后只见最后一个错"导致无法定位是哪一环挂了
                string diag = "";
                try {
                    // 1) 自动定位时，调 ip-api.com（直连）拿中文城市 + 经纬度
                    //    **IP 定位必须直连**：走代理拿到的是代理出口 IP，城市会定位错
                    string cnCity = "";
                    double lat = double.NaN, lon = double.NaN;
                    if (string.IsNullOrEmpty(cityName)) {
                        try {
                            string ipJson = Download("http://ip-api.com/json/?lang=zh-CN&fields=status,city,lat,lon", false);
                            var mCN = Regex.Match(ipJson, "\"city\"\\s*:\\s*\"([^\"]+)\"");
                            cnCity = mCN.Success ? mCN.Groups[1].Value : "";
                            var mLat = Regex.Match(ipJson, "\"lat\"\\s*:\\s*([\\-0-9.]+)");
                            var mLon = Regex.Match(ipJson, "\"lon\"\\s*:\\s*([\\-0-9.]+)");
                            if (mLat.Success) lat = double.Parse(mLat.Groups[1].Value, CultureInfo.InvariantCulture);
                            if (mLon.Success) lon = double.Parse(mLon.Groups[1].Value, CultureInfo.InvariantCulture);
                        } catch (Exception exIp) {
                            diag += "[IP定位失败: " + Msg(exIp) + "]";
                        }
                        if (double.IsNaN(lat) || double.IsNaN(lon)) {
                            diag += "[无经纬度,跳过OpenMeteo]";
                        }
                    }

                    // 2) 主力：Open-Meteo（稳定，wttr.in 经常 500 弃用为兜底）
                    //    手动城市 → 先地理编码拿经纬度；自动定位 → 直接用 ip-api 的经纬度
                    bool ok = false;
                    try {
                        if (string.IsNullOrEmpty(cityName)) {
                            if (!double.IsNaN(lat) && !double.IsNaN(lon)) ok = FetchOpenMeteo(lat, lon, cnCity);
                        } else {
                            ok = FetchOpenMeteoCity(cityName);
                        }
                    } catch (Exception exOm) {
                        diag += "[OpenMeteo失败: " + Msg(exOm) + "]";
                    }

                    // 3) 兜底：老路 wttr.in（走系统代理，500 时自动重试 1 次）
                    if (!ok) {
                        try {
                            FetchWttrIn(url, cityName, cnCity);
                        } catch (Exception exW) {
                            LastError = "[v2]" + diag + "wttr.in: " + Msg(exW);
                            if (onDone != null) onDone();
                            return;
                        }
                    }

                    if (Loaded) LastError = "";
                } catch (Exception ex) {
                    // 意外异常也解开 AggregateException（Task.Wait 包内部异常），C# 5 兼容写法
                    LastError = Msg(ex);
                }
                if (onDone != null) onDone();
            });
            th.IsBackground = true;
            th.Start();
        }

        // 取真实错误消息：解开 Task.Wait 包的 AggregateException（C# 5 兼容，不用 is X var）
        static string Msg(Exception e) {
            AggregateException a = e as AggregateException;
            Exception r = (a != null && a.InnerException != null) ? a.InnerException : e;
            return r.Message;
        }

        // Open-Meteo 当前天气（按经纬度）。先直连，失败再走系统代理
        static bool FetchOpenMeteo(double lat, double lon, string displayCity) {
            string url = "https://api.open-meteo.com/v1/forecast?latitude=" +
                lat.ToString("0.####", CultureInfo.InvariantCulture) +
                "&longitude=" + lon.ToString("0.####", CultureInfo.InvariantCulture) +
                "&current_weather=true";
            string json = null;
            try { json = Download(url, false); } catch { }
            if (json == null) json = Download(url, true);   // 直连不行再走代理，再失败抛给上层

            // current_weather: { "temperature": 25.3, ..., "weathercode": 2, ... }
            var mT = Regex.Match(json, "\"temperature\"\\s*:\\s*([\\-0-9.]+)");
            var mC = Regex.Match(json, "\"weathercode\"\\s*:\\s*(\\d+)");
            if (!mT.Success) throw new Exception("Open-Meteo 返回缺少 temperature");
            Temp = Math.Round(double.Parse(mT.Groups[1].Value, CultureInfo.InvariantCulture))
                       .ToString(CultureInfo.InvariantCulture);
            int code = mC.Success ? int.Parse(mC.Groups[1].Value) : -1;
            Status = WmoToStatus(code);
            StatusCn = Status;                 // Open-Meteo 直接给中文状态
            Icon = WmoToIcon(code);
            City = displayCity;
            Loaded = true;
            return true;
        }

        // 地理编码：地名 → 经纬度。
        // 首选 Photon（OSM 全文检索，街道级精度，国内可直连，免费无 key）：
        //   "昆明 官渡区 吴井路" → 吴井路 102.73, 25.02（层级用空格分隔）
        // 兜底 Open-Meteo geocoding（城市/县级，覆盖差些但稳）
        static bool FetchOpenMeteoCity(string name) {
            double plat, plon;
            if (TryGeocodePhoton(name, out plat, out plon)) {
                return FetchOpenMeteo(plat, plon, name);
            }
            // ---- 以下为 Open-Meteo geocoding 兜底 ----
            string gurl = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=zh&name=" +
                System.Uri.EscapeDataString(name);
            string gjson = null;
            try { gjson = Download(gurl, false); } catch { }
            if (gjson == null) gjson = Download(gurl, true);
            var mLat = Regex.Match(gjson, "\"latitude\"\\s*:\\s*([\\-0-9.]+)");
            var mLon = Regex.Match(gjson, "\"longitude\"\\s*:\\s*([\\-0-9.]+)");
            var mName = Regex.Match(gjson, "\"name\"\\s*:\\s*\"([^\"]+)\"");
            if (!mLat.Success || !mLon.Success) throw new Exception("地理编码失败: " + name + "（试试用空格分隔层级，如\"昆明 官渡区 吴井路\"）");
            string disp = mName.Success ? mName.Groups[1].Value : name;
            return FetchOpenMeteo(
                double.Parse(mLat.Groups[1].Value, CultureInfo.InvariantCulture),
                double.Parse(mLon.Groups[1].Value, CultureInfo.InvariantCulture), disp);
        }

        // Photon 街道级地理编码。GeoJSON 响应:
        // features[0].geometry.coordinates = [lon, lat]（注意顺序是经度在前！）
        static bool TryGeocodePhoton(string name, out double lat, out double lon) {
            lat = double.NaN; lon = double.NaN;
            string url = "https://photon.komoot.io/api/?lang=default&limit=1&q=" +
                System.Uri.EscapeDataString(name);
            string json = null;
            try { json = Download(url, false); } catch { }
            if (json == null) json = Download(url, true);   // 直连不通走代理
            var mC = Regex.Match(json,
                "\"coordinates\"\\s*:\\s*\\[\\s*([\\-0-9.]+)\\s*,\\s*([\\-0-9.]+)");
            if (!mC.Success) return false;
            lon = double.Parse(mC.Groups[1].Value, CultureInfo.InvariantCulture);
            lat = double.Parse(mC.Groups[2].Value, CultureInfo.InvariantCulture);
            return true;
        }

        // wttr.in 兜底（走系统代理），解析 j1 JSON。500 常是偶发，自动重试 1 次
        static void FetchWttrIn(string url, string cityName, string cnCity) {
            string json = null;
            Exception last = null;
            for (int i = 0; i < 2; i++) {
                try { json = Download(url, true); last = null; break; }
                catch (Exception e) { last = e; if (i == 0) Thread.Sleep(1200); }
            }
            if (last != null) throw last;
            if (json == null) throw new Exception("wttr.in 返回空");

            // nearest_area[0].areaName[0].value  → 英文城市（fallback）
            var mCity = Regex.Match(json,
                "\"areaName\"\\s*:\\s*\\[\\s*\\{\\s*\"value\"\\s*:\\s*\"([^\"]+)\"");
            string englishCity = mCity.Success ? mCity.Groups[1].Value : "";

            // 城市显示优先级：手动指定 > ip-api 中文 > wttr.in 英文
            City = !string.IsNullOrEmpty(cityName) ? cityName
                 : (!string.IsNullOrEmpty(cnCity) ? cnCity : englishCity);

            // current_condition[0].temp_C         → 温度（"19"）
            var mTemp = Regex.Match(json, "\"temp_C\"\\s*:\\s*\"([^\"]+)\"");
            Temp = mTemp.Success ? mTemp.Groups[1].Value : "-";

            // current_condition[0].weatherDesc[0].value  → 英文状态
            var mStatus = Regex.Match(json,
                "\"weatherDesc\"\\s*:\\s*\\[\\s*\\{\\s*\"value\"\\s*:\\s*\"([^\"]+)\"");
            Status = mStatus.Success ? mStatus.Groups[1].Value : "-";

            StatusCn = StatusToCn(Status);
            Icon = StatusToIcon(Status);
            Loaded = true;
        }

        // useProxy=true 时用系统代理（IE 代理设置，公司/VPN 代理环境也能通外网）；
        // useProxy=false 时直连（拿真实本机 IP，用于 IP 定位场景）。
        // 关键：必须显式设 Proxy，否则 WebClient 默认还是会走系统代理
        // 加 8 秒超时：避免代理挂死/无响应时一直卡住、连"错误信息"都看不到
        static string Download(string url, bool useProxy) {
            var task = Task.Run(() => DownloadInternal(url, useProxy));
            try {
                if (task.Wait(8000)) return task.Result;
                throw new TimeoutException("请求超时 (8秒未响应，检查代理设置)");
            } catch (AggregateException ae) {
                // Task.Wait() 会把内部异常包成 AggregateException，解开拿真实错误
                throw ae.InnerException ?? ae;
            }
        }

        static string DownloadInternal(string url, bool useProxy) {
            using (var wc = new WebClient()) {
                wc.Encoding = Encoding.UTF8;
                if (useProxy) {
                    // 走代理时必须显式 UseDefaultCredentials=true，
                    // 否则企业代理需要 NTLM/Kerberos 认证时 WebClient 会 401/407
                    wc.Proxy = WebRequest.GetSystemWebProxy();
                    wc.UseDefaultCredentials = true;
                } else {
                    wc.Proxy = null;
                }
                // 关键2：必须用浏览器 UA，否则 wttr.in 返回 HTML，JSON 正则匹配不到
                wc.Headers.Add("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                    "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                wc.Headers.Add("Accept", "application/json");
                // 强制按 UTF-8 解码原始字节，不依赖响应头 charset
                byte[] data = wc.DownloadData(url);
                return Encoding.UTF8.GetString(data);
            }
        }

        // 英文天气状态 → Unicode 图标（顺序很重要：从最具体到最一般）
        static string StatusToIcon(string s) {
            if (string.IsNullOrEmpty(s)) return "";
            string x = s.ToLowerInvariant();
            if (x.Contains("thunder") || x.Contains("storm")) return "⚡";
            if (x.Contains("snow")) return "❄";
            if (x.Contains("sleet") || x.Contains("ice")) return "❄";
            if (x.Contains("rain") || x.Contains("shower") || x.Contains("drizzle")) return "☂";
            if (x.Contains("fog") || x.Contains("mist") || x.Contains("haze") || x.Contains("smog")) return "≡";
            if (x.Contains("overcast")) return "☁";
            if (x.Contains("cloudy")) return "⛅";                // partly cloudy 优先
            if (x.Contains("clear") || x.Contains("sunny")) return "☀";
            return "";
        }

        // 英文状态 → 中文（尽量覆盖 wttr.in 常见值，未命中则回退英文）
        static string StatusToCn(string s) {
            if (string.IsNullOrEmpty(s)) return "";
            string x = s.ToLowerInvariant();
            if (x.Contains("thunder") || x.Contains("storm")) return "雷暴";
            if (x.Contains("snow")) return "雪";
            if (x.Contains("sleet")) return "雨夹雪";
            if (x.Contains("rain") || x.Contains("shower")) return "雨";
            if (x.Contains("drizzle")) return "毛毛雨";
            if (x.Contains("smog")) return "雾霾";
            if (x.Contains("fog")) return "雾";
            if (x.Contains("mist")) return "薄雾";
            if (x.Contains("haze")) return "霾";
            if (x.Contains("overcast")) return "阴";
            if (x.Contains("cloudy")) return "多云";
            if (x.Contains("clear") || x.Contains("sunny")) return "晴";
            return s;   // 未能识别则显示原英文
        }

        // Open-Meteo 的 WMO 天气码 → 中文状态（标准 WMO 4677 码表）
        static string WmoToStatus(int c) {
            if (c == 0 || c == 1) return "晴";
            if (c == 2) return "多云";
            if (c == 3) return "阴";
            if (c == 45 || c == 48) return "雾";
            if (c >= 51 && c <= 57) return "毛毛雨";
            if (c >= 61 && c <= 65) return "雨";
            if (c == 66 || c == 67) return "冻雨";
            if ((c >= 71 && c <= 77) || c == 85 || c == 86) return "雪";
            if (c >= 80 && c <= 82) return "阵雨";
            if (c == 96 || c == 99) return "雷暴冰雹";
            if (c == 95) return "雷暴";
            return "未知";
        }

        // WMO 天气码 → 图标
        static string WmoToIcon(int c) {
            if (c == 0 || c == 1) return "☀";
            if (c == 2) return "⛅";
            if (c == 3) return "☁";
            if (c == 45 || c == 48) return "≡";
            if ((c >= 51 && c <= 67) || (c >= 80 && c <= 82)) return "☂";
            if ((c >= 71 && c <= 77) || c == 85 || c == 86) return "❄";
            if (c >= 95) return "⚡";
            return "";
        }

        // 把所有字段拼成一行显示文本（如 "☀ 昆明 19°C 晴"）
        // 加载失败时显示具体错误信息（不再无限"获取中..."），方便诊断代理/网络问题
        public static string ComposeLine() {
            if (Loaded) {
                if (string.IsNullOrEmpty(City)) return "位置获取失败";
                string temp = string.IsNullOrEmpty(Temp) ? "-" : Temp + "°C";
                string status = string.IsNullOrEmpty(StatusCn) ? "" : " " + StatusCn;
                return (Icon + " " + City + " " + temp + status).Trim();
            }
            if (!string.IsNullOrEmpty(LastError)) return "天气失败: " + LastError;
            return "天气获取中...";
        }
    }

    // ------------------------------------------------------------ 设置持久化
    internal class Settings {
        public double Tint = 0.72;        // 背景不透明度
        public double Edge = 0.13;        // 高光描边强度
        public double Corner = 30;        // 圆角半径（像素，0=方角，150=半圆）
        public string City = "";           // 天气城市（留空 = 自动 IP 定位；支持中文如"北京"）
        // ---- v1.7 背景图片 ----
        public string BgImage = "";        // 背景图片绝对路径（空 = 使用渐变背景）
        public string BgFit = "fill";      // 缩放模式: fill 填充裁剪 / fit 适应留边 / stretch 拉伸 / tile 平铺
        public double BgDim = 0.30;        // 暗化遮罩强度 0~0.80（保证时间文字可读）
        public double BgBlur = 0.0;        // 图片模糊半径 0~30（px）
        public bool Topmost = false;
        public bool Seconds = true;
        public double X = double.NaN;     // 窗口位置
        public double Y = double.NaN;

        static string FilePath {
            get {
                return System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "settings.json");
            }
        }

        public static Settings Load() {
            var s = new Settings();
            try {
                if (!File.Exists(FilePath)) return s;
                var map = FlatJson(File.ReadAllText(FilePath, Encoding.UTF8));
                string v;
                if (map.TryGetValue("tint", out v)) s.Tint = Num(v, s.Tint);
                if (map.TryGetValue("edge", out v)) s.Edge = Num(v, s.Edge);
                if (map.TryGetValue("corner", out v)) s.Corner = Num(v, s.Corner);
                if (map.TryGetValue("city", out v)) s.City = v ?? "";
                // v1.7 背景图片
                if (map.TryGetValue("bgImage", out v)) s.BgImage = v ?? "";
                if (map.TryGetValue("bgFit", out v)) s.BgFit = NormFit(v);
                if (map.TryGetValue("bgDim", out v)) s.BgDim = Num(v, s.BgDim);
                if (map.TryGetValue("bgBlur", out v)) s.BgBlur = Num(v, s.BgBlur);
                if (map.TryGetValue("topmost", out v)) s.Topmost = Bool(v, s.Topmost);
                if (map.TryGetValue("seconds", out v)) s.Seconds = Bool(v, s.Seconds);
                if (map.TryGetValue("x", out v)) s.X = Num(v, s.X);
                if (map.TryGetValue("y", out v)) s.Y = Num(v, s.Y);
            } catch { }
            s.Tint = Clamp01(s.Tint);
            s.Edge = Clamp01(s.Edge);
            s.Corner = ClampCorner(s.Corner);
            s.BgDim = Clamp(s.BgDim, 0, 0.80);
            s.BgBlur = Clamp(s.BgBlur, 0, 30);
            return s;
        }

        // 缩放模式取值归一化，非法值回退 fill
        static string NormFit(string v) {
            if (v == "fit" || v == "stretch" || v == "tile" || v == "fill") return v;
            return "fill";
        }

        // 双参 clamp（C# 5：普通静态方法即可）
        static double Clamp(double v, double lo, double hi) {
            if (double.IsNaN(v)) return lo;
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        // JSON 字符串转义：**先转反斜杠再转引号**（Windows 路径必需，否则 C:\a 会破坏解析）
        static string Esc(string s) {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        public void Save() {
            try {
                var sb = new StringBuilder();
                sb.Append("{\n");
                sb.Append("  // 背景不透明度 (0.30 ~ 0.95)\n");
                sb.Append("  \"tint\": ").Append(F(Tint)).Append(",\n");
                sb.Append("  // 高光描边强度 (0.0 ~ 0.40)\n");
                sb.Append("  \"edge\": ").Append(F(Edge)).Append(",\n");
                sb.Append("  // 圆角半径（像素，0 = 方角，50 较圆润，150 = 半圆）\n");
                sb.Append("  \"corner\": ").Append(F(Corner)).Append(",\n");
                sb.Append("  // 天气城市（留空 = 自动 IP 定位，可手动指定如 Beijing / Shanghai）\n");
                sb.Append("  \"city\": \"").Append(Esc(City)).Append("\",\n");
                sb.Append("  // v1.7 背景图片路径（留空 = 使用渐变背景；拖图片到窗口或右键菜单选择）\n");
                sb.Append("  \"bgImage\": \"").Append(Esc(BgImage)).Append("\",\n");
                sb.Append("  // 背景图缩放模式：fill 填充裁剪 / fit 适应留边 / stretch 拉伸 / tile 平铺\n");
                sb.Append("  \"bgFit\": \"").Append(NormFit(BgFit)).Append("\",\n");
                sb.Append("  // 背景图暗化遮罩强度（0.0 ~ 0.80，越大时间文字越清晰）\n");
                sb.Append("  \"bgDim\": ").Append(F(BgDim)).Append(",\n");
                sb.Append("  // 背景图模糊半径（0 ~ 30 像素，0 = 不模糊）\n");
                sb.Append("  \"bgBlur\": ").Append(F(BgBlur)).Append(",\n");
                sb.Append("  // 窗口置顶（始终在最上层）\n");
                sb.Append("  \"topmost\": ").Append(Topmost ? "true" : "false").Append(",\n");
                sb.Append("  // 显示秒\n");
                sb.Append("  \"seconds\": ").Append(Seconds ? "true" : "false").Append(",\n");
                sb.Append("  // 窗口位置 X（像素）\n");
                sb.Append("  \"x\": ").Append(F(X)).Append(",\n");
                sb.Append("  // 窗口位置 Y（像素）\n");
                sb.Append("  \"y\": ").Append(F(Y)).Append("\n");
                sb.Append("}\n");
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            } catch { }
        }

        static double Clamp01(double v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }
        static double ClampCorner(double v) { return v < 0 ? 0 : (v > 150 ? 150 : v); }

        static string F(double v) {
            return double.IsNaN(v) ? "null" : v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        static double Num(string raw, double fallback) {
            double d;
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out d)
                ? d : fallback;
        }

        static bool Bool(string raw, bool fallback) {
            raw = raw.Trim().ToLowerInvariant();
            if (raw == "true" || raw == "1") return true;
            if (raw == "false" || raw == "0") return false;
            return fallback;
        }

        // 只解析扁平的 {"key": value} 结构，支持 // 单行 和 /* */ 块注释
        // 标准 JSON 不支持注释，本项目使用 JSON5 风格以便用户手编配置
        static Dictionary<string, string> FlatJson(string s) {
            var d = new Dictionary<string, string>();
            int i = s.IndexOf('{');
            if (i < 0) return d;
            i++;
            while (i < s.Length) {
                // 跳过空白与逗号
                while (i < s.Length && ", \t\r\n".IndexOf(s[i]) >= 0) i++;
                if (i >= s.Length || s[i] == '}') break;
                // 跳过 // 单行注释
                if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '/') {
                    while (i < s.Length && s[i] != '\n') i++;
                    continue;
                }
                // 跳过 /* */ 块注释
                if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '*') {
                    i += 2;
                    while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) i++;
                    i = Math.Min(i + 2, s.Length);
                    continue;
                }
                if (s[i] != '"') break;
                // 键名用带转义解析（s[i] 定位在开引号，解析后 i 停在闭引号之后）
                string key = ParseJsonString(s, ref i);
                while (i < s.Length && s[i] != ':') i++;
                i++;
                while (i < s.Length && " \t".IndexOf(s[i]) >= 0) i++;
                // 值：带引号 → 按 JSON 字符串解析（还原 \" \\ 等转义，且支持值内逗号）；
                //     不带引号 → 原样读到逗号/右括号（数字、布尔）
                string val;
                if (i < s.Length && s[i] == '"') {
                    val = ParseJsonString(s, ref i);
                } else {
                    var vb = new StringBuilder();
                    while (i < s.Length && s[i] != ',' && s[i] != '}') vb.Append(s[i++]);
                    val = vb.ToString().Trim();
                }
                d[key] = val;
            }
            return d;
        }

        // 解析 JSON 字符串字面量（s[i] 必须定位在开引号），返回还原后的内容，
        // 并把 i 推进到闭引号之后。这修复了 Load 不反转义导致的
        // "保存转义 → 读取原样 → 再保存再转义"指数膨胀 bug（city 字段雪球化）
        static string ParseJsonString(string s, ref int i) {
            i++;   // 跳过开引号
            var sb = new StringBuilder();
            while (i < s.Length) {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length) {
                    char e = s[i + 1];
                    if (e == '"') { sb.Append('"'); i += 2; }
                    else if (e == '\\') { sb.Append('\\'); i += 2; }
                    else if (e == 'n') { sb.Append('\n'); i += 2; }
                    else if (e == 't') { sb.Append('\t'); i += 2; }
                    else if (e == 'r') { sb.Append('\r'); i += 2; }
                    else if (e == 'b') { sb.Append('\b'); i += 2; }
                    else if (e == 'f') { sb.Append('\f'); i += 2; }
                    else { sb.Append(e); i += 2; }   // 未知转义保底：取转义符本身
                } else if (c == '"') {
                    i++;
                    break;
                } else {
                    sb.Append(c);
                    i++;
                }
            }
            return sb.ToString();
        }
    }

    // ------------------------------------------------------------ 主窗口
    public class MainWindow : Window {
        TextBlock hhText, mmText, ssText, dateText, colon2, weatherText;
        FrameworkElement bgLayer;
        Path edgePath;
        Settings cfg;
        MenuItem cityInfoItem;   // 右键菜单里的"天气城市"状态行（打开菜单时刷新文本）
        // v1.7 背景图片相关图层引用（用于运行时切换/调参）
        Rectangle bgImageRect;   // 图片层（ImageBrush 填充）
        Rectangle bgDimRect;     // 暗化遮罩层
        Rectangle bgFrostRect;   // 磨砂颗粒层（图片模式下降低不透明度）
        Polygon bgPurpleTri, bgCyanTri;   // 渐变两片三角形
        Rectangle bgBandRect;    // 斜向高光带
        MenuItem[] fitItems;     // 缩放模式子菜单项（刷新勾选状态用）

        public MainWindow() {
            Title = "clock";
            Width = 740;
            Height = 300;
            WindowStartupLocation = WindowStartupLocation.Manual;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            WindowStyle = WindowStyle.None;
            ShowInTaskbar = true;
            ResizeMode = ResizeMode.NoResize;

            cfg = Settings.Load();
            Topmost = cfg.Topmost;

            TryLoadIcon();
            BuildUi();
            RestorePosition();

            ContextMenu = BuildMenu();
            Closing += delegate { SavePosition(); };

            // v1.7：直接把图片拖到时钟上即可设为背景
            EnableImageDrop();

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += delegate { UpdateClock(); };
            timer.Start();
            UpdateClock();

            WatchSettings();

            // v1.5：天气。启动时拉一次，之后每 10 分钟刷新
            // city 留空 = 自动 IP 定位；填了则用指定城市（支持中文，如"北京"）
            FetchWeather();
            var weatherTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
            weatherTimer.Tick += delegate { FetchWeather(); };
            weatherTimer.Start();
        }

        // 按当前 cfg.City 发起一次天气获取。
        // 每次调用时实时读配置（而不是构造时算死），配合 settings.json 热重载：
        // 改完 city 立即生效，无需重启程序
        void FetchWeather() {
            bool autoLocate = string.IsNullOrEmpty(cfg.City);
            string url = autoLocate ? "https://wttr.in/?format=j1" : ("https://wttr.in/" + System.Uri.EscapeDataString(cfg.City) + "?format=j1");
            string cityName = autoLocate ? null : cfg.City;   // 手动指定则直接用它做显示名
            Weather.FetchAsync(delegate { Dispatcher.BeginInvoke(new Action(UpdateWeather)); }, url, cityName);
        }

        void UpdateWeather() {
            weatherText.Text = Weather.ComposeLine();
        }

        // 监视 settings.json：编辑器保存时实时生效（重载到内存并重绘）
        void WatchSettings() {
            try {
                var watcher = new FileSystemWatcher(
                    AppDomain.CurrentDomain.BaseDirectory, "settings.json");
                watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime;
                watcher.Changed += delegate { Dispatcher.BeginInvoke(new Action(ReloadSettings)); };
                watcher.Created += delegate { Dispatcher.BeginInvoke(new Action(ReloadSettings)); };
                watcher.EnableRaisingEvents = true;
            } catch { }
        }

        void ReloadSettings() {
            // 编辑器保存过程中可能短暂占用文件，重试几次
            Settings fresh = null;
            for (int i = 0; i < 8; i++) {
                try { fresh = Settings.Load(); break; }
                catch { System.Threading.Thread.Sleep(60); }
            }
            if (fresh == null) return;
            Settings old = cfg;
            cfg = fresh;
            bgLayer.Clip = BuildSquircleGeometry(740, 300, cfg.Corner);
            edgePath.Data = BuildSquircleGeometry(740, 300, cfg.Corner);
            ApplyBackground();
            ApplyEdge();
            ApplySeconds();
            if (Topmost != cfg.Topmost) Topmost = cfg.Topmost;
            // 城市配置变了 → 立即按新城市重新拉天气（无需重启）
            if (old.City != fresh.City) FetchWeather();
        }

        // ============================================================ 界面
        void BuildUi() {
            bgLayer = BuildBackground(740, 300, cfg.Corner);
            edgePath = new Path {
                Data = BuildSquircleGeometry(740, 300, cfg.Corner),
                StrokeThickness = 1,
                SnapsToDevicePixels = true,
                Stretch = Stretch.Fill,   // 描边铺满整个布局区
                IsHitTestVisible = false  // 仅装饰，stack/dateText 接收鼠标
            };
            ApplyBackground();
            ApplyEdge();

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 圆形关闭按钮：无论背景圆角调多大都协调。悬停加深。
            var closeNorm = new SolidColorBrush(Color.FromArgb(0xD9, 0xF4, 0x5A, 0x5A));
            var closeHover = new SolidColorBrush(Color.FromArgb(0xFF, 0xE8, 0x38, 0x38));
            var closeBtn = new Border {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),   // 半宽 = 完美圆
                Background = closeNorm,
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 14, 0),
                Cursor = Cursors.Hand,
                Child = new TextBlock {
                    Text = "×",
                    FontSize = 16,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            closeBtn.MouseLeftButtonUp += delegate { Close(); };
            closeBtn.MouseEnter += delegate { closeBtn.Background = closeHover; };
            closeBtn.MouseLeave += delegate { closeBtn.Background = closeNorm; };
            Grid.SetRow(closeBtn, 0);
            grid.Children.Add(closeBtn);

            // 左上角天气（红色框附近），暖黄色在紫青背景上对比舒服
            weatherText = new TextBlock {
                Text = "天气获取中...",
                FontFamily = new FontFamily("华文琥珀, STHupo, 幼圆, YouYuan, Microsoft YaHei"),
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x82)),   // 暖黄
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(22, 26, 0, 0),
                Effect = new DropShadowEffect {
                    Color = Color.FromRgb(0xFF, 0xD5, 0x4F),
                    ShadowDepth = 0,
                    BlurRadius = 12,
                    Opacity = 0.6
                }
            };
            grid.Children.Add(weatherText);
            Grid.SetRowSpan(weatherText, grid.RowDefinitions.Count);  // 铺满，靠左上角对齐

            var stack = new StackPanel {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            // 时分/冒号：白→暖金垂直渐变（暖色调与紫→青冷背景形成色相对撞，突出对比）
            // 秒：浅粉→玫瑰粉渐变（保留柔和焦点身份，但比纯色更有层次）
            var timeGradient = new LinearGradientBrush {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1)
            };
            timeGradient.GradientStops.Add(new GradientStop(Colors.White, 0.0));
            timeGradient.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0xF2, 0xC8), 0.5));   // 中段淡暖白
            timeGradient.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0xD2, 0x6E), 1.0));   // 底部暖金
            var ssGradient = new LinearGradientBrush {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1)
            };
            ssGradient.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0xD9, 0xE3), 0.0));     // 顶部浅粉
            ssGradient.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0xA6, 0xC2), 1.0));     // 底部玫瑰粉

            hhText = CreateTextBlock("00", 130, timeGradient);
            mmText = CreateTextBlock("00", 130, timeGradient);
            ssText = CreateTextBlock("00", 195, ssGradient);
            var colon1 = CreateTextBlock(":", 130, timeGradient);
            colon2 = CreateTextBlock(":", 130, timeGradient);

            // 柔光发光：时分/冒号暖白光晕（呼应渐变），秒柔和粉光晕（不夸张）
            var glowWhite = new DropShadowEffect {
                Color = Color.FromRgb(0xFF, 0xF6, 0xDE),   // 暖白光，呼应金字渐变
                ShadowDepth = 0,
                BlurRadius = 16,
                Opacity = 0.85
            };
            var glowPink = new DropShadowEffect {
                Color = Color.FromRgb(0xFF, 0xD8, 0xE4),   // 极淡粉光，配合柔和粉字体
                ShadowDepth = 0,
                BlurRadius = 16,                            // 从 22 调到 16
                Opacity = 0.65                              // 从 0.95 调到 0.65
            };
            hhText.Effect = glowWhite;
            mmText.Effect = glowWhite;
            colon1.Effect = glowWhite;
            colon2.Effect = glowWhite;
            ssText.Effect = glowPink;

            stack.Children.Add(hhText);
            stack.Children.Add(colon1);
            stack.Children.Add(mmText);
            stack.Children.Add(colon2);
            stack.Children.Add(ssText);
            Grid.SetRow(stack, 1);
            grid.Children.Add(stack);

            dateText = new TextBlock {
                Text = "-",
                FontFamily = new FontFamily("华文琥珀, STHupo, 幼圆, YouYuan, Microsoft YaHei"),
                FontSize = 15,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 12),
                Effect = new DropShadowEffect {
                    Color = Color.FromRgb(0xFF, 0xFF, 0xFF),
                    ShadowDepth = 0,
                    BlurRadius = 10,
                    Opacity = 0.75
                }
            };
            Grid.SetRow(dateText, 2);
            grid.Children.Add(dateText);

            grid.Children.Insert(0, bgLayer);
            Grid.SetRowSpan(bgLayer, grid.RowDefinitions.Count);   // 背景铺满全部三行
            grid.Children.Insert(1, edgePath);
            Grid.SetRowSpan(edgePath, grid.RowDefinitions.Count);  // 高光描边铺满全部三行
            Content = grid;

            EnableDrag(stack);
            EnableDrag(dateText);

            ApplySeconds();
        }

        TextBlock CreateTextBlock(string text, double fontSize, Brush foreground) {
            return new TextBlock {
                Text = text,
                // 圆润可爱风 v2：华文琥珀（STHUPO.TTF，超粗圆体，自带 Q 弹感）优先，
                // 回退幼圆 → Comic Sans MS → 等宽兜底
                FontFamily = new FontFamily("华文琥珀, STHupo, 幼圆, YouYuan, Comic Sans MS, Consolas"),
                FontWeight = FontWeights.Bold,
                FontSize = fontSize,
                Foreground = foreground,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        // 超椭圆（squircle）圆角矩形，覆盖整个 w×h 容器。
        // 四条直线边 + 四个角各一条三次贝塞尔；控制点偏移 d = 0.85r，
        // 曲率从直边的 0 连续过渡到对角点，无圆弧-直边突变，视觉圆润。
        // 几何坐标从 (0,0) 到 (w,h)，Path 的保留尺寸即 w×h，配合 Stretch.Fill 铺满布局区。
        Geometry BuildSquircleGeometry(double w, double h, double r) {
            r = Math.Max(0, Math.Min(r, Math.Min(w, h) / 2 - 2));
            double d = 0.85 * r;
            var fig = new PathFigure {
                StartPoint = new Point(r, 0),
                IsClosed = true,
                Segments = {
                    new LineSegment(new Point(w - r, 0), true),
                    new BezierSegment(new Point(w - r + d, 0), new Point(w, r - d), new Point(w, r), true),
                    new LineSegment(new Point(w, h - r), true),
                    new BezierSegment(new Point(w, h - r + d), new Point(w - r + d, h), new Point(w - r, h), true),
                    new LineSegment(new Point(r, h), true),
                    new BezierSegment(new Point(r - d, h), new Point(0, h - r + d), new Point(0, h - r), true),
                    new LineSegment(new Point(0, r), true),
                    new BezierSegment(new Point(0, r - d), new Point(r - d, 0), new Point(r, 0), true)
                }
            };
            var pg = new PathGeometry();
            pg.Figures.Add(fig);
            pg.Freeze();
            return pg;
        }

        // v1.4 背景：霓虹紫青对角分层 + 斜向高光分隔带 + 磨砂/亚克力颗粒。
        // v1.7 新增背景图片层（置底）与暗化遮罩层（图片之上，保证时间文字可读）。
        // 顶层 Canvas 用 squircle 几何做裁剪（圆角面板），整体透明度由 bgLayer.Opacity 控制。
        FrameworkElement BuildBackground(double w, double h, double r) {
            var canvas = new Canvas {
                Width = w,
                Height = h,
                Clip = BuildSquircleGeometry(w, h, r),
                IsHitTestVisible = false
            };

            // ---- v1.7 图片层（置底）。外扩 pad 是为模糊留出出血区，
            //      避免 BlurEffect 在边缘采样到透明而出现发虚的白边
            double pad = 40;
            bgImageRect = new Rectangle {
                Width = w + pad * 2,
                Height = h + pad * 2,
                Visibility = Visibility.Collapsed
            };
            Canvas.SetLeft(bgImageRect, -pad);
            Canvas.SetTop(bgImageRect, -pad);

            // 左上→右下方向：霓虹紫渐变（左上亮紫，向右下渐深）
            var purple = new LinearGradientBrush(
                Color.FromRgb(0x9A, 0x3B, 0xF7),
                Color.FromRgb(0x1B, 0x0A, 0x3A),
                new Point(0, 0), new Point(1, 1));
            purple.GradientStops.Insert(1, new GradientStop(Color.FromRgb(0x5E, 0x1A, 0xA8), 0.55));
            bgPurpleTri = new Polygon {
                Points = new PointCollection { new Point(0, 0), new Point(0, h), new Point(w, 0) },
                Fill = purple
            };

            // 左上→右下方向：霓虹青渐变（右下亮青，向左上渐深）
            var cyan = new LinearGradientBrush(
                Color.FromRgb(0x06, 0x1B, 0x2A),
                Color.FromRgb(0x00, 0xE5, 0xFF),
                new Point(0, 0), new Point(1, 1));
            cyan.GradientStops.Insert(1, new GradientStop(Color.FromRgb(0x00, 0x9C, 0xC8), 0.55));
            bgCyanTri = new Polygon {
                Points = new PointCollection { new Point(0, h), new Point(w, 0), new Point(w, h) },
                Fill = cyan
            };

            // 斜向高光分隔带：沿左下→右上对角线，法线方向中间亮、两侧渐变透明，模拟发光分割
            double cx = w / 2, cy = h / 2;
            double nl = Math.Sqrt(w * w + h * h);
            double nx = h / nl, ny = w / nl;      // 对角线的单位法向量
            double W = 120;                        // 光晕带宽
            var bandBrush = new LinearGradientBrush {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = new Point(cx - nx * W / 2, cy - ny * W / 2),
                EndPoint = new Point(cx + nx * W / 2, cy + ny * W / 2),
                GradientStops = {
                    new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0),
                    new GradientStop(Color.FromArgb(0x62, 0xE8, 0xFF, 0xFF), 0.5),
                    new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1)
                }
            };
            bgBandRect = new Rectangle { Width = w, Height = h, Fill = bandBrush };

            // 暗化遮罩（图片之上）：纯黑半透明，强度由 bgDim 控制
            bgDimRect = new Rectangle {
                Width = w, Height = h,
                Fill = new SolidColorBrush(Colors.Black),
                Visibility = Visibility.Collapsed
            };

            // 磨砂 / 亚克力颗粒层（运行时生成细颗粒位图）
            bgFrostRect = new Rectangle {
                Width = w, Height = h,
                Fill = MakeFrostBrush((int)(w / 5), (int)(h / 5)),
                Opacity = 0.28
            };

            canvas.Children.Add(bgImageRect);   // 1 图片（最底）
            canvas.Children.Add(bgPurpleTri);   // 2 渐变
            canvas.Children.Add(bgCyanTri);     // 3 渐变
            canvas.Children.Add(bgBandRect);    // 4 高光带
            canvas.Children.Add(bgDimRect);     // 5 暗化遮罩（盖在图片上）
            canvas.Children.Add(bgFrostRect);   // 6 磨砂颗粒（最上）
            return canvas;
        }

        // 生成一张细微噪点位图，模拟磨砂/亚克力玻璃的颗粒感，再拉伸平铺到背景。
        static ImageBrush MakeFrostBrush(int w, int h) {
            w = Math.Max(8, w); h = Math.Max(8, h);
            var rnd = new Random(20240906);
            var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
            var px = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++) {
                byte g = (byte)(140 + rnd.Next(-60, 60));
                px[i * 4] = g; px[i * 4 + 1] = g; px[i * 4 + 2] = g;
                px[i * 4 + 3] = 150;
            }
            bmp.WritePixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);
            var ib = new ImageBrush(bmp);
            ib.Stretch = Stretch.Fill;
            return ib;
        }

        void EnableDrag(UIElement elem) {
            elem.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) {
                if (e.ButtonState != MouseButtonState.Pressed) return;
                try { DragMove(); } catch { }
            };
        }

        // ============================================================ 背景 / 外观
        // 背景整体透明度可调（0.30 ~ 0.95）：作用于所有渐变/高光带/磨砂层，
        // 文字（时间/日期/关闭按钮）在背景之上不受影响，保持清晰。
        void ApplyBackground() {
            bgLayer.Opacity = cfg.Tint;
            ApplyBackgroundImage();
        }

        // ---------------------------------------------------------- v1.7 背景图片
        // 按当前配置刷新背景图相关图层。无图/图片读取失败时自动回退渐变背景（不报错）
        void ApplyBackgroundImage() {
            bool has = !string.IsNullOrEmpty(cfg.BgImage) && File.Exists(cfg.BgImage);
            BitmapImage bmp = null;
            if (has) {
                try {
                    // DecodePixelWidth 降采样到 2 倍窗宽：4K 大图也不吃内存、不卡顿
                    bmp = LoadBitmap(cfg.BgImage, (int)Math.Round(Width * 2));
                } catch { bmp = null; }
            }
            bool ok = bmp != null;

            // 图片层
            if (ok) {
                // 模糊时向四周外扩（给 BlurEffect 留出血区，避免边缘发虚露底）；
                // 不模糊时严格贴合窗口，保证"填充裁剪"的取景不被多裁
                double pad = cfg.BgBlur > 0.5 ? 40 : 0;
                bgImageRect.Width = Width + pad * 2;
                bgImageRect.Height = Height + pad * 2;
                Canvas.SetLeft(bgImageRect, -pad);
                Canvas.SetTop(bgImageRect, -pad);

                var brush = new ImageBrush(bmp) {
                    AlignmentX = AlignmentX.Center,
                    AlignmentY = AlignmentY.Center
                };
                ApplyFit(brush, cfg.BgFit, bmp);
                brush.Freeze();
                bgImageRect.Fill = brush;
                bgImageRect.Visibility = Visibility.Visible;
                // 模糊：图片模式下才生效（RenderingBias.Performance 换帧率）
                bgImageRect.Effect = cfg.BgBlur > 0.5
                    ? new BlurEffect { Radius = cfg.BgBlur, RenderingBias = RenderingBias.Performance }
                    : null;
            } else {
                bgImageRect.Fill = null;
                bgImageRect.Effect = null;
                bgImageRect.Visibility = Visibility.Collapsed;
            }

            // 暗化遮罩
            bgDimRect.Opacity = cfg.BgDim;
            bgDimRect.Visibility = (ok && cfg.BgDim > 0.01) ? Visibility.Visible : Visibility.Collapsed;

            // 渐变三件套：有图时隐藏（图片替代渐变），无图时显示
            var gv = ok ? Visibility.Collapsed : Visibility.Visible;
            bgPurpleTri.Visibility = gv;
            bgCyanTri.Visibility = gv;
            bgBandRect.Visibility = gv;

            // 磨砂颗粒：图片模式下减半，只留一点质感
            bgFrostRect.Opacity = ok ? 0.12 : 0.28;
        }

        // 按文件名后缀判断是否为支持展示的图片（防呆：拖入文件夹/压缩包时静默忽略）
        static bool IsImageFile(string path) {
            if (string.IsNullOrEmpty(path)) return false;
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return ext == ".jpg" || ext == ".jpeg" || ext == ".png" ||
                   ext == ".bmp" || ext == ".gif" || ext == ".webp";
        }

        // 加载位图：OnLoad 立刻读完并释放文件句柄（之后图片可被移动/删除）
        static BitmapImage LoadBitmap(string path, int decodeWidth) {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        // 四种缩放模式映射到 ImageBrush
        static void ApplyFit(ImageBrush b, string fit, BitmapImage src) {
            string f = (fit == null) ? "fill" : fit;
            if (f == "fit") {
                b.Stretch = Stretch.Uniform;          // 适应：完整显示，可能留边
            } else if (f == "stretch") {
                b.Stretch = Stretch.Fill;             // 拉伸：铺满，可能变形
            } else if (f == "tile") {
                // 平铺：按原始像素尺寸重复，视口高度按图片宽高比折算
                double tw = Math.Min(360, Math.Max(120, src.PixelWidth));
                double ratio = src.PixelWidth > 0 ? ((double)src.PixelHeight / src.PixelWidth) : 0.6;
                b.Viewport = new Rect(0, 0, tw, Math.Max(20, tw * ratio));
                b.ViewportUnits = BrushMappingMode.Absolute;
                b.Stretch = Stretch.None;
                b.TileMode = TileMode.Tile;
            } else {
                b.Stretch = Stretch.UniformToFill;    // 填充裁剪（默认）
            }
        }

        // 设置 / 清除背景图（拖拽与菜单共用），并落盘
        void SetBgImage(string path) {
            if (!IsImageFile(path) || !File.Exists(path)) return;
            cfg.BgImage = path;
            cfg.Save();
            ApplyBackgroundImage();
        }

        void ClearBgImage() {
            cfg.BgImage = "";
            cfg.Save();
            ApplyBackgroundImage();
        }

        void ApplyEdge() {
            edgePath.Stroke = new SolidColorBrush(
                Color.FromArgb((byte)Math.Round(cfg.Edge * 255), 0xFF, 0xFF, 0xFF));
        }

        void ApplySeconds() {
            var v = cfg.Seconds ? Visibility.Visible : Visibility.Collapsed;
            colon2.Visibility = v;
            ssText.Visibility = v;
        }

        // ============================================================ 设置菜单
        ContextMenu BuildMenu() {
            var cm = new ContextMenu();
            cm.Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1B, 0x1E, 0x30));
            cm.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            cm.Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xEE, 0xF7));

            // 滑块拖动会高频触发，这里只改内存并重绘，统一在菜单关闭时落盘
            var tintItem = new MenuItem { StaysOpenOnClick = true };
            tintItem.Header = MakeSlider("背景不透明度", cfg.Tint, 0.30, 0.95, delegate(double v) {
                cfg.Tint = v;
                ApplyBackground();
            });

            var edgeItem = new MenuItem { StaysOpenOnClick = true };
            edgeItem.Header = MakeSlider("描边强度", cfg.Edge, 0.0, 0.40, delegate(double v) {
                cfg.Edge = v;
                ApplyEdge();
            });

            var cornerItem = new MenuItem { StaysOpenOnClick = true };
            cornerItem.Header = MakeSlider("圆角大小", cfg.Corner, 0.0, 150.0, delegate(double v) {
                cfg.Corner = v;
                bgLayer.Clip = BuildSquircleGeometry(740, 300, v);   // 背景裁剪
                edgePath.Data = BuildSquircleGeometry(740, 300, v);  // 高光描边
            });

            var topItem = new MenuItem {
                Header = "窗口置顶",
                IsCheckable = true,
                IsChecked = cfg.Topmost,
                StaysOpenOnClick = true
            };
            topItem.Click += delegate {
                cfg.Topmost = topItem.IsChecked;
                Topmost = cfg.Topmost;
            };

            var secItem = new MenuItem {
                Header = "显示秒",
                IsCheckable = true,
                IsChecked = cfg.Seconds,
                StaysOpenOnClick = true
            };
            secItem.Click += delegate {
                cfg.Seconds = secItem.IsChecked;
                ApplySeconds();
            };

            var exitItem = new MenuItem { Header = "退出" };
            exitItem.Click += delegate { Close(); };

            // ---- v1.7 背景图片 ----
            var chooseBgItem = new MenuItem { Header = "选择背景图片…" };
            chooseBgItem.Click += delegate {
                var dlg = new Microsoft.Win32.OpenFileDialog {
                    Title = "选择背景图片",
                    Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp|所有文件|*.*"
                };
                if (dlg.ShowDialog() == true) SetBgImage(dlg.FileName);
            };
            var clearBgItem = new MenuItem { Header = "清除背景图片（回到渐变）" };
            clearBgItem.Click += delegate { ClearBgImage(); };

            // 缩放模式子菜单（互斥勾选）
            var fitMenu = new MenuItem { Header = "缩放模式" };
            string[][] fitDefs = new string[][] {
                new string[] { "fill", "填充裁剪（推荐）" },
                new string[] { "fit", "适应留边" },
                new string[] { "stretch", "拉伸铺满" },
                new string[] { "tile", "平铺" }
            };
            fitItems = new MenuItem[fitDefs.Length];
            for (int i = 0; i < fitDefs.Length; i++) {
                string key = fitDefs[i][0];
                var mi = new MenuItem {
                    Header = fitDefs[i][1],
                    IsCheckable = true,
                    IsChecked = (NormFitKey(cfg.BgFit) == key)
                };
                mi.Click += delegate {
                    cfg.BgFit = key;
                    cfg.Save();
                    ApplyBackgroundImage();
                    RefreshFitChecks();
                };
                fitItems[i] = mi;
                fitMenu.Items.Add(mi);
            }

            var dimItem = new MenuItem { StaysOpenOnClick = true };
            dimItem.Header = MakeSlider("背景暗化", cfg.BgDim, 0.0, 0.80, delegate(double v) {
                cfg.BgDim = v;
                ApplyBackgroundImage();
            });

            var blurItem = new MenuItem { StaysOpenOnClick = true };
            blurItem.Header = MakeSlider("背景模糊", cfg.BgBlur, 0.0, 30.0, delegate(double v) {
                cfg.BgBlur = v;
                ApplyBackgroundImage();
            });

            var bgInfoItem = new MenuItem {
                Header = "背景图片",
                IsEnabled = false
            };
            var bgTipItem = new MenuItem {
                Header = "提示：可直接把图片拖到时钟上",
                IsEnabled = false
            };

            // ---- 天气城市：菜单直接改，不用手编 settings.json ----
            cityInfoItem = new MenuItem {
                Header = "天气城市",
                IsEnabled = false   // 纯状态行，不可点
            };
            var setCityItem = new MenuItem { Header = "设置城市…" };
            setCityItem.Click += delegate {
                string result;
                if (PromptCity(cfg.City, out result)) {
                    cfg.City = result;
                    cfg.Save();
                    Weather.Loaded = false;      // 让显示区先回到"获取中"
                    FetchWeather();
                }
            };
            var autoCityItem = new MenuItem { Header = "自动定位 (IP)" };
            autoCityItem.Click += delegate {
                if (cfg.City.Length == 0) return;
                cfg.City = "";
                cfg.Save();
                Weather.Loaded = false;
                FetchWeather();
            };
            // 每次打开菜单时刷新状态行（城市可能在对话框里刚改过）
            cm.Opened += delegate {
                cityInfoItem.Header = "天气城市: " +
                    (string.IsNullOrEmpty(cfg.City) ? "自动定位 (IP)" : cfg.City);
                // 背景图状态行：显示文件名（太长则截断）
                string bgName = string.IsNullOrEmpty(cfg.BgImage)
                    ? "渐变背景" : System.IO.Path.GetFileName(cfg.BgImage);
                if (bgName.Length > 22) bgName = bgName.Substring(0, 21) + "…";
                bgInfoItem.Header = "背景图片: " + bgName;
                RefreshFitChecks();
            };

            cm.Closed += delegate { cfg.Save(); };

            cm.Items.Add(tintItem);
            cm.Items.Add(edgeItem);
            cm.Items.Add(cornerItem);
            cm.Items.Add(new Separator());
            cm.Items.Add(topItem);
            cm.Items.Add(secItem);
            cm.Items.Add(new Separator());
            cm.Items.Add(bgInfoItem);
            cm.Items.Add(chooseBgItem);
            cm.Items.Add(clearBgItem);
            cm.Items.Add(fitMenu);
            cm.Items.Add(dimItem);
            cm.Items.Add(blurItem);
            cm.Items.Add(bgTipItem);
            cm.Items.Add(new Separator());
            cm.Items.Add(cityInfoItem);
            cm.Items.Add(setCityItem);
            cm.Items.Add(autoCityItem);
            cm.Items.Add(new Separator());
            cm.Items.Add(exitItem);
            return cm;
        }

        // 缩放模式取值归一化（与 Settings.NormFit 同规则）
        static string NormFitKey(string v) {
            if (v == "fit" || v == "stretch" || v == "tile" || v == "fill") return v;
            return "fill";
        }

        // 拖拽图片到窗口设为背景（宽松判定：只认图片扩展名，其余静默忽略）
        void EnableImageDrop() {
            AllowDrop = true;
            DragOver += delegate(object s, DragEventArgs e) {
                string p = DropImagePath(e);
                e.Effects = (p != null) ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            Drop += delegate(object s, DragEventArgs e) {
                string p = DropImagePath(e);
                if (p != null) SetBgImage(p);
                e.Handled = true;
            };
        }

        // 从拖拽数据里取第一个图片文件路径；不是图片则返回 null
        static string DropImagePath(DragEventArgs e) {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return null;
            for (int i = 0; i < files.Length; i++) {
                if (IsImageFile(files[i])) return files[i];
            }
            return null;
        }

        // 刷新缩放模式子菜单的勾选状态
        void RefreshFitChecks() {
            if (fitItems == null) return;
            string[] keys = new string[] { "fill", "fit", "stretch", "tile" };
            for (int i = 0; i < fitItems.Length && i < keys.Length; i++) {
                fitItems[i].IsChecked = (NormFitKey(cfg.BgFit) == keys[i]);
            }
        }

        // 城市输入对话框（纯代码 WPF 窗口，无 XAML）。确定返回 true，result 带出输入值
        bool PromptCity(string current, out string result) {
            result = current;
            var win = new Window {
                Title = "设置天气城市",
                Width = 440,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                WindowStyle = WindowStyle.ToolWindow,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true,
                Owner = this,
                Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1B, 0x1E, 0x30))
            };
            var panel = new StackPanel { Margin = new Thickness(16) };

            var tip = new TextBlock {
                Text = "支持街道级精度，层级用空格分隔：\n例：昆明 官渡区 吴井路　|　北京 海淀区\n留空 = 自动 IP 定位（代理 TUN 模式下可能不准）",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xB4, 0xC8)),
                Margin = new Thickness(0, 0, 0, 10),
                FontSize = 12
            };

            var box = new TextBox {
                Text = current,
                FontSize = 14,
                Padding = new Thickness(6, 5, 6, 5)
            };

            var btnPanel = new StackPanel {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var okBtn = new Button {
                Content = "确定",
                Width = 76,
                IsDefault = true,          // 回车=确定
                Padding = new Thickness(0, 4, 0, 4),
                Margin = new Thickness(0, 0, 10, 0)
            };
            var cancelBtn = new Button {
                Content = "取消",
                Width = 76,
                IsCancel = true,           // ESC=取消
                Padding = new Thickness(0, 4, 0, 4)
            };
            okBtn.Click += delegate { win.DialogResult = true; win.Close(); };

            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            panel.Children.Add(tip);
            panel.Children.Add(box);
            panel.Children.Add(btnPanel);
            win.Content = panel;
            box.Focus();
            box.SelectAll();

            bool? ok = win.ShowDialog();
            if (ok == true) result = box.Text.Trim();
            return ok == true;
        }

        StackPanel MakeSlider(string label, double value, double min, double max,
                              Action<double> onChanged) {
            var panel = new StackPanel {
                Orientation = Orientation.Vertical,
                Width = 210,
                Margin = new Thickness(6, 4, 6, 4)
            };
            var caption = new TextBlock {
                Text = Label(label, value),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xEE, 0xF7)),
                Margin = new Thickness(0, 0, 0, 4)
            };
            var slider = new Slider {
                Minimum = min,
                Maximum = max,
                Value = value,
                SmallChange = 0.01,
                LargeChange = 0.05,
                TickFrequency = 0.01,
                IsSnapToTickEnabled = false
            };
            slider.ValueChanged += delegate {
                caption.Text = Label(label, slider.Value);
                onChanged(slider.Value);
            };
            panel.Children.Add(caption);
            panel.Children.Add(slider);
            return panel;
        }

        static string Label(string label, double v) {
            return label + "  " + v.ToString("0.00", CultureInfo.InvariantCulture);
        }

        // ============================================================ 位置记忆
        void RestorePosition() {
            if (!double.IsNaN(cfg.X) && !double.IsNaN(cfg.Y)) {
                Left = cfg.X;
                Top = cfg.Y;
            } else {
                Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
                Top = (SystemParameters.PrimaryScreenHeight - Height) / 2;
            }
            ClampToScreen();
        }

        void ClampToScreen() {
            double vl = SystemParameters.VirtualScreenLeft;
            double vt = SystemParameters.VirtualScreenTop;
            double vw = SystemParameters.VirtualScreenWidth;
            double vh = SystemParameters.VirtualScreenHeight;

            if (Width > vw) Width = vw;
            if (Height > vh) Height = vh;
            if (Left < vl) Left = vl;
            if (Top < vt) Top = vt;
            if (Left + Width > vl + vw) Left = vl + vw - Width;
            if (Top + Height > vt + vh) Top = vt + vh - Height;
        }

        void SavePosition() {
            cfg.X = Left;
            cfg.Y = Top;
            cfg.Save();
        }

        // ============================================================ 图标 / 计时
        void TryLoadIcon() {
            try {
                string p = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "clock.ico");
                if (File.Exists(p))
                    Icon = BitmapFrame.Create(new Uri(p, UriKind.Absolute));
            } catch { }
        }

        static string Pad(int n) { return n < 10 ? "0" + n : "" + n; }

        void UpdateClock() {
            var now = DateTime.Now;
            hhText.Text = Pad(now.Hour);
            mmText.Text = Pad(now.Minute);
            if (cfg.Seconds) ssText.Text = Pad(now.Second);

            string[] wk = { "日", "一", "二", "三", "四", "五", "六" };
            string w = wk[(int)now.DayOfWeek];
            dateText.Text = string.Format("{0} 年 {1} 月 {2} 日  星期{3}",
                now.Year, Pad(now.Month), Pad(now.Day), w);
        }
    }

    public class App : Application {
        [STAThread]
        public static void Main() {
            // .NET 4.0 默认只支持 TLS 1.0/1.1，wttr.in 等现代服务强制 TLS 1.2+，
            // 不开这行 WebClient 会 SSL 握手失败但只在 catch 里静默吞掉
            System.Net.ServicePointManager.SecurityProtocol |=
                System.Net.SecurityProtocolType.Tls12 |
                System.Net.SecurityProtocolType.Tls11;

            var app = new App();

            // 未捕获异常改为弹窗而不是静默崩溃，便于定位问题
            app.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e) {
                try {
                    MessageBox.Show(e.Exception.ToString(), "桌面时钟 - 运行错误",
                                    MessageBoxButton.OK, MessageBoxImage.Error);
                } catch { }
                e.Handled = true;
            };

            var win = new MainWindow();
            app.Run(win);
        }
    }
}
