namespace RcloneManager.Core.Models;

public sealed class FilterAnalysisResult
{
    public string FilePath { get; set; } = string.Empty;
    public bool FileExists { get; set; }
    public FilterRuleMode RecommendedMode { get; set; } = FilterRuleMode.FilterFrom;
    public bool HasIncludeRules { get; set; }
    public bool HasInvalidInlineMaxSize { get; set; }
    public string? ExtractedMaxSize { get; set; }
    public bool HasExclusionsAfterIncludes { get; set; }
    public List<string> Warnings { get; set; } = new();
    public string OptimizedContent { get; set; } = string.Empty;
}
