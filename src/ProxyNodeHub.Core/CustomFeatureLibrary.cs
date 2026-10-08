using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ProxyNodeHub;

// ══════════════════════ 自定义特征码模型 ══════════════════════

/// <summary>规则类型</summary>
public enum FeatureRuleType
{
    FilePattern,     // 按文件名/扩展名/关键词匹配
    ContentPattern,  // 按内容特征匹配
    ExcludeRepo,     // 排除仓库
    RepoMapping      // 直接映射仓库路径
}

/// <summary>规则动作</summary>
public enum FeatureRuleAction
{
    ProbeAndCount,   // 下载探测并统计节点
    FollowLinks,     // 追踪链接
    Skip,            // 跳过该仓库
    DirectUse        // 直接使用已知路径
}

/// <summary>匹配条件</summary>
public class FeatureMatch
{
    public List<string> Extensions { get; set; } = new();
    public List<string> Keywords { get; set; } = new();
    public List<string> Exclude { get; set; } = new();
    public int MaxDepth { get; set; } = 5;
    public string? ContainsUrl { get; set; }
    public int MinLines { get; set; } = 0;
    public int MaxLines { get; set; } = 10000;
    public List<string> HasFiles { get; set; } = new();
    public List<string> ReadmeContains { get; set; } = new();
    public List<string> NotReadmeContains { get; set; } = new();
}

/// <summary>单条规则</summary>
public class FeatureRule
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
    public FeatureRuleType Type { get; set; } = FeatureRuleType.FilePattern;
    public FeatureRuleAction Action { get; set; } = FeatureRuleAction.ProbeAndCount;
    public FeatureMatch Match { get; set; } = new();
    public Dictionary<string, List<string>>? Repos { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>特征码包</summary>
public class FeaturePack
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0";
    public List<FeatureRule> Rules { get; set; } = new();
}

/// <summary>自定义特征库管理器</summary>
public static class CustomFeatureLibrary
{
    private static readonly string Path = System.IO.Path.Combine(
        AppEnvironment.UserDataDir, "custom_features.json");

    private static FeaturePack? _pack;
    private static bool _loaded = false;

    public static FeaturePack Pack
    {
        get
        {
            EnsureLoaded();
            return _pack ?? new FeaturePack { Name = "自定义特征库", Rules = new List<FeatureRule>() };
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        try
        {
            if (File.Exists(Path))
            {
                var json = File.ReadAllText(Path);
                _pack = JsonSerializer.Deserialize<FeaturePack>(json);
            }
        }
        catch { _pack = null; }
        _loaded = true;
    }

    public static void Save(FeaturePack pack)
    {
        _pack = pack;
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(Path, JsonSerializer.Serialize(pack, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static void Reset()
    {
        _pack = new FeaturePack { Name = "自定义特征库", Rules = new List<FeatureRule>() };
        Save(_pack);
    }

    /// <summary>解析并校验特征码 JSON</summary>
    public static (bool ok, string error, FeaturePack? pack) Import(string json)
    {
        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };
            var pack = JsonSerializer.Deserialize<FeaturePack>(json, options);
            if (pack == null) return (false, "无法解析 JSON", null);
            if (pack.Rules == null || pack.Rules.Count == 0) return (false, "没有规则", null);
            if (pack.Rules.Count > 100) return (false, "规则数超过限制 (最多100条)", null);

            for (int i = 0; i < pack.Rules.Count; i++)
            {
                var rule = pack.Rules[i];
                if (string.IsNullOrEmpty(rule.Id)) rule.Id = $"rule-{i + 1:D3}";
                if (string.IsNullOrEmpty(rule.Description)) rule.Description = $"规则 {i + 1}";
            }

            if (string.IsNullOrEmpty(pack.Name)) pack.Name = "导入的特征库";
            return (true, "", pack);
        }
        catch (Exception ex)
        {
            return (false, $"JSON 格式错误: {ex.Message}", null);
        }
    }

    /// <summary>获取特征库文档 (给 AI 的提示词)</summary>
    public static string GetDocument()
    {
        return @"# ProxyNodeHub 特征库文档

## 概述
ProxyNodeHub 是一个 GitHub 免费节点仓库监控工具。它通过特征库识别哪些仓库包含免费代理节点，以及订阅链接的位置。

## 内置特征 (默认，不可修改)
1. 已知仓库映射: 15+ 个仓库的直接路径
2. 文件名关键词: sub/node/v2ray/clash/proxy/shield/star/vip/mix 等
3. 内容探测: 检查 vmess:// vless:// trojan:// ss:// 协议
4. README 解析: 提取 raw.githubusercontent.com 链接
5. 非节点仓库排除: 检测 main.go/Dockerfile/Makefile 并排除

## 自定义特征码格式
请按以下 JSON 格式生成特征码:

```json
{
  ""name"": ""特征名称"",
  ""rules"": [
    {
      ""type"": ""repo_mapping | file_pattern | exclude_repo | content_pattern"",
      ""action"": ""direct_use | probe_and_count | skip | follow_links"",
      ""description"": ""规则描述"",
      ""match"": {
        ""extensions"": ["".txt""],
        ""keywords"": [""sub"", ""node""],
        ""exclude"": [""test""],
        ""hasFiles"": [""main.go""],
        ""readmeContains"": [""panel""],
        ""notReadmeContains"": [""订阅""]
      },
      ""repos"": {
        ""owner/repo"": [""path/to/file.txt""]
      }
    }
  ]
}
```

## 规则类型说明
- repo_mapping: 直接指定仓库的订阅文件路径 (action: direct_use)
- file_pattern: 按文件名/扩展名/关键词匹配 (action: probe_and_count)
- exclude_repo: 排除符合条件的仓库 (action: skip)
- content_pattern: 按文件内容特征匹配 (action: follow_links)

## 示例

### 示例1: 已知仓库映射
用户: ""idtheo 仓库有 shield.txt、star.txt、vip.txt 包含节点""

```json
{
  ""name"": ""idtheo 规则"",
  ""rules"": [
    {
      ""type"": ""repo_mapping"",
      ""action"": ""direct_use"",
      ""repos"": {
        ""idtheo/V2Ray-CleanIPs-Servers"": [""shield.txt"", ""star.txt"", ""vip.txt""]
      }
    }
  ]
}
```

### 示例2: 排除面板项目
用户: ""排除那些 Go 语言写的 VPN 面板项目""

```json
{
  ""name"": ""排除面板"",
  ""rules"": [
    {
      ""type"": ""exclude_repo"",
      ""action"": ""skip"",
      ""match"": {
        ""hasFiles"": [""main.go"", ""Dockerfile""],
        ""readmeContains"": [""panel"", ""management"", ""dashboard""],
        ""notReadmeContains"": [""订阅"", ""节点"", ""subscription"", ""free node""]
      }
    }
  ]
}
```

### 示例3: 文件特征匹配
用户: ""识别文件名包含 vip 或 premium 的 txt 文件""

```json
{
  ""name"": ""VIP 文件识别"",
  ""rules"": [
    {
      ""type"": ""file_pattern"",
      ""action"": ""probe_and_count"",
      ""match"": {
        ""extensions"": ["".txt""],
        ""keywords"": [""vip"", ""premium""],
        ""exclude"": [""test"", ""sample""]
      }
    }
  ]
}
```
";
    }
}
