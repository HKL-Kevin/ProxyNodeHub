namespace ProxyNodeHub;

public static class LanguageFilter
{
    /// <summary>仅接受中文或英文仓库; 其它语种返回 false</summary>
    public static bool IsAcceptable(string? description, string fullName)
    {
        var text = $"{description ?? ""} {fullName}";
        if (string.IsNullOrWhiteSpace(text)) return true;

        foreach (var ch in text)
        {
            int c = ch;
            if (c >= 0x0400 && c <= 0x04FF) return false;  // Cyrillic
            if (c >= 0x0370 && c <= 0x03FF) return false;  // Greek
            if (c >= 0x0590 && c <= 0x05FF) return false;  // Hebrew
            if (c >= 0x0600 && c <= 0x06FF) return false;  // Arabic
            if (c >= 0x0900 && c <= 0x097F) return false;  // Devanagari
            if (c >= 0x0E00 && c <= 0x0E7F) return false;  // Thai
            if (c >= 0x3040 && c <= 0x30FF) return false;  // Japanese kana
            if (c >= 0x1100 && c <= 0x11FF) return false;  // Hangul jamo
            if (c >= 0xAC00 && c <= 0xD7AF) return false;  // Hangul syllables
        }
        return true; // Latin + 中文 通过
    }
}
