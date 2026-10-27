using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

// 便携版直播源获取器：双击即走 双节点配置 -> 最新ZIP -> 解密解压 -> 解析 -> 过滤广告 -> 生成 FongMi JSON
// 用法:
//   LiveConfig.exe                     自动从双节点获取最新配置与 ZIP
//   LiveConfig.exe <url>               从指定 URL 下载 (zip/txt)
//   LiveConfig.exe <本地文件.zip|txt>   直接解析本地文件
//   可选参数: -p <解压密码>  -kw <广告关键词,逗号分隔>  -o <输出json路径>
class Program
{
    const string ZIP_PASSWORD = "DBhkhdnefkhfq,#%";
    const string DEFAULT_EPG = "http://207.56.16.135:9999/tv/diyp.php?ch={name}&date={date}";
    static readonly string[] CONFIG_NODES = new string[] {
        "http://207.56.13.146:9999/qmtv/config.json",
        "http://qmtv.tvbus.cc/qmtv/config.json"
    };
    static readonly string[] AD_KEYWORDS = new string[] {
        "applive", "甄选", "好物", "福利", "会员专享", "今日推荐", "精品推荐", "优享",
        "养生馆", "严选", "银龄", "美好生活", "健康有约", "健康在线"
    };

    static string zipPass = ZIP_PASSWORD;
    static List<string> adKw = new List<string>(AD_KEYWORDS);
    static int filteredCount = 0;

    class Group { public string Name; public Dictionary<string, List<string>> Chs = new Dictionary<string, List<string>>(); }
    static List<Group> groups = new List<Group>();

