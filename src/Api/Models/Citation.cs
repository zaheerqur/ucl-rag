namespace Api.Models;

/// <summary>
/// A single citation returned in an /ask response.
///
/// M4 matching convention: the gold set uses combined refs like "31.14(a)".
/// At eval time the runner forms this as: ArticleNumber + "." + ParagraphNumber.
/// </summary>
public record Citation(
    string ArticleNumber,
    string ParagraphNumber,
    string ArticleTitle,
    string Excerpt);
