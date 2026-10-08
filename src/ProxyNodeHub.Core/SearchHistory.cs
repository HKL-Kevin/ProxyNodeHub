using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ProxyNodeHub;

/// <summary>
/// 搜索历史记录 - 用于过滤已搜索过的仓库
/// </summary>
public class SearchRecord
{
    public List<RepoEntry> Repos { get; set; } = new();
    public int SaveDays { get; set; } = 1;
}

public class RepoEntry
{
    public string FullName { get; set; } = "";
    public DateTime ExpiryDate { get; set; }
}

/// <summary>
/// 搜索历史管理器
/// </summary>
public static class SearchHistory
{
    private static readonly string FilePath = Path.Combine(
        AppEnvironment.UserDataDir, "searched_repos.json");

    private static SearchRecord _record = null!;
    private static readonly object _lock = new();

    /// <summary>加载搜索记录</summary>
    public static SearchRecord Load()
    {
        if (_record != null) return _record;

        lock (_lock)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    _record = JsonSerializer.Deserialize<SearchRecord>(json) ?? new SearchRecord();
                }
            }
            catch { }

            _record ??= new SearchRecord();
            return _record;
        }
    }

    /// <summary>保存搜索记录到文件</summary>
    public static void Save()
    {
        lock (_lock)
        {
            try
            {
                var json = JsonSerializer.Serialize(_record, new JsonSerializerOptions { WriteIndented = true });
                AtomicFile.Write(FilePath, json);
            }
            catch { }
        }
    }

    /// <summary>添加仓库到记录</summary>
    public static void AddRepos(List<string> repoNames, int saveDays)
    {
        var record = Load();
        record.SaveDays = saveDays;
        var expiryDate = DateTime.Now.AddDays(saveDays);

        foreach (var name in repoNames)
        {
            if (!record.Repos.Any(r => r.FullName == name))
            {
                record.Repos.Add(new RepoEntry
                {
                    FullName = name,
                    ExpiryDate = expiryDate
                });
            }
        }

        CleanupExpired();
        Save();
    }

    /// <summary>检查仓库是否已被记录且未过期</summary>
    public static bool IsExcluded(string fullName)
    {
        var record = Load();
        var entry = record.Repos.FirstOrDefault(r => r.FullName == fullName);
        return entry != null && entry.ExpiryDate > DateTime.Now;
    }

    /// <summary>清除所有记录</summary>
    public static void Clear()
    {
        lock (_lock)
        {
            _record = new SearchRecord();
            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
            catch { }
        }
    }

    /// <summary>清理过期记录</summary>
    public static void CleanupExpired()
    {
        var record = Load();
        record.Repos.RemoveAll(r => r.ExpiryDate <= DateTime.Now);
        Save();
    }

    /// <summary>获取排除的仓库数量</summary>
    public static int GetExcludedCount()
    {
        var record = Load();
        return record.Repos.Count(r => r.ExpiryDate > DateTime.Now);
    }
}
