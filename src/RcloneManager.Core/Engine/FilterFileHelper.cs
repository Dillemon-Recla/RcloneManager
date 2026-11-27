using System.Text;
using System.Text.RegularExpressions;
using RcloneManager.Core.Models;

namespace RcloneManager.Core.Engine;

public static partial class FilterFileHelper
{
    [GeneratedRegex(@"^-\s*\*\s*\[\s*--\s*max-size\s+([^\]]+?)\s*\]\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex InlineMaxSizeRegex();

    public static FilterAnalysisResult Analyze(string filePath)
    {
        var result = new FilterAnalysisResult
        {
            FilePath = filePath,
            FileExists = !string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath)
        };

        if (!result.FileExists)
        {
            result.Warnings.Add("Arquivo de filtro não encontrado.");
            return result;
        }

        var lines = File.ReadAllLines(filePath);
        var hasIncludes = false;
        var hasExcludesPrefix = false;
        var hasBarePatterns = false;
        var seenInclude = false;
        var hasExclusionsAfterIncludes = false;
        var hasInvalidInlineMaxSize = false;
        string? extractedMaxSize = null;

        var topExclusions = new List<string>();
        var includesAndComments = new List<string>();
        var trailingComments = new List<string>();

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            if (trimmed.StartsWith('#') || trimmed.StartsWith(';'))
            {
                if (!seenInclude)
                    topExclusions.Add(rawLine);
                else
                    includesAndComments.Add(rawLine);
                continue;
            }

            var inlineMatch = InlineMaxSizeRegex().Match(trimmed);
            if (inlineMatch.Success)
            {
                hasInvalidInlineMaxSize = true;
                extractedMaxSize = inlineMatch.Groups[1].Value.Trim();
                result.Warnings.Add(
                    $"Regra inválida encontrada: '{trimmed}'. O Rclone não aceita [-- max-size ...] dentro do arquivo de filtro; deve ser '- *' e a flag '--max-size {extractedMaxSize}'.");
                continue;
            }

            if (trimmed.StartsWith("+ ", StringComparison.Ordinal))
            {
                hasIncludes = true;
                seenInclude = true;
                includesAndComments.Add(trimmed);
                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                hasExcludesPrefix = true;
                if (trimmed == "- *")
                {
                    trailingComments.Add(trimmed);
                }
                else
                {
                    if (seenInclude)
                        hasExclusionsAfterIncludes = true;
                    topExclusions.Add(trimmed);
                }
                continue;
            }

            // Linha sem prefixo '+ ' ou '- ' (sintaxe de exclude-from misturada em filter-from)
            hasBarePatterns = true;
            if (seenInclude)
                hasExclusionsAfterIncludes = true;
            topExclusions.Add("- " + trimmed);
        }

        if (hasIncludes && hasBarePatterns)
        {
            result.Warnings.Add(
                "O arquivo mistura regras '+ ...' (filter-from) com padrões sem prefixo '- ' (exclude-from). Em --filter-from, exclusões precisam começar com '- '.");
        }

        if (hasExclusionsAfterIncludes)
        {
            result.Warnings.Add(
                "Exclusões de pastas/sistema estão posicionadas APÓS regras '+ **.ext'. No Rclone, a primeira regra correspondente vence (first-match-wins), fazendo arquivos de AppData/.gemini/Downloads serem incluídos antes da exclusão.");
        }

        result.HasIncludeRules = hasIncludes;
        result.HasInvalidInlineMaxSize = hasInvalidInlineMaxSize;
        result.ExtractedMaxSize = extractedMaxSize;
        result.HasExclusionsAfterIncludes = hasExclusionsAfterIncludes;
        result.RecommendedMode = (hasIncludes || hasExcludesPrefix || hasInvalidInlineMaxSize)
            ? FilterRuleMode.FilterFrom
            : FilterRuleMode.ExcludeFrom;

        result.OptimizedContent = BuildOptimizedContent(topExclusions, includesAndComments, extractedMaxSize, hasIncludes || hasInvalidInlineMaxSize);
        return result;
    }

