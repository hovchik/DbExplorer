namespace DbExplorer.Application.Updates;

/// <summary>
/// A release version as the installer build numbers them: "1.2.0", "1.2.0.1" or "1.2.0-rc1" (a tag may start with
/// "v"; a "+commit" build suffix is ignored). Compared like SemVer: numbers first, then a pre-release ("-rc1") is older
/// than the release with the same numbers, and pre-release labels compare part by part (numeric parts as numbers).
/// </summary>
public sealed class AppVersion : IComparable<AppVersion>, IEquatable<AppVersion>
{
    private readonly int[] _numbers;
    private readonly string[] _preRelease;

    private AppVersion(int[] numbers, string[] preRelease, string text)
    {
        _numbers = numbers;
        _preRelease = preRelease;
        Text = text;
    }

    /// <summary>The version as written, without "v" or a build suffix, e.g. "1.2.0-rc1".</summary>
    public string Text { get; }

    public bool IsPreRelease => _preRelease.Length > 0;

    /// <summary>A build from a pull request or a manual run, numbered 0.0.&lt;run&gt; so it never looks like a release.</summary>
    public bool IsTestBuild => _numbers[0] == 0 && (_numbers.Length < 2 || _numbers[1] == 0);

    public static AppVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"'{text}' is not a version like 1.2.0 or 1.2.0-rc1.");

    public static bool TryParse(string? text, out AppVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        var dash = s.IndexOf('-');
        var core = dash >= 0 ? s[..dash] : s;
        var pre = dash >= 0 ? s[(dash + 1)..] : "";
        if (dash >= 0 && pre.Length == 0) return false;

        var parts = core.Split('.');
        if (parts.Length is < 1 or > 4) return false;
        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit) || !int.TryParse(parts[i], out numbers[i])) return false;
        }

        var labels = pre.Length == 0 ? [] : pre.Split('.');
        if (labels.Any(l => l.Length == 0 || !l.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))) return false;
        version = new AppVersion(numbers, labels, s);
        return true;
    }

    public int CompareTo(AppVersion? other)
    {
        if (other is null) return 1;
        for (var i = 0; i < Math.Max(_numbers.Length, other._numbers.Length); i++)
        {
            var a = i < _numbers.Length ? _numbers[i] : 0;
            var b = i < other._numbers.Length ? other._numbers[i] : 0;
            if (a != b) return a.CompareTo(b);
        }

        // Same numbers: the release is newer than any of its pre-releases.
        if (_preRelease.Length == 0 || other._preRelease.Length == 0) return other._preRelease.Length.CompareTo(_preRelease.Length);
        for (var i = 0; i < Math.Min(_preRelease.Length, other._preRelease.Length); i++)
        {
            var c = CompareLabel(_preRelease[i], other._preRelease[i]);
            if (c != 0) return c;
        }

        return _preRelease.Length.CompareTo(other._preRelease.Length);
    }

    /// <summary>"rc2" &lt; "rc10": letters compare as text and the trailing number as a number; plain numbers sort
    /// before words, as in SemVer.</summary>
    private static int CompareLabel(string a, string b)
    {
        var aNumeric = a.All(char.IsAsciiDigit);
        var bNumeric = b.All(char.IsAsciiDigit);
        if (aNumeric && bNumeric) return CompareDigits(a, b);
        if (aNumeric != bNumeric) return aNumeric ? -1 : 1;

        var (aWord, aNumber) = Split(a);
        var (bWord, bNumber) = Split(b);
        var word = string.Compare(aWord, bWord, StringComparison.OrdinalIgnoreCase);
        if (word != 0) return word;
        return CompareDigits(aNumber, bNumber);

        static (string Word, string Number) Split(string label)
        {
            var end = label.Length;
            while (end > 0 && char.IsAsciiDigit(label[end - 1])) end--;
            return (label[..end], label[end..]);
        }
    }

    private static int CompareDigits(string a, string b)
    {
        a = a.TrimStart('0');
        b = b.TrimStart('0');
        return a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
    }

    public bool Equals(AppVersion? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is AppVersion v && Equals(v);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        var length = _numbers.Length;
        while (length > 1 && _numbers[length - 1] == 0) length--;
        for (var i = 0; i < length; i++) hash.Add(_numbers[i]);
        foreach (var label in _preRelease) hash.Add(label.ToLowerInvariant().TrimStart('0'));
        return hash.ToHashCode();
    }
    public override string ToString() => Text;

    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;
}
