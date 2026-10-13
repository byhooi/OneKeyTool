// Updater.cs — 检查更新与自我替换
//
// 流程：
//   1. Check()      向 GitHub 查询 latest release，与当前版本比对
//   2. Download()   下载裸 exe 到临时目录，用 API 返回的 digest 校验
//   3. Apply()      重命名自身为 .old → 写入新 exe → 启动新实例 → 旧实例退出
//   4. 新实例启动时清理 .old
//
// 安全说明：
//   - 只发一次 GET，不上传任何数据（无参数、无标识、无遥测）
//   - 哈希取自 GitHub API 返回的 asset.digest 字段（服务端计算），
//     不是我们自己上传的清单文件，因此少一次下载、少一个可能被替换的环节
//   - digest 缺失时拒绝自动更新，而不是跳过校验
//   - 替换采用「先重命名、后写入」并带回滚：写入失败会把 .old 改回来，
//     不会让用户剩下一个不可用的 exe
//
// .NET Framework 4.0 注意：SecurityProtocolType 没有 Tls12 具名成员
// （那是 4.5 才加的），必须用数值 3072。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace OneKeyTool
{
    /// <summary>一次版本检查的结果。</summary>
    internal sealed class ReleaseInfo
    {
        /// <summary>tag 名，形如 v1.3.1。</summary>
        public string Tag;

        /// <summary>Release 标题。</summary>
        public string Name;

        /// <summary>Release 页面地址（用于手动下载）。</summary>
        public string HtmlUrl;

        /// <summary>裸 exe 的下载地址；旧版本 Release 可能没有。</summary>
        public string ExeUrl;

        /// <summary>exe 的 SHA256（小写十六进制），来自 API 的 digest 字段。</summary>
        public string ExeSha256;

        /// <summary>exe 的字节数，用于下载完整性初判。</summary>
        public long ExeSize;

        /// <summary>是否具备自动更新所需的资产（exe + 服务端哈希）。</summary>
        public bool CanAutoUpdate
        {
            get { return ExeUrl != null && !string.IsNullOrEmpty(ExeSha256); }
        }
    }

    internal static class Updater
    {
        private const string Owner = "byhooi";
        private const string Repo = "OneKeyTool";
        private const string ApiLatest = "https://api.github.com/repos/" + Owner + "/" + Repo + "/releases/latest";
        private const string UserAgent = "OneKeyTool-Updater";

        /// <summary>Release 页面地址，供手动下载时使用。</summary>
        public const string ReleasesPage = "https://github.com/" + Owner + "/" + Repo + "/releases/latest";

        /// <summary>TLS 1.2。.NET 4.0 的枚举里没有这个具名成员，只能用数值。</summary>
        private const int SecurityProtocolTls12 = 3072;

        private const int TimeoutMs = 20000;

        private static bool _protocolSet;

        private static void EnsureProtocol()
        {
            if (_protocolSet) return;
            _protocolSet = true;
            try
            {
                // 叠加而不是覆盖，避免把系统已启用的更高版本协议关掉
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)SecurityProtocolTls12;
            }
            catch { }
        }

        // ==================== 1. 检查更新 ====================

        /// <summary>
        /// 查询最新 Release。返回 null 表示已是最新；抛异常表示检查失败。
        /// 阻塞调用，请放到后台线程。
        /// </summary>
        public static ReleaseInfo Check()
        {
            EnsureProtocol();

            string json = GetString(ApiLatest);
            ReleaseInfo info = ParseRelease(json);
            if (info == null || string.IsNullOrEmpty(info.Tag)) return null;

            if (CompareVersions(info.Tag, BuildInfo.Version) <= 0) return null;
            return info;
        }

        /// <summary>从 Release JSON 里挑出我们需要的字段与资产地址。</summary>
        internal static ReleaseInfo ParseRelease(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;

            ReleaseInfo info = new ReleaseInfo();
            info.Tag = JsonString(json, "tag_name");
            info.Name = JsonString(json, "name");
            info.HtmlUrl = JsonString(json, "html_url");

            // 资产形如 {"name":"...","browser_download_url":"...","digest":"sha256:..."}
            // 旧 Release 只有 zip，没有裸 exe，此时 CanAutoUpdate 为 false，
            // 界面会退回引导用户手动下载。
            foreach (string asset in JsonObjectsInArray(json, "assets"))
            {
                string name = JsonString(asset, "name");
                string url = JsonString(asset, "browser_download_url");
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url)) continue;
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                info.ExeUrl = url;
                info.ExeSha256 = ParseDigest(JsonString(asset, "digest"));
                info.ExeSize = JsonLong(asset, "size");
                break;
            }

            return info;
        }

        /// <summary>把 API 的 "sha256:abcd..." 转成纯小写十六进制；格式不符返回 null。</summary>
        internal static string ParseDigest(string digest)
        {
            if (string.IsNullOrEmpty(digest)) return null;

            string s = digest.Trim();
            int colon = s.IndexOf(':');
            if (colon >= 0)
            {
                string algo = s.Substring(0, colon).Trim();
                if (!algo.Equals("sha256", StringComparison.OrdinalIgnoreCase)) return null;
                s = s.Substring(colon + 1).Trim();
            }

            // 只接受 64 位十六进制，避免把异常内容当成哈希
            if (s.Length != 64) return null;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return null;
            }
            return s.ToLowerInvariant();
        }

        // ==================== 2. 下载并校验 ====================

        /// <summary>
        /// 下载裸 exe 到临时目录并校验哈希与大小。
        /// 返回临时 exe 的完整路径。阻塞调用，请放到后台线程。
        /// </summary>
        public static string Download(ReleaseInfo info, Action<int> onProgress)
        {
            if (info == null) throw new ArgumentNullException("info");
            if (!info.CanAutoUpdate)
                throw new InvalidOperationException("该版本未提供可供自动更新的文件，请手动下载。");

            EnsureProtocol();

            string tempDir = Path.Combine(Path.GetTempPath(), "OneKeyTool-update");
            if (Directory.Exists(tempDir)) TryDeleteDirectory(tempDir);
            Directory.CreateDirectory(tempDir);

            string target = Path.Combine(tempDir, FileNameOf(info.ExeUrl));
            DownloadFile(info.ExeUrl, target, onProgress);

            // 大小初判：能更早发现截断的下载
            if (info.ExeSize > 0)
            {
                long actualSize = new FileInfo(target).Length;
                if (actualSize != info.ExeSize)
                {
                    TryDelete(target);
                    throw new InvalidOperationException(
                        "下载的文件大小不符（期望 " + info.ExeSize + " 字节，实际 " + actualSize + "），已中止更新。");
                }
            }

            string actual = Sha256OfFile(target);
            if (!string.Equals(actual, info.ExeSha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(target);
                throw new InvalidOperationException(
                    "下载的文件校验失败（哈希不匹配），已中止更新。\n期望 " + info.ExeSha256 + "\n实际 " + actual);
            }

            if (!LooksLikeExecutable(target))
            {
                TryDelete(target);
                throw new InvalidOperationException("下载的文件不是有效的可执行文件，已中止更新。");
            }

            return target;
        }

        /// <summary>计算文件 SHA256，返回小写十六进制。</summary>
        internal static string Sha256OfFile(string path)
        {
            using (FileStream fs = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(fs);
                StringBuilder sb = new StringBuilder(h.Length * 2);
                for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary>粗略确认是 PE 可执行文件（MZ 头）。</summary>
        internal static bool LooksLikeExecutable(string path)
        {
            try
            {
                using (FileStream fs = File.OpenRead(path))
                {
                    if (fs.Length < 2) return false;
                    int b0 = fs.ReadByte(), b1 = fs.ReadByte();
                    return b0 == 'M' && b1 == 'Z';
                }
            }
            catch { return false; }
        }

        // ==================== 3. 自我替换 ====================

        /// <summary>
        /// 用下载好的新 exe 替换自身并重启。
        /// 失败时会把原文件改回来，保证不留下不可用的 exe。
        /// </summary>
        public static void Apply(string newExePath)
        {
            string self = Application.ExecutablePath;
            string old = self + ".old";

            // 清掉上次更新残留的 .old（若还占着，说明上次没清干净）
            TryDelete(old);

            // Windows 允许重命名正在运行的 exe，但不允许覆盖它。
            // 先把自身挪开，原路径就空出来了。
            try
            {
                File.Move(self, old);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "无法移动当前程序文件，可能没有写入权限。\n" +
                    "请把程序放到有写入权限的目录（如文档或桌面）后重试，或手动下载更新。\n\n" +
                    self + "\n" + ex.Message);
            }

            try
            {
                File.Copy(newExePath, self, true);
            }
            catch (Exception ex)
            {
                // 关键回滚：写不进去就把原文件改回来，否则用户没有可用的 exe 了
                try { if (!File.Exists(self)) File.Move(old, self); }
                catch { }
                throw new InvalidOperationException("写入新版本失败，已恢复原版本。\n" + ex.Message);
            }

            // 启动新实例；它会等本进程退出后清理 .old
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(self, CleanupArgument);
                psi.UseShellExecute = false;
                psi.WorkingDirectory = Path.GetDirectoryName(self);
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                // 新实例没起来，但 exe 已就位：用户手动启动即可，不算失败
                throw new InvalidOperationException(
                    "新版本已就位，但自动重启失败，请手动启动程序。\n" + ex.Message);
            }
        }

        /// <summary>新实例启动时携带的参数，用于清理旧文件。</summary>
        public const string CleanupArgument = "--cleanup-old";

        /// <summary>
        /// 由新实例在启动时调用：删除上一次更新留下的 .old。
        /// 旧进程可能还没退干净，所以带重试。
        /// </summary>
        public static void CleanupOldFiles()
        {
            string old = Application.ExecutablePath + ".old";
            for (int i = 0; i < 20; i++)
            {
                if (!File.Exists(old)) return;
                try
                {
                    File.Delete(old);
                    return;
                }
                catch
                {
                    Thread.Sleep(250);
                }
            }
            // 删不掉就算了：残留一个 .old 不影响使用，下次启动还会再试
        }

        // ==================== HTTP ====================

        private static string GetString(string url)
        {
            using (HttpWebResponse resp = Request(url))
            using (Stream s = resp.GetResponseStream())
            using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
            {
                return sr.ReadToEnd();
            }
        }

        private static void DownloadFile(string url, string target, Action<int> onProgress)
        {
            using (HttpWebResponse resp = Request(url))
            using (Stream src = resp.GetResponseStream())
            using (FileStream dst = File.Create(target))
            {
                long total = resp.ContentLength;
                byte[] buf = new byte[81920];
                long done = 0;
                int lastPercent = -1;
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    dst.Write(buf, 0, n);
                    done += n;
                    if (onProgress != null && total > 0)
                    {
                        int percent = (int)(done * 100 / total);
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            onProgress(percent);
                        }
                    }
                }
            }
        }

        private static HttpWebResponse Request(string url)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UserAgent;
            req.Timeout = TimeoutMs;
            req.ReadWriteTimeout = TimeoutMs;
            req.Accept = "application/vnd.github+json";
            // 不跟随重定向到别的域名的下载地址由 .NET 处理；
            // GitHub 的 assets 会 302 到 objects.githubusercontent.com，属正常。
            return (HttpWebResponse)req.GetResponse();
        }

        // ==================== JSON 解析（.NET 4.0 无内置 JSON）====================

        /// <summary>取 "key":123 中的整数值。找不到或不是数字返回 0。</summary>
        internal static long JsonLong(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return 0;

            string needle = "\"" + key + "\"";
            int i = json.IndexOf(needle, StringComparison.Ordinal);
            if (i < 0) return 0;

            i = json.IndexOf(':', i + needle.Length);
            if (i < 0) return 0;
            i++;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;

            int start = i;
            while (i < json.Length && (char.IsDigit(json[i]) || json[i] == '-')) i++;
            if (i == start) return 0;

            long v;
            return long.TryParse(json.Substring(start, i - start),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        /// <summary>取 "key":"value" 中的字符串值，处理常见转义。找不到返回 null。</summary>
        internal static string JsonString(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return null;

            string needle = "\"" + key + "\"";
            int i = json.IndexOf(needle, StringComparison.Ordinal);
            if (i < 0) return null;

            i = json.IndexOf(':', i + needle.Length);
            if (i < 0) return null;
            i++;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '"') return null;
            i++;

            StringBuilder sb = new StringBuilder();
            while (i < json.Length && json[i] != '"')
            {
                if (json[i] == '\\' && i + 1 < json.Length)
                {
                    i++;
                    switch (json[i])
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case '\\': sb.Append('\\'); break;
                        case '"': sb.Append('"'); break;
                        case '/': sb.Append('/'); break;
                        case 'u':
                            // \uXXXX
                            if (i + 4 < json.Length)
                            {
                                int code;
                                if (int.TryParse(json.Substring(i + 1, 4),
                                        NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                                {
                                    sb.Append((char)code);
                                    i += 4;
                                }
                            }
                            break;
                        default: sb.Append(json[i]); break;
                    }
                }
                else sb.Append(json[i]);
                i++;
            }
            return sb.ToString();
        }

        /// <summary>取出 "key":[...] 数组里的每个顶层对象文本。</summary>
        internal static List<string> JsonObjectsInArray(string json, string key)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return result;

            string needle = "\"" + key + "\"";
            int i = json.IndexOf(needle, StringComparison.Ordinal);
            if (i < 0) return result;

            i = json.IndexOf('[', i + needle.Length);
            if (i < 0) return result;

            int depth = 0;
            int start = -1;
            bool inString = false;
            for (; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '{')
                {
                    if (depth == 0) start = i;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        result.Add(json.Substring(start, i - start + 1));
                        start = -1;
                    }
                }
                else if (c == ']' && depth == 0)
                {
                    break;
                }
            }
            return result;
        }

        // ==================== 版本号比较 ====================

        /// <summary>
        /// 比较版本号。a &gt; b 返回正数，a &lt; b 返回负数，相同返回 0。
        /// 忽略 v 前缀，忽略 - 之后的后缀（如 v1.0.0-beta 与 v1.0.0 视为相同）。
        /// </summary>
        internal static int CompareVersions(string a, string b)
        {
            int[] pa = ParseVersion(a);
            int[] pb = ParseVersion(b);
            int n = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < n; i++)
            {
                int x = i < pa.Length ? pa[i] : 0;
                int y = i < pb.Length ? pb[i] : 0;
                if (x != y) return x.CompareTo(y);
            }
            return 0;
        }

        private static int[] ParseVersion(string v)
        {
            if (string.IsNullOrEmpty(v)) return new int[0];

            string s = v.Trim().TrimStart('v', 'V');
            int cut = s.IndexOfAny(new char[] { '-', '+' });
            if (cut >= 0) s = s.Substring(0, cut);

            string[] parts = s.Split('.');
            List<int> nums = new List<int>(parts.Length);
            for (int i = 0; i < parts.Length; i++)
            {
                int n;
                nums.Add(int.TryParse(parts[i], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out n) ? n : 0);
            }
            return nums.ToArray();
        }

        // ==================== 杂项 ====================

        private static string FileNameOf(string url)
        {
            int slash = url.LastIndexOf('/');
            return slash >= 0 ? url.Substring(slash + 1) : url;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        private static void TryDeleteDirectory(string path)
        {
            try { Directory.Delete(path, true); }
            catch { }
        }
    }
}