    private static string BuildOptimizedContent(
        List<string> topExclusions,
        List<string> includesAndComments,
        string? extractedMaxSize,
        bool addCatchAllExclude)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine("# Rclone --filter-from (Otimizado pelo RcloneManager)");
        sb.AppendLine("# Ordem importa (First-Match-Wins): Exclusões críticas primeiro, includes depois.");
        if (!string.IsNullOrWhiteSpace(extractedMaxSize))
            sb.AppendLine($"# Nota: Limite de tamanho '--max-size {extractedMaxSize}' deve ser configurado na tarefa.");
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine();

        var seenRules = new HashSet<string>(StringComparer.Ordinal);
        sb.AppendLine("# 1. EXCLUSÕES PRIORITÁRIAS (Sistema, Ocultas, Downloads e Temporários)");
        foreach (var item in topExclusions)
        {
            var trimmed = item.Trim();
            if (trimmed.StartsWith('#') || trimmed.StartsWith(';'))
                continue;

            // Garante que - **/.*/** também cubra pastas/arquivos ocultos na raiz (- .*/** e - .*)
            if (trimmed is "- **/.*/**" or "- **/.*")
            {
                if (seenRules.Add("- .*/**"))
                    sb.AppendLine("- .*/**");
                if (seenRules.Add("- .*"))
                    sb.AppendLine("- .*");
            }

            if (seenRules.Add(trimmed))
                sb.AppendLine(trimmed);
        }