    static int Main(string[] args)
    {
        Console.WriteLine("========================================================");
        Console.WriteLine("   直播源便携获取器 (TVBox/FongMi LiveConfig 生成)");
        Console.WriteLine("   双击运行: 自动获取最新配置 -> 下载ZIP -> 生成JSON");
        Console.WriteLine("========================================================");

        string target = null, outFile = null;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "-p" && i + 1 < args.Length) zipPass = args[++i];
            else if (a == "-kw" && i + 1 < args.Length)
            {
                adKw = args[++i].Split(new[] { ',', '，', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0).ToList();
            }
            else if (a == "-o" && i + 1 < args.Length) outFile = args[++i];
            else target = a;
        }
        if (outFile == null) outFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sss.json");

        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
        try { Run(target, outFile); }
        catch (Exception ex) { Console.WriteLine("[失败] " + ex.Message); }
        Console.Write("\n按回车键退出...");
        Console.ReadLine();
        return 0;
    }

    static void Run(string target, string outFile)
    {
        string epg = DEFAULT_EPG;
        byte[] data = null;

        if (target != null && File.Exists(target))
        {
            Console.WriteLine("[本地] 加载文件: " + target);
            data = File.ReadAllBytes(target);
        }
        else if (target != null && Regex.IsMatch(target, @"^https?://", RegexOptions.IgnoreCase))
        {
            Console.WriteLine("[下载] " + target);
            data = HttpGetBytes(target);
        }
        else
        {
            // 双节点接力: 节点返回 source 后尝试下载, 失败则切下一个节点
            foreach (string node in CONFIG_NODES)
            {
                Console.WriteLine("[节点] " + node);
                string cfgText;
                try { cfgText = Encoding.UTF8.GetString(HttpGetBytes(node)); }
                catch (Exception ex) { Console.WriteLine("       节点失败: " + ex.Message); continue; }

                var mEpg = Regex.Match(cfgText, "\"epg\"\\s*:\\s*\"([^\"]+)\"");
                if (mEpg.Success) epg = JsonUnescape(mEpg.Groups[1].Value);
                var mApk = Regex.Match(cfgText, "\"apk\"\\s*:\\s*\"([^\"]+)\"");
                if (mApk.Success) Console.WriteLine("       APK更新地址: " + JsonUnescape(mApk.Groups[1].Value));

                var mSrc = Regex.Match(cfgText, "\"source\"\\s*:\\s*\"([^\"]+)\"");
                if (!mSrc.Success) { Console.WriteLine("       返回中无 source 字段"); continue; }
                string src = JsonUnescape(mSrc.Groups[1].Value);
                Console.WriteLine("       最新源: " + src);
                try { data = HttpGetBytes(src); break; }
                catch (Exception ex) { Console.WriteLine("       下载失败: " + ex.Message); }
            }
            if (data == null) throw new Exception("所有配置节点均不可用, 可将 ZIP/TXT 拖到本 EXE 上离线解析");
        }

        // 内容分发: ZIP / 文本
        List<KeyValuePair<string, byte[]>> files = new List<KeyValuePair<string, byte[]>>();
        if (data.Length > 4 && data[0] == 'P' && data[1] == 'K')
        {
            Console.WriteLine("[ZIP] 检测到压缩包, 开始解密解压 (密码: " + zipPass + ")");
            files = ReadZip(data);
            Console.WriteLine("[ZIP] 解出 " + files.Count + " 个文件: " +
                string.Join(", ", files.Select(f => f.Key).ToArray()));
        }
        else files.Add(new KeyValuePair<string, byte[]>("input.txt", data));

        foreach (var f in files)
        {
            string text = DecodeBytes(f.Value);
            if (text.TrimStart().StartsWith("[") || text.TrimStart().StartsWith("{")) IngestJson(text, f.Key);
            else if (text.TrimStart().StartsWith("#EXTM3U")) IngestM3u(text, f.Key);
            else IngestTxt(text, f.Key);
        }

        if (groups.Count == 0) throw new Exception("没有解析到任何频道");
        string json = BuildJson(epg);
        File.WriteAllText(outFile, json, new UTF8Encoding(false));

        int chTotal = groups.Sum(g => g.Chs.Count);
        int urlTotal = groups.Sum(g => g.Chs.Values.Sum(v => v.Count));
        Console.WriteLine("\n--------------------------------------------------------");
        Console.WriteLine("分组 " + groups.Count + " | 频道 " + chTotal + " | 线路 " + urlTotal + " | 已过滤广告 " + filteredCount);
        Console.WriteLine("EPG: " + epg);
        Console.WriteLine("已生成: " + outFile);
        Console.WriteLine("在 FongMi TV / 影视仓 中以本 JSON 文件作为直播配置导入即可");
        Console.WriteLine("--------------------------------------------------------");
    }

    // ==================== 网络与文本 ====================
    static byte[] HttpGetBytes(string url)
    {
        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
        req.UserAgent = "okhttp/3.12.0";
        req.Timeout = 20000;
        req.ReadWriteTimeout = 30000;
        req.AllowAutoRedirect = true;
        using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
        using (var ms = new MemoryStream())
        using (var rs = resp.GetResponseStream())
        {
            byte[] buf = new byte[65536]; int n;
            while ((n = rs.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
            Console.WriteLine("       下载成功: " + ms.Length + " 字节");
            return ms.ToArray();
        }
    }

    static string DecodeBytes(byte[] b)
    {
        try { return new UTF8Encoding(false, true).GetString(b); }
        catch { return Encoding.GetEncoding("gbk").GetString(b); }
    }

    // 还原 JSON 字符串转义: \/ -> / , \" -> " , \\ -> \ , \uXXXX -> 字符
    static string JsonUnescape(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
            char c = s[++i];
            switch (c)
            {
                case '/': sb.Append('/'); break;
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    {
                        int cp;
                        if (i + 4 < s.Length && int.TryParse(s.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out cp))
                        {
                            sb.Append((char)cp); i += 4;
                        }
                        else sb.Append(c);
                        break;
                    }
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    // ==================== ZIP: 解密 + 解压 ====================
    static List<KeyValuePair<string, byte[]>> ReadZip(byte[] buf)
    {
        int eocd = -1;
        for (int i = buf.Length - 22; i >= Math.Max(0, buf.Length - 65557); i--)
            if (buf[i] == 0x50 && buf[i + 1] == 0x4b && buf[i + 2] == 0x05 && buf[i + 3] == 0x06) { eocd = i; break; }
        if (eocd < 0) throw new Exception("ZIP 目录未找到");
        int count = BitConverter.ToUInt16(buf, eocd + 10);
        int p = (int)BitConverter.ToUInt32(buf, eocd + 16);
        var passBytes = Encoding.UTF8.GetBytes(zipPass);
        var result = new List<KeyValuePair<string, byte[]>>();

        for (int n = 0; n < count; n++)
        {
            if (p + 46 > buf.Length || BitConverter.ToUInt32(buf, p) != 0x02014b50) break;
            int flag = BitConverter.ToUInt16(buf, p + 8);
            int method = BitConverter.ToUInt16(buf, p + 10);
            int modTime = BitConverter.ToUInt16(buf, p + 12);
            uint crc = BitConverter.ToUInt32(buf, p + 16);
            int compSize = (int)BitConverter.ToUInt32(buf, p + 20);
            int nameLen = BitConverter.ToUInt16(buf, p + 28);
            int extraLen = BitConverter.ToUInt16(buf, p + 30);
            int cmtLen = BitConverter.ToUInt16(buf, p + 32);
            int loff = (int)BitConverter.ToUInt32(buf, p + 42);
            string name = Encoding.UTF8.GetString(buf, p + 46, nameLen);
            string enc = null;

            if (method == 99)   // WinZip AES: extra field 0x9901
            {
                bool found = false;
                for (int e = p + 30 + nameLen; e + 4 <= p + 30 + nameLen + extraLen; )
                {
                    int id = BitConverter.ToUInt16(buf, e); int sz = BitConverter.ToUInt16(buf, e + 2);
                    if (id == 0x9901)
                    {
                        method = BitConverter.ToUInt16(buf, e + 9);
                        enc = "aes:" + buf[e + 8]; found = true; break;
                    }
                    e += 4 + sz;
                }
                if (!found) { p += 46 + nameLen + extraLen + cmtLen; continue; }
            }
            else if ((flag & 1) != 0) enc = "zip";

            if (!name.EndsWith("/") && compSize > 0)
            {
                int dataStart = loff + 30 + BitConverter.ToUInt16(buf, loff + 26) + BitConverter.ToUInt16(buf, loff + 28);
                byte[] raw = Slice(buf, dataStart, compSize);
                try
                {
                    if (enc == "zip") raw = ZipCryptoDecrypt(raw, passBytes, (modTime >> 8) & 0xff, (int)((crc >> 24) & 0xff));
                    else if (enc != null && enc.StartsWith("aes")) raw = AesDecrypt(raw, passBytes, int.Parse(enc.Split(':')[1]));
                }
                catch (Exception ex) { throw new Exception("解密失败(" + name + "): " + ex.Message + " —— 检查解压密码"); }

                byte[] bytes;
                if (method == 0) bytes = raw;
                else if (method == 8) bytes = Inflate(raw);
                else { p += 46 + nameLen + extraLen + cmtLen; continue; }
                result.Add(new KeyValuePair<string, byte[]>(name, bytes));
            }
            p += 46 + nameLen + extraLen + cmtLen;
        }
        return result;
    }

    static byte[] Slice(byte[] src, int off, int len)
    {
        byte[] r = new byte[len]; Buffer.BlockCopy(src, off, r, 0, len); return r;
    }

    static byte[] Inflate(byte[] raw)
    {
        using (var ms = new MemoryStream(raw))
        using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
        using (var outMs = new MemoryStream())
        {
            ds.CopyTo(outMs);
            return outMs.ToArray();
        }
    }

    // ---- PKWARE ZipCrypto ----
    static uint[] CRCT = BuildCrcTable();
    static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = ((c & 1) != 0) ? (0xEDB88320 ^ (c >> 1)) : (c >> 1);
            t[n] = c;
        }
        return t;
    }
    static void ZcUpd(uint[] k, byte b)
    {
        k[0] = (k[0] >> 8) ^ CRCT[(k[0] ^ b) & 0xff];
        k[1] = unchecked(k[1] + (k[0] & 0xff));
        k[1] = unchecked(k[1] * 134775813 + 1);
        k[2] = (k[2] >> 8) ^ CRCT[(k[2] ^ (byte)(k[1] >> 24)) & 0xff];
    }
    static byte[] ZipCryptoDecrypt(byte[] data, byte[] pw, int chk1, int chk2)
    {
        if (pw == null || pw.Length == 0) throw new Exception("ZIP 已加密, 请用 -p 提供解压密码");
        uint[] k = { 0x12345678, 0x23456789, 0x34567890 };
        foreach (byte b in pw) ZcUpd(k, b);
        byte[] outB = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            ushort t = (ushort)((k[2] | 2) & 0xffff);
            byte plain = (byte)(data[i] ^ (byte)(((t * (t ^ 1)) >> 8) & 0xff));
            outB[i] = plain;
            ZcUpd(k, plain);
        }
        if (outB.Length >= 12 && outB[11] != chk1 && outB[11] != chk2) throw new Exception("密码校验不通过");
        byte[] res = new byte[outB.Length - 12]; Buffer.BlockCopy(outB, 12, res, 0, res.Length);
        return res;  // 前 12 字节为加密头
    }

    // ---- WinZip AES (zip4j 兼容: PBKDF2-SHA1 1000 轮 + AES-CTR, 计数器从 1 开始) ----
    static byte[] AesDecrypt(byte[] data, byte[] pw, int strength)
    {
        int keyLen = strength == 1 ? 16 : strength == 2 ? 24 : 32;
        int saltLen = strength == 1 ? 8 : strength == 2 ? 12 : 16;
        if (data.Length < saltLen + 2) throw new Exception("数据过短");
        byte[] salt = Slice(data, 0, saltLen);
        byte[] payload = Slice(data, saltLen + 2, data.Length - saltLen - 2);
        using (var pbkdf2 = new Rfc2898DeriveBytes(pw, salt, 1000, HashAlgorithmName.SHA1))
        {
            byte[] bits = pbkdf2.GetBytes(keyLen + 16 + 2);   // key + macKey + iv + pvv
            byte[] aesKey = Slice(bits, 0, keyLen);
            byte[] iv = Slice(bits, keyLen, 16);
            using (var aes = Aes.Create())
            {
                aes.Key = aesKey; aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None;
                using (var enc = aes.CreateEncryptor())
                {
                    byte[] outB = new byte[payload.Length];
                    byte[] counter = new byte[16];
                    for (int off = 0, blockIdx = 1; off < payload.Length; off += 16, blockIdx++)
                    {
                        // 16 字节大端计数器, 从 1 开始
                        uint lo = (uint)blockIdx;
                        counter[15] = (byte)(lo & 0xff); counter[14] = (byte)((lo >> 8) & 0xff);
                        counter[13] = (byte)((lo >> 16) & 0xff); counter[12] = (byte)((lo >> 24) & 0xff);
                        byte[] ks = enc.TransformFinalBlock(counter, 0, 16);
                        int n = Math.Min(16, payload.Length - off);
                        for (int i = 0; i < n; i++) outB[off + i] = (byte)(payload[off + i] ^ ks[i]);
                    }
                    return outB;
                }
            }
        }
    }

    // ==================== 解析: TXT / M3U / JSON ====================
    static void AddCh(string groupName, string chName, List<string> urls)
    {
        string hay = (chName + " " + string.Join(" ", urls.ToArray())).ToLowerInvariant();
        foreach (string kw in adKw)
            if (hay.Contains(kw)) { filteredCount++; return; }
        Group g = groups.FirstOrDefault(x => x.Name == groupName);
        if (g == null) { g = new Group { Name = groupName }; groups.Add(g); }
        List<string> lst;
        if (!g.Chs.TryGetValue(chName, out lst)) { lst = new List<string>(); g.Chs[chName] = lst; }
        foreach (string u in urls) if (!lst.Contains(u)) lst.Add(u);
    }

    static void IngestTxt(string text, string srcName)
    {
        string cur = "未分组"; int n = 0;
        foreach (string rawLine in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            if (Regex.IsMatch(line, "#genre#", RegexOptions.IgnoreCase))
            {
                cur = line.Split(',')[0].Replace("#genre#", "").Replace("#GENRE#", "").Trim();
                if (cur.Length == 0) cur = "未分组";
                continue;
            }
            int ci = line.IndexOf(',');
            if (ci < 1) continue;
            string ch = line.Substring(0, ci).Trim();
            List<string> urls = line.Substring(ci + 1).Split('#').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            if (ch.Length > 0 && urls.Count > 0) { AddCh(cur, ch, urls); n++; }
        }
        Console.WriteLine("[" + srcName + "] TXT 解析: " + n + " 个频道");
    }

    static void IngestM3u(string text, string srcName)
    {
        string cur = "未分组", name = ""; int n = 0;
        foreach (string rawLine in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            string line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#EXTINF"))
            {
                var g = Regex.Match(line, "group-title=\"([^\"]*)\"", RegexOptions.IgnoreCase);
                cur = g.Success && g.Groups[1].Value.Length > 0 ? g.Groups[1].Value : "未分组";
                int li = line.LastIndexOf(',');
                name = li >= 0 ? line.Substring(li + 1).Trim() : "";
                continue;
            }
            if (line.StartsWith("#")) continue;
            if (name.Length > 0 && Regex.IsMatch(line, "^https?://", RegexOptions.IgnoreCase))
            {
                AddCh(cur, name, new List<string> { line }); n++; name = "";
            }
        }
        Console.WriteLine("[" + srcName + "] M3U 解析: " + n + " 个频道");
    }

    static void IngestJson(string text, string srcName)
    {
        object data;
        try { data = new JavaScriptSerializer().DeserializeObject(text); }
        catch (Exception ex) { Console.WriteLine("[" + srcName + "] JSON 解析失败: " + ex.Message); return; }
        int n = JsonWalk(data);
        Console.WriteLine("[" + srcName + "] JSON 解析: " + n + " 个频道");
    }

    static int JsonWalk(object data)
    {
        int n = 0;
        var list = data as object[];
        var dict = data as Dictionary<string, object>;
        if (list != null) { foreach (object g in list) n += WalkGroup(g); return n; }
        if (dict != null)
        {
            if (dict.ContainsKey("lives") && dict["lives"] is object[])
            {
                foreach (object l in (object[])dict["lives"])
                {
                    var ld = l as Dictionary<string, object>; if (ld == null) continue;
                    object gs;
                    if (ld.TryGetValue("groups", out gs) && gs is object[]) n += JsonWalk(gs);
                    else
                    {
                        object chs; object gname;
                        ld.TryGetValue("name", out gname); if (gname == null) ld.TryGetValue("group", out gname);
                        if (ld.TryGetValue("channels", out chs) && chs is object[])
                        {
                            var wrap = new Dictionary<string, object>();
                            wrap["name"] = gname; wrap["channel"] = chs;
                            n += WalkGroup(wrap);
                        }
                    }
                }
                return n;
            }
            if (dict.ContainsKey("groups")) return JsonWalk(dict["groups"]);
        }
        return n;
    }

    static int WalkGroup(object gObj)
    {
        var g = gObj as Dictionary<string, object>; if (g == null) return 0;
        object tmp;
        string gName = "未分组";
        if (g.TryGetValue("name", out tmp) && tmp is string) gName = (string)tmp;
        else if (g.TryGetValue("group", out tmp) && tmp is string) gName = (string)tmp;
        int n = 0;
        object chans = null;
        if (!g.TryGetValue("channel", out chans)) g.TryGetValue("channels", out chans);
        var chList = chans as object[]; if (chList == null) return 0;
        foreach (object cObj in chList)
        {
            var c = cObj as Dictionary<string, object>; if (c == null) continue;
            object nameObj, urlsObj;
            if (!c.TryGetValue("name", out nameObj) || !(nameObj is string)) continue;
            var urls = new List<string>();
            if (c.TryGetValue("urls", out urlsObj) && urlsObj is object[])
                foreach (object u in (object[])urlsObj)
                {
                    if (u is string) urls.Add((string)u);
                    else { var ud = u as Dictionary<string, object>; object uu; if (ud != null && ud.TryGetValue("url", out uu) && uu is string) urls.Add((string)uu); }
                }
            if (urls.Count > 0) { AddCh(gName, (string)nameObj, urls); n++; }
        }
        return n;
    }

    // ==================== 生成 FongMi LiveConfig JSON ====================
    static string Esc(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    static string BuildJson(string epg)
    {
        var sb = new StringBuilder();
        sb.Append("{\n  \"lives\": [{\n");
        sb.Append("    \"name\": \"直播\",\n");
        if (!string.IsNullOrEmpty(epg)) sb.Append("    \"epg\": \"").Append(Esc(epg)).Append("\",\n");
        sb.Append("    \"groups\": [");
        bool firstGroup = true;
        foreach (Group g in groups)
        {
            if (!firstGroup) sb.Append(",");
            firstGroup = false;
            sb.Append("\n      {\n        \"name\": \"").Append(Esc(g.Name)).Append("\",\n        \"channel\": [");
            bool firstCh = true;
            foreach (var kv in g.Chs)
            {
                if (!firstCh) sb.Append(",");
                firstCh = false;
                sb.Append("\n          {\n            \"name\": \"").Append(Esc(kv.Key)).Append("\",\n            \"urls\": [");
                bool firstUrl = true;
                foreach (string u in kv.Value)
                {
                    if (!firstUrl) sb.Append(",");
                    firstUrl = false;
                    sb.Append("\n              \"").Append(Esc(u)).Append("\"");
                }
                sb.Append("\n            ]\n          }");
            }
            sb.Append("\n        ]\n      }");
        }
        sb.Append("\n    ]\n  }]\n}");
        return sb.ToString();
    }
}
