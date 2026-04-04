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

            var context = DetermineContext(queryTokens);
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
            var tablePrefix = ExtractIdentifierBefore(sqlText, checkPosition);

            if (!string.IsNullOrEmpty(tablePrefix))
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

    private static string ExtractIdentifierBefore(string sqlText, int dotPosition)
    {
        var identifier = new StringBuilder();

        for (int i = dotPosition - 1; i >= 0; i--)
        {
            char c = sqlText[i];

            if (char.IsLetterOrDigit(c) || c == '_')
            {
                identifier.Insert(0, c);
            }
            else if (char.IsWhiteSpace(c))
            {
                if (identifier.Length > 0)
                    break;
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

    private static SqlContext DetermineContext(List<string> tokens)
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
            if (IsPartialKeyword(currentToken))
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
                var tokensAfterSelect = tokens.Skip(i + 1).ToList();

                if (tokensAfterSelect.Count == 0)
                {
                    context.Type = SqlContextType.SelectList;
                    context.ExpectingColumnName = true;
                    context.LastKeyword = "SELECT";
                }
                else if (HasFromKeyword(tokensAfterSelect))
                {
                    var fromIndex = -1;
                    for (int j = 0; j < tokensAfterSelect.Count; j++)
                    {
                        if (FromKeywords.Contains(tokensAfterSelect[j].ToUpperInvariant()))
                        {
                            fromIndex = j;
                            break;
                        }
                    }

                    if (fromIndex >= 0)
                    {
                        var tokensBeforeFrom = tokensAfterSelect.Take(fromIndex).ToList();
                        var lastTokenBeforeFrom = tokensBeforeFrom.LastOrDefault()?.ToUpperInvariant();

                        if (string.IsNullOrEmpty(lastTokenBeforeFrom) ||
                            lastTokenBeforeFrom == "," ||
                            IsPartialWord(lastTokenBeforeFrom))
                        {
                            context.Type = SqlContextType.SelectList;
                            context.ExpectingColumnName = true;
                            context.LastKeyword = "SELECT";
                        }
                        else
                        {
                            var fromContext = AnalyzeFromContext(tokensAfterSelect);
                            context.Type = fromContext.Type;
                            context.LastKeyword = fromContext.LastKeyword;
                            context.ExpectingTableName = fromContext.ExpectingTableName;
                            context.ExpectingColumnName = fromContext.ExpectingColumnName;
                            context.ExpectingKeyword = fromContext.ExpectingKeyword;
                            foreach (var alias in fromContext.TableAliases)
                                context.TableAliases[alias.Key] = alias.Value;
                            context.AvailableAliases = context.TableAliases.Keys.ToList();
                        }
                    }
                }
                else
                {
                    var lastTokenAfterSelect = tokensAfterSelect.Last().ToUpperInvariant();

                    if (IsPartialFromKeyword(lastTokenAfterSelect))
                    {
                        context.Type = SqlContextType.SelectList;
                        context.ExpectingKeyword = true;
                        context.LastKeyword = "SELECT";
                    }
                    else if (tokensAfterSelect.Contains("*") ||
                            (tokensAfterSelect.Count >= 1 && !IsPartialWord(tokensAfterSelect[0])))
                    {
                        context.Type = SqlContextType.SelectList;
                        context.ExpectingKeyword = true;
                        context.LastKeyword = "SELECT";
                    }
                    else
                    {
                        context.Type = SqlContextType.SelectList;
                        context.ExpectingColumnName = true;
                        context.LastKeyword = "SELECT";
                    }
                }
                break;
            }
            else if (FromKeywords.Contains(token))
            {
                context.Type = SqlContextType.FromClause;
                context.ExpectingTableName = true;
                context.LastKeyword = token;
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

    private static bool HasFromKeyword(List<string> tokens)
    {
        return tokens.Any(t => FromKeywords.Contains(t.ToUpperInvariant()));
    }

    private static SqlContext AnalyzeFromContext(List<string> tokensAfterSelect)
    {
        var context = new SqlContext();

        for (int i = 0; i < tokensAfterSelect.Count; i++)
        {
            var token = tokensAfterSelect[i].ToUpperInvariant();
            if (token == "FROM")
            {
                var tokensAfterFrom = tokensAfterSelect.Skip(i + 1).ToList();

                if (tokensAfterFrom.Count == 0)
                {
                    context.Type = SqlContextType.FromClause;
                    context.ExpectingTableName = true;
                    context.LastKeyword = "FROM";
                }
                else if (tokensAfterFrom.Count == 1 && IsPartialWord(tokensAfterFrom[0]))
                {
                    context.Type = SqlContextType.FromClause;
                    context.ExpectingTableName = true;
                    context.LastKeyword = "FROM";
                }
                else if (tokensAfterFrom.Any(t => WhereKeywords.Contains(t.ToUpperInvariant())))
                {
                    context.Type = SqlContextType.WhereClause;
                    context.ExpectingColumnName = true;
                }
                else
                {
                    var lastToken = tokensAfterFrom.Last();
                    var hasJoinKeywords = tokensAfterFrom.Any(t => FromKeywords.Contains(t.ToUpperInvariant()));

                    if (tokensAfterFrom.Count >= 2 &&
                        !IsKeyword(tokensAfterFrom[0]) &&
                        !IsKeyword(tokensAfterFrom[1]) &&
                        !hasJoinKeywords &&
                        IsPartialWord(lastToken))
                    {
                        context.Type = SqlContextType.AfterTableAlias;
                        context.ExpectingKeyword = true;
                        context.LastKeyword = "FROM";
                    }
                    else if (!hasJoinKeywords && tokensAfterFrom.Count >= 1 &&
                            !IsKeyword(tokensAfterFrom[0]) && IsPartialWord(lastToken))
                    {
                        context.Type = SqlContextType.AfterTableName;
                        context.ExpectingKeyword = true;
                        context.LastKeyword = "FROM";
                    }
                    else
                    {
                        context.Type = SqlContextType.FromClause;
                        context.ExpectingTableName = true;
                    }
                }
                break;
            }
        }

        return context;
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
                var nextToken = tokens[i + 1];
                if (!string.IsNullOrEmpty(nextToken) && !IsKeyword(nextToken))
                    tableNames.Add(CleanIdentifier(nextToken));
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

            if (token == "FROM" || token.EndsWith("JOIN"))
            {
                if (i + 1 < tokens.Count)
                {
                    var tableName = CleanIdentifier(tokens[i + 1]);

                    bool hasAlias = false;
                    if (i + 2 < tokens.Count)
                    {
                        var potentialAlias = tokens[i + 2];

                        if (potentialAlias.Equals("AS", StringComparison.OrdinalIgnoreCase))
                        {
                            if (i + 3 < tokens.Count)
                            {
                                var alias = CleanIdentifier(tokens[i + 3]);
                                if (!IsKeyword(alias))
                                {
                                    aliases[alias] = tableName;
                                    hasAlias = true;
                                }
                            }
                        }
                        else if (!IsKeyword(potentialAlias) &&
                                !potentialAlias.Equals(",", StringComparison.OrdinalIgnoreCase) &&
                                !potentialAlias.Equals("(", StringComparison.OrdinalIgnoreCase) &&
                                !potentialAlias.Equals(")", StringComparison.OrdinalIgnoreCase) &&
                                !potentialAlias.Equals("WHERE", StringComparison.OrdinalIgnoreCase) &&
                                !potentialAlias.Equals("ORDER", StringComparison.OrdinalIgnoreCase) &&
                                !potentialAlias.Equals("GROUP", StringComparison.OrdinalIgnoreCase) &&
                                !potentialAlias.Equals("HAVING", StringComparison.OrdinalIgnoreCase) &&
                                !potentialAlias.Equals("LIMIT", StringComparison.OrdinalIgnoreCase) &&
                                !potentialAlias.Equals("INNER", StringComparison.OrdinalIgnoreCase) &&
                                !potentialAlias.Equals("LEFT", StringComparison.OrdinalIgnoreCase) &&
                                !potentialAlias.Equals("RIGHT", StringComparison.OrdinalIgnoreCase) &&
                                !potentialAlias.Equals("FULL", StringComparison.OrdinalIgnoreCase))
                        {
                            var alias = CleanIdentifier(potentialAlias);
                            aliases[alias] = tableName;
                            hasAlias = true;
                        }
                    }

                    if (!hasAlias && !aliases.ContainsKey(tableName))
                        aliases[tableName] = tableName;
                }
            }
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
