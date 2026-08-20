using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using dbclient.IntelliSense.Models;
using dbclient.IntelliSense.Interfaces;

namespace dbclient.IntelliSense.Parsing;

public class SqlParser : ISqlParser
{
    private static readonly Regex TokenRegex = new(
        @"\b\w+\b|[.,();]|'[^']*'|""[^""]*""|\[[^\]]*\]|--[^\r\n]*|/\*[\s\S]*?\*/",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> SelectKeywords = new(StringComparer.OrdinalIgnoreCase)
        { "SELECT", "DISTINCT", "TOP" };

    private static readonly HashSet<string> FromKeywords = new(StringComparer.OrdinalIgnoreCase)
        { "FROM", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "CROSS" };

    private static readonly HashSet<string> WhereKeywords = new(StringComparer.OrdinalIgnoreCase)
        { "WHERE", "ON", "HAVING" };

    public SqlContext AnalyzeContext(string sqlText, int caretPosition)
    {
        if (string.IsNullOrEmpty(sqlText) || caretPosition < 0)
            return new SqlContext { Type = SqlContextType.Unknown };

        try
        {
            var dotContext = CheckForDotNotation(sqlText, caretPosition);
            if (dotContext != null)
                return dotContext;

            var sqlTextBeforeCursor = sqlText[..Math.Min(caretPosition, sqlText.Length)];
            var tokens = TokenizeSQL(sqlTextBeforeCursor).ToList();

            if (tokens.Count == 0)
                return new SqlContext { Type = SqlContextType.General };

            var queryTokens = GetCurrentQueryTokens(tokens);

            if (queryTokens.Count == 0)
                return new SqlContext { Type = SqlContextType.General };

            // Whether the caret sits immediately after an identifier character — i.e. the user is
            // still typing the last token ("FROM Cust|") vs. done with it ("FROM Customers |").
            var caretAtWordEnd = caretPosition > 0 && caretPosition <= sqlText.Length &&
                (char.IsLetterOrDigit(sqlText[caretPosition - 1]) || sqlText[caretPosition - 1] == '_');

            var context = DetermineContext(queryTokens, caretAtWordEnd);
            context.CurrentQuery = string.Join(" ", queryTokens);
            context.AllTokens = queryTokens;

            // Extract aliases from the FULL query text (including after cursor)
            // so that SELECT columns get completions from tables in the FROM clause
            var fullTokens = TokenizeSQL(sqlText).ToList();
            var fullQueryTokens = GetCurrentQueryTokens(fullTokens);
            var fullAliases = ExtractTableAliasesFromTokens(fullQueryTokens);
            foreach (var alias in fullAliases)
                context.TableAliases[alias.Key] = alias.Value;
            context.AvailableAliases = context.TableAliases.Keys.ToList();

            return context;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SqlParser.AnalyzeContext failed: {ex.Message}");
            return new SqlContext { Type = SqlContextType.General };
        }
    }

    private static SqlContext? CheckForDotNotation(string sqlText, int caretPosition)
    {
        int checkPosition = caretPosition - 1;
        while (checkPosition >= 0 && char.IsWhiteSpace(sqlText[checkPosition]))
            checkPosition--;

        int identifierEnd = checkPosition + 1;
        while (checkPosition >= 0 && (char.IsLetterOrDigit(sqlText[checkPosition]) || sqlText[checkPosition] == '_'))
            checkPosition--;

        if (checkPosition >= 0 && sqlText[checkPosition] == '.')
        {
            var tablePrefix = ExtractQualifiedIdentifierBefore(sqlText, checkPosition);

            // "1." is a decimal literal being typed, not a column reference
            if (!string.IsNullOrEmpty(tablePrefix) && !tablePrefix.All(char.IsDigit))
            {
                var allQueryTokens = TokenizeSQL(sqlText).ToList();
                allQueryTokens.Insert(0, "SELECT");

                var context = new SqlContext
                {
                    Type = SqlContextType.ColumnAfterDot,
                    TablePrefix = tablePrefix,
                    ExpectingColumnName = true,
                    TableAliases = ExtractTableAliasesFromTokens(allQueryTokens),
                };
                context.AvailableAliases = context.TableAliases.Keys.ToList();
                return context;
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts the (possibly schema-qualified) identifier ending at <paramref name="dotPosition"/>,
    /// e.g. "dbo.Orders." → "dbo.Orders". Handles [bracketed], "quoted" and `backticked` parts.
    /// </summary>
    private static string ExtractQualifiedIdentifierBefore(string sqlText, int dotPosition)
    {
        var identifier = new StringBuilder();

        int i = dotPosition - 1;
        while (i >= 0)
        {
            char c = sqlText[i];

            if (char.IsLetterOrDigit(c) || c == '_')
            {
                identifier.Insert(0, c);
                i--;
            }
            else if (c == ']' || c == '`' || c == '"')
            {
                // Quoted identifier part: copy its contents without the delimiters
                char open = c == ']' ? '[' : c;
                i--;
                while (i >= 0 && sqlText[i] != open)
                {
                    identifier.Insert(0, sqlText[i]);
                    i--;
                }
                i--; // skip the opening delimiter
            }
            else if (c == '.' && identifier.Length > 0)
            {
                // Continue into the schema/qualifier part
                identifier.Insert(0, '.');
                i--;
            }
            else
            {
                break;
            }
        }

        return identifier.ToString();
    }

    private static IEnumerable<string> TokenizeSQL(string sqlText)
    {
        var matches = TokenRegex.Matches(sqlText);
        return matches.Cast<Match>()
                     .Select(m => m.Value.Trim())
                     .Where(token => !string.IsNullOrEmpty(token) &&
                                   !token.StartsWith("--") &&
                                   !token.StartsWith("/*"));
    }

    private static List<string> GetCurrentQueryTokens(List<string> allTokens)
    {
        var queryTokens = new List<string>();
        var parenthesesDepth = 0;
        var startIndex = 0;

        for (int i = allTokens.Count - 1; i >= 0; i--)
        {
            var token = allTokens[i];

            if (token == ")")
                parenthesesDepth++;
            else if (token == "(")
                parenthesesDepth--;
            else if (parenthesesDepth == 0)
            {
                if (token == ";" || IsStatementStart(token))
                {
                    if (IsStatementStart(token))
                        startIndex = i;
                    else if (token == ";")
                        startIndex = i + 1;
                    break;
                }
            }
        }

        for (int i = startIndex; i < allTokens.Count; i++)
        {
            var token = allTokens[i];
            if (token != ";")
                queryTokens.Add(token);
        }

        return queryTokens;
    }

    private static bool IsStatementStart(string token)
    {
        var upperToken = token.ToUpperInvariant();
        return upperToken is "SELECT" or "INSERT" or "UPDATE" or "DELETE" or "CREATE" or "ALTER" or "DROP";
    }

    private static SqlContext DetermineContext(List<string> tokens, bool caretAtWordEnd)
    {
        var context = new SqlContext { Type = SqlContextType.General };

        if (tokens.Count == 0)
            return context;

        context.TableAliases = ExtractTableAliasesFromTokens(tokens);
        context.AvailableAliases = context.TableAliases.Keys.ToList();

        var lastToken = tokens.Last();
        if (lastToken.EndsWith('.'))
        {
            context.Type = SqlContextType.ColumnAfterDot;
            context.TablePrefix = lastToken.TrimEnd('.');
            context.ExpectingColumnName = true;
            return context;
        }

        if (lastToken == "." && tokens.Count >= 2)
        {
            var secondLastToken = tokens[^2];
            context.Type = SqlContextType.ColumnAfterDot;
            context.TablePrefix = secondLastToken;
            context.ExpectingColumnName = true;
            return context;
        }

        var currentToken = lastToken.ToUpperInvariant();

        if (tokens.Count == 1)
        {
            // Only treat the word as a partially typed keyword while the caret still touches it —
            // "SELEC|" is partial, but after "SELECT |" the clause context below must decide.
            if (caretAtWordEnd && IsPartialKeyword(currentToken))
            {
                context.Type = SqlContextType.General;
                context.ExpectingKeyword = true;
                return context;
            }
        }

        var allQueryTokens = tokens.ToList();
        allQueryTokens.Insert(0, "SELECT");
        var globalAliases = ExtractTableAliasesFromTokens(allQueryTokens);
        context.TableAliases = globalAliases;
        context.AvailableAliases = globalAliases.Keys.ToList();

        for (int i = tokens.Count - 1; i >= 0; i--)
        {
            var token = tokens[i].ToUpperInvariant();

            if (token == "SELECT")
            {
                // Note: the backward scan hits any FROM/JOIN/WHERE token before reaching SELECT,
                // so at this point there is no clause keyword between SELECT and the caret.
                var tokensAfterSelect = tokens.Skip(i + 1).ToList();

                context.Type = SqlContextType.SelectList;
                context.LastKeyword = "SELECT";

                if (tokensAfterSelect.Count == 0)
                {
                    context.ExpectingColumnName = true;
                }
                else
                {
                    var lastTokenAfterSelect = tokensAfterSelect.Last().ToUpperInvariant();

                    if (IsPartialFromKeyword(lastTokenAfterSelect) ||
                        tokensAfterSelect.Contains("*") ||
                        !IsPartialWord(tokensAfterSelect[0]))
                    {
                        context.ExpectingKeyword = true;
                    }
                    else
                    {
                        context.ExpectingColumnName = true;
                    }
                }
                break;
            }
            else if ((token == "GROUP" || token == "ORDER") && i >= tokens.Count - 2)
            {
                // "... GROUP |" or "... ORDER B|" — the only valid continuation is BY.
                // Skip when the word sits in table position (it's a table named Group/Order there).
                var prev = i > 0 ? tokens[i - 1].ToUpperInvariant() : "";
                var tablePosition = prev == "FROM" || prev.EndsWith("JOIN") || prev == "," || prev == ".";
                var partialAfter = i == tokens.Count - 1 ||
                    "BY".StartsWith(tokens[^1], StringComparison.OrdinalIgnoreCase);

                if (!tablePosition && partialAfter)
                {
                    context.Type = SqlContextType.AfterGroupOrOrder;
                    context.ExpectingKeyword = true;
                    context.LastKeyword = token;
                    break;
                }
            }
            else if (FromKeywords.Contains(token))
            {
                if (token != "FROM" && token != "JOIN")
                {
                    // INNER/LEFT/RIGHT/FULL/CROSS — a JOIN keyword should follow, not a table
                    context.Type = SqlContextType.AfterTableName;
                    context.ExpectingKeyword = true;
                    context.LastKeyword = token;
                    break;
                }

                var afterFrom = tokens.Skip(i + 1).ToList();
                var fromContext = AnalyzeFromClauseTokens(afterFrom, caretAtWordEnd);
                context.Type = fromContext.Type;
                context.ExpectingTableName = fromContext.ExpectingTableName;
                context.ExpectingKeyword = fromContext.ExpectingKeyword;
                context.LastKeyword = token;
                break;
            }
            else if (token == "BY" && i > 0 &&
                     (tokens[i - 1].Equals("ORDER", StringComparison.OrdinalIgnoreCase) ||
                      tokens[i - 1].Equals("GROUP", StringComparison.OrdinalIgnoreCase)))
            {
                context.Type = SqlContextType.WhereClause;
                context.ExpectingColumnName = true;
                context.LastKeyword = $"{tokens[i - 1].ToUpperInvariant()} BY";
                break;
            }
            else if (WhereKeywords.Contains(token))
            {
                if (token == "ON" && IsAfterJoin(tokens, i))
                {
                    context.Type = SqlContextType.JoinCondition;
                    context.ExpectingColumnName = true;
                    context.LastKeyword = token;
                }
                else
                {
                    context.Type = SqlContextType.WhereClause;
                    context.ExpectingColumnName = true;
                    context.LastKeyword = token;
                }
                break;
            }
            else if (token == "INSERT")
            {
                if (i + 1 < tokens.Count && tokens[i + 1].Equals("INTO", StringComparison.OrdinalIgnoreCase))
                {
                    context.Type = SqlContextType.InsertInto;
                    context.ExpectingTableName = true;
                }
                break;
            }
            else if (token == "UPDATE")
            {
                context.Type = SqlContextType.UpdateTable;
                context.ExpectingTableName = true;
                break;
            }
        }

        return context;
    }

    private static bool IsPartialKeyword(string token)
    {
        string[] commonKeywords = ["SELECT", "FROM", "WHERE", "INSERT", "UPDATE", "DELETE", "CREATE", "ALTER", "DROP"];
        return commonKeywords.Any(keyword => keyword.StartsWith(token, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPartialWord(string token)
    {
        string[] completeKeywords = ["SELECT", "FROM", "WHERE", "INSERT", "UPDATE", "DELETE", "AND", "OR", "JOIN", "*"];
        return !completeKeywords.Contains(token.ToUpperInvariant());
    }

    private static bool IsPartialFromKeyword(string token)
    {
        return "FROM".StartsWith(token, StringComparison.OrdinalIgnoreCase) && token.Length < 4;
    }

    /// <summary>
    /// Classifies the caret position given the tokens between the last FROM/JOIN keyword and the
    /// caret: still naming a table (FromClause), or done and expecting a keyword
    /// (AfterTableName/AfterTableAlias — JOIN, WHERE, GROUP BY, ...).
    /// </summary>
    private static SqlContext AnalyzeFromClauseTokens(List<string> afterFrom, bool caretAtWordEnd)
    {
        var context = new SqlContext { Type = SqlContextType.FromClause, ExpectingTableName = true };

        if (afterFrom.Count == 0)
            return context;

        var idx = 0;
        var lastRefHasAlias = false;

        while (idx < afterFrom.Count)
        {
            if (!TryParseQualifiedName(afterFrom, ref idx, out _, out _, out var endsWithDot) || endsWithDot)
                return context; // dangling comma/dot or unparsable (e.g. subquery paren) — expect a table

            lastRefHasAlias = false;

            if (idx < afterFrom.Count && afterFrom[idx].Equals("AS", StringComparison.OrdinalIgnoreCase))
            {
                idx++;
                if (idx >= afterFrom.Count)
                {
                    // "FROM t AS |" — user is about to type a new alias name; nothing useful to suggest
                    context.Type = SqlContextType.General;
                    context.ExpectingTableName = false;
                    return context;
                }
                if (IsIdentifierToken(afterFrom[idx]))
                {
                    lastRefHasAlias = true;
                    idx++;
                }
            }
            else if (idx < afterFrom.Count && IsIdentifierToken(afterFrom[idx]))
            {
                lastRefHasAlias = true;
                idx++;
            }

            if (idx < afterFrom.Count && afterFrom[idx] == ",")
            {
                idx++;
                if (idx >= afterFrom.Count)
                    return context; // "FROM a, |" — next table expected
                continue;
            }
            break;
        }

        if (idx < afterFrom.Count)
        {
            // "FROM Customers c WH|" — a lone identifier being typed after table + alias is a
            // partially typed keyword; suggest keywords and let the prefix filter narrow them.
            if (caretAtWordEnd && idx == afterFrom.Count - 1 && lastRefHasAlias && IsIdentifierToken(afterFrom[^1]))
            {
                context.Type = SqlContextType.AfterTableAlias;
                context.ExpectingTableName = false;
                context.ExpectingKeyword = true;
                return context;
            }
            return context; // unparsed remainder — fall back to table expectation
        }

        if (caretAtWordEnd)
        {
            // Caret still touches the last identifier. If that identifier is the table name itself,
            // the user is typing the table; if it parsed as an "alias", it is more likely a partially
            // typed keyword (GRO|, WH|) — suggest keywords and let the prefix filter decide.
            if (!lastRefHasAlias)
                return context;

            context.Type = SqlContextType.AfterTableName;
        }
        else
        {
            context.Type = lastRefHasAlias ? SqlContextType.AfterTableAlias : SqlContextType.AfterTableName;
        }

        context.ExpectingTableName = false;
        context.ExpectingKeyword = true;
        return context;
    }

    /// <summary>
    /// Parses a possibly schema-qualified identifier ("dbo.Orders", "[dbo].[Orders]") starting at
    /// <paramref name="i"/>, advancing it past the consumed tokens. <paramref name="endsWithDot"/>
    /// is true for a trailing dot with nothing after it ("dbo.").
    /// </summary>
    private static bool TryParseQualifiedName(List<string> tokens, ref int i, out string fullName, out string bareName, out bool endsWithDot)
    {
        fullName = string.Empty;
        bareName = string.Empty;
        endsWithDot = false;

        if (i >= tokens.Count || !IsIdentifierToken(tokens[i]))
            return false;

        var parts = new List<string> { CleanIdentifier(tokens[i]) };
        i++;

        while (i < tokens.Count && tokens[i] == ".")
        {
            if (i + 1 < tokens.Count && IsIdentifierToken(tokens[i + 1]))
            {
                parts.Add(CleanIdentifier(tokens[i + 1]));
                i += 2;
            }
            else
            {
                i++;
                endsWithDot = true;
                break;
            }
        }

        fullName = string.Join(".", parts);
        bareName = parts[^1];
        return true;
    }

    private static bool IsIdentifierToken(string token)
    {
        if (string.IsNullOrEmpty(token) || IsKeyword(token))
            return false;
        var c = token[0];
        return char.IsLetter(c) || c == '_' || c == '[' || c == '"' || c == '`';
    }

    public IList<string> ExtractTableNames(string sqlText)
    {
        var tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tokens = TokenizeSQL(sqlText).ToList();

        for (int i = 0; i < tokens.Count - 1; i++)
        {
            var token = tokens[i].ToUpperInvariant();
            if (token == "FROM" || token.EndsWith("JOIN"))
            {
                var j = i + 1;
                if (TryParseQualifiedName(tokens, ref j, out var fullName, out _, out var endsWithDot) && !endsWithDot)
                    tableNames.Add(fullName);
            }
        }

        return tableNames.ToList();
    }

    public IDictionary<string, string> ExtractTableAliases(string sqlText)
    {
        var tokens = TokenizeSQL(sqlText).ToList();
        return ExtractTableAliasesFromTokens(tokens);
    }

    private static Dictionary<string, string> ExtractTableAliasesFromTokens(List<string> tokens)
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i].ToUpperInvariant();

            if (token != "FROM" && !token.EndsWith("JOIN"))
                continue;

            var j = i + 1;
            while (true)
            {
                if (!TryParseQualifiedName(tokens, ref j, out var fullName, out var bareName, out var endsWithDot) || endsWithDot)
                    break;

                string? alias = null;
                if (j < tokens.Count && tokens[j].Equals("AS", StringComparison.OrdinalIgnoreCase))
                {
                    if (j + 1 < tokens.Count && IsIdentifierToken(tokens[j + 1]))
                    {
                        alias = CleanIdentifier(tokens[j + 1]);
                        j += 2;
                    }
                    else
                    {
                        j++;
                    }
                }
                else if (j < tokens.Count && IsIdentifierToken(tokens[j]))
                {
                    alias = CleanIdentifier(tokens[j]);
                    j++;
                }

                var key = alias ?? bareName;
                if (!aliases.ContainsKey(key))
                    aliases[key] = fullName;

                // "FROM a, b c, d" — keep consuming comma-separated table refs
                if (j < tokens.Count && tokens[j] == ",")
                {
                    j++;
                    continue;
                }
                break;
            }

            i = j - 1;
        }

        return aliases;
    }

    private static bool IsKeyword(string token)
    {
        var upperToken = token.ToUpperInvariant();
        return SelectKeywords.Contains(upperToken) ||
               FromKeywords.Contains(upperToken) ||
               WhereKeywords.Contains(upperToken) ||
               upperToken is "WHERE" or "ORDER" or "GROUP" or "HAVING" or "LIMIT" or "INTO" or "VALUES" or "SET" or "BY";
    }

    private static bool IsAfterJoin(List<string> tokens, int onPosition)
    {
        for (int i = onPosition - 1; i >= 0; i--)
        {
            var token = tokens[i].ToUpperInvariant();

            if (FromKeywords.Contains(token) && token.Contains("JOIN"))
                return true;

            if (token is "SELECT" or "WHERE" or "HAVING" or "INSERT" or "UPDATE")
                return false;
        }

        return false;
    }

    private static string CleanIdentifier(string identifier)
    {
        return identifier.Trim('[', ']', '"', '\'', '`');
    }
}
