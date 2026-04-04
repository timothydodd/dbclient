using System.Text;
using System.Text.RegularExpressions;

namespace dbclient.Services;

public static partial class SqlFormatter
{
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "JOIN", "INNER", "LEFT", "RIGHT", "CROSS", "FULL", "OUTER",
        "ON", "AND", "OR", "GROUP", "BY", "ORDER", "HAVING", "INSERT", "INTO", "VALUES",
        "UPDATE", "SET", "DELETE", "CREATE", "ALTER", "DROP", "TABLE", "VIEW", "INDEX",
        "UNION", "ALL", "AS", "CASE", "WHEN", "THEN", "ELSE", "END", "IN", "NOT", "NULL",
        "IS", "LIKE", "BETWEEN", "EXISTS", "DISTINCT", "TOP", "LIMIT", "OFFSET", "WITH",
        "EXEC", "CALL", "BEGIN", "COMMIT", "ROLLBACK", "ASC", "DESC", "COUNT", "SUM",
        "AVG", "MIN", "MAX", "COALESCE", "CAST", "CONVERT", "IF", "IFNULL", "ISNULL",
        "PRIMARY", "KEY", "FOREIGN", "REFERENCES", "CONSTRAINT", "DEFAULT", "CHECK",
        "UNIQUE", "TRUNCATE", "DECLARE", "RETURNS", "PROCEDURE", "FUNCTION"
    };

    private static readonly HashSet<string> NewlineBeforeKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "FROM", "WHERE", "JOIN", "INNER", "LEFT", "RIGHT", "CROSS", "FULL",
        "GROUP", "ORDER", "HAVING", "UNION", "SET", "VALUES", "LIMIT", "OFFSET"
    };

    private static readonly HashSet<string> IndentKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "AND", "OR"
    };

    public static string Format(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return sql;

        var tokens = Tokenize(sql);
        var sb = new StringBuilder();
        var indent = "    ";
        var afterSelect = false;
        var selectDepth = 0;

        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            // Preserve string literals, comments, and whitespace-only tokens as-is
            if (token.Type == TokenType.StringLiteral || token.Type == TokenType.Comment)
            {
                sb.Append(token.Value);
                continue;
            }

            if (token.Type == TokenType.Whitespace)
                continue; // We'll manage our own whitespace

            var upper = token.Value.ToUpperInvariant();
            var isKeyword = Keywords.Contains(token.Value);

            // Handle SELECT - put columns on new lines
            if (upper == "SELECT")
            {
                if (sb.Length > 0 && sb[^1] != '\n')
                    sb.AppendLine();
                sb.Append("SELECT");
                afterSelect = true;
                selectDepth = 0;
                continue;
            }

            if (upper == "DISTINCT" && afterSelect)
            {
                sb.Append(' ').Append("DISTINCT");
                continue;
            }

            if (upper == "TOP" && afterSelect)
            {
                sb.Append(' ').Append("TOP");
                continue;
            }

            // Newline-before keywords end SELECT column mode
            if (NewlineBeforeKeywords.Contains(token.Value))
            {
                afterSelect = false;
                sb.AppendLine();
                sb.Append(isKeyword ? upper : token.Value);
                continue;
            }

            // AND/OR get newline + indent
            if (IndentKeywords.Contains(token.Value))
            {
                afterSelect = false;
                sb.AppendLine();
                sb.Append(indent).Append(upper);
                continue;
            }

            // Comma in SELECT list - newline after
            if (token.Value == "," && afterSelect)
            {
                sb.Append(',');
                sb.AppendLine();
                sb.Append(indent);
                continue;
            }

            // Regular token
            if (afterSelect && selectDepth == 0)
            {
                // First column after SELECT
                sb.Append(' ');
                selectDepth++;
            }
            else if (sb.Length > 0 && sb[^1] != '\n' && sb[^1] != ' ' && sb[^1] != '(' && token.Value != ")" && token.Value != ",")
            {
                sb.Append(' ');
            }

            sb.Append(isKeyword ? upper : token.Value);
        }

        return sb.ToString().Trim();
    }

    private static List<Token> Tokenize(string sql)
    {
        var tokens = new List<Token>();
        var i = 0;

        while (i < sql.Length)
        {
            // String literal (single-quoted)
            if (sql[i] == '\'')
            {
                var start = i;
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == '\'' && i + 1 < sql.Length && sql[i + 1] == '\'')
                        i += 2; // escaped quote
                    else if (sql[i] == '\'')
                    {
                        i++;
                        break;
                    }
                    else
                        i++;
                }
                tokens.Add(new Token(sql[start..i], TokenType.StringLiteral));
                continue;
            }

            // Line comment
            if (i + 1 < sql.Length && sql[i] == '-' && sql[i + 1] == '-')
            {
                var start = i;
                while (i < sql.Length && sql[i] != '\n')
                    i++;
                tokens.Add(new Token(sql[start..i], TokenType.Comment));
                continue;
            }

            // Block comment
            if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*')
            {
                var start = i;
                i += 2;
                while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/'))
                    i++;
                if (i + 1 < sql.Length) i += 2;
                tokens.Add(new Token(sql[start..i], TokenType.Comment));
                continue;
            }

            // Whitespace
            if (char.IsWhiteSpace(sql[i]))
            {
                var start = i;
                while (i < sql.Length && char.IsWhiteSpace(sql[i]))
                    i++;
                tokens.Add(new Token(sql[start..i], TokenType.Whitespace));
                continue;
            }

            // Word (identifier or keyword)
            if (char.IsLetterOrDigit(sql[i]) || sql[i] == '_' || sql[i] == '@' || sql[i] == '#')
            {
                var start = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_' || sql[i] == '@' || sql[i] == '#' || sql[i] == '.'))
                    i++;
                tokens.Add(new Token(sql[start..i], TokenType.Word));
                continue;
            }

            // Quoted identifier [name] or `name`
            if (sql[i] == '[' || sql[i] == '`' || sql[i] == '"')
            {
                var start = i;
                var closer = sql[i] == '[' ? ']' : sql[i];
                i++;
                while (i < sql.Length && sql[i] != closer)
                    i++;
                if (i < sql.Length) i++;
                tokens.Add(new Token(sql[start..i], TokenType.Word));
                continue;
            }

            // Single char token (operators, parens, comma, etc.)
            tokens.Add(new Token(sql[i].ToString(), TokenType.Symbol));
            i++;
        }

        return tokens;
    }

    private record Token(string Value, TokenType Type);

    private enum TokenType
    {
        Word,
        Symbol,
        StringLiteral,
        Comment,
        Whitespace
    }
}