        sb.AppendLine();
        sb.AppendLine("# 2. ARQUIVOS PERMITIDOS (Mídias, Documentos, Códigos e Utilitários)");
        foreach (var item in includesAndComments)
        {
            var trimmed = item.Trim();
            if (trimmed.StartsWith('#') && (trimmed.Contains("EXCLUS", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("BLOQUEAR", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("EXCLUIR", StringComparison.OrdinalIgnoreCase)))
                continue;

            if (trimmed.StartsWith('#') || trimmed.StartsWith(';'))
            {
                sb.AppendLine(trimmed);
                continue;
            }

            if (seenRules.Add(trimmed))
                sb.AppendLine(trimmed);
        }

        if (addCatchAllExclude)
        {
            sb.AppendLine();
            sb.AppendLine("# 3. BLOQUEAR TODO O RESTANTE NÃO LISTADO ACIMA");
            sb.AppendLine("- *");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Resolve qual flag (--filter-from ou --exclude-from) e qual arquivo usar em tempo de execução.
    /// Caso o arquivo tenha sintaxe incompatível com --filter-from, gera uma cópia temporária sanitizada
    /// para garantir execução segura sem alterar o arquivo original sem consentimento explícito.
    /// </summary>
    public static (string FlagName, string EffectiveFilePath, string? AutoDetectedMaxSize, List<string> Notes) ResolveForExecution(
        string filterFilePath,
        FilterRuleMode mode,
        string workingDirectory)
    {
        var notes = new List<string>();
        var analysis = Analyze(filterFilePath);
        if (!analysis.FileExists)
            return ("--exclude-from", filterFilePath, null, notes);

        var chosenMode = mode == FilterRuleMode.AutoDetect ? analysis.RecommendedMode : mode;
        var flagName = chosenMode == FilterRuleMode.ExcludeFrom ? "--exclude-from" : "--filter-from";

        if (chosenMode == FilterRuleMode.FilterFrom && analysis.Warnings.Count > 0)
        {
            var runtimeDir = Path.Combine(workingDirectory, ".rclone-manager-runtime");
            Directory.CreateDirectory(runtimeDir);
            var sanitizedPath = Path.Combine(runtimeDir, Path.GetFileNameWithoutExtension(filterFilePath) + ".sanitized.filter");
            File.WriteAllText(sanitizedPath, analysis.OptimizedContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            notes.Add($"Filtro sanitizado automaticamente para --filter-from em '{sanitizedPath}' ({analysis.Warnings.Count} ajuste(s) aplicado(s)).");
            return (flagName, sanitizedPath, analysis.ExtractedMaxSize, notes);
        }

        return (flagName, filterFilePath, analysis.ExtractedMaxSize, notes);
    }

    public static FilterAnalysisResult ApplyOptimizationWithBackup(string filePath)
    {
        var analysis = Analyze(filePath);
        if (!analysis.FileExists)
            return analysis;

        var backupPath = filePath + ".bak";
        if (!File.Exists(backupPath))
        {
            File.Copy(filePath, backupPath, overwrite: false);
        }
        else
        {
            var timestampedBak = $"{filePath}.{DateTime.Now:yyyyMMdd_HHmmss}.bak";
            File.Copy(filePath, timestampedBak, overwrite: true);
        }

        File.WriteAllText(filePath, analysis.OptimizedContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return Analyze(filePath);
    }

    /// <summary>
    /// Constrói um arquivo --filter-from invertido (complemento booleano exato First-Match-Wins)
    /// que seleciona APENAS arquivos no destino que hoje estão EXCLUÍDOS pelo filtro original.
    /// </summary>
    public static string? BuildInvertedExcludedOnlyFilterContent(string filterFilePath, FilterRuleMode mode)
    {
        var analysis = Analyze(filterFilePath);
        if (!analysis.FileExists)
            return null;

        var chosenMode = mode == FilterRuleMode.AutoDetect ? analysis.RecommendedMode : mode;
        var sb = new StringBuilder();
        sb.AppendLine("# Filtro invertido automático (seleciona apenas itens excluídos pelo filtro principal)");

        if (chosenMode == FilterRuleMode.ExcludeFrom)
        {
            var rawLines = File.ReadAllLines(filterFilePath);
            var addedAny = false;
            foreach (var raw in rawLines)
            {
                var trimmed = raw.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#') || trimmed.StartsWith(';'))
                    continue;
                if (InlineMaxSizeRegex().IsMatch(trimmed))
                    continue;

                var pattern = trimmed.StartsWith("- ", StringComparison.Ordinal)
                    ? trimmed[2..].Trim()
                    : (trimmed.StartsWith("+ ", StringComparison.Ordinal) ? string.Empty : trimmed);

                if (string.IsNullOrEmpty(pattern))
                    continue;

                sb.AppendLine("+ " + pattern);
                addedAny = true;
            }

            if (!addedAny)
                return null;

            sb.AppendLine("- *");
            return sb.ToString();
        }

        var sourceContent = analysis.Warnings.Count > 0
            ? analysis.OptimizedContent
            : File.ReadAllText(filterFilePath);

        var lines = sourceContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var hasExplicitCatchAll = false;
        var ruleCount = 0;

        foreach (var raw in lines)
        {
            var trimmed = raw.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#') || trimmed.StartsWith(';'))
                continue;
            if (InlineMaxSizeRegex().IsMatch(trimmed))
                continue;

            if (trimmed == "- *")
            {
                sb.AppendLine("+ *");
                hasExplicitCatchAll = true;
                ruleCount++;
            }
            else if (trimmed == "+ *")
            {
                sb.AppendLine("- *");
                hasExplicitCatchAll = true;
                ruleCount++;
            }
            else if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                sb.AppendLine("+ " + trimmed[2..]);
                ruleCount++;
            }
            else if (trimmed.StartsWith("+ ", StringComparison.Ordinal))
            {
                sb.AppendLine("- " + trimmed[2..]);
                ruleCount++;
            }
        }

        if (ruleCount == 0)
            return null;

        // Em --filter-from, a regra implícita final do Rclone é '+ *' (incluir o restante);
        // portanto no filtro invertido o restante não listado deve ser protegido ('- *').
        if (!hasExplicitCatchAll)
            sb.AppendLine("- *");

        return sb.ToString();
    }
}
