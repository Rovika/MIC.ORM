using System.Text;

namespace Mic.Orm.Db.Sql;

/// <summary>Provides the text operations on hand-written SQL that need no model: the parameter syntax an application
/// writes, converted to the provider's.</summary>
public static class SqlTextHelper {

  /// <summary>Converts each parameter a hand-written statement writes as <c>@name</c> to the provider's prefix and the
  /// name, and returns the names once each in order of first use.</summary>
  /// <param name="sql">The statement, with parameters written <c>@name</c>.</param>
  /// <param name="parameterPrefix">The provider's prefix, such as <c>@</c> or <c>:</c>.</param>
  /// <param name="names">When this method returns, contains the parameter names the statement uses.</param>
  /// <returns>The statement as the provider takes it; the same text where the prefix is <c>@</c>.</returns>
  /// <remarks>A quoted literal, a double-quoted or bracketed identifier, <c>@@</c>, an <c>@</c> inside a name and an
  /// <c>@</c> followed by anything but a name are left as they are.</remarks>
  public static string ConvertParameters(string sql, string parameterPrefix, out IReadOnlyList<string> names) {
    ArgumentNullException.ThrowIfNull(sql);
    ArgumentNullException.ThrowIfNull(parameterPrefix);
    var parameterNames = new List<string>();
    names = parameterNames;
    var text = new StringBuilder(sql.Length + 16);
    var closing = '\0';
    for (var i = 0; i < sql.Length; i++) {
      var ch = sql[i];
      if (closing != '\0') {
        text.Append(ch);
        if (ch == closing)
          closing = '\0';
        continue;
      }
      if (ch is '\'' or '"' or '[') {
        closing = ch == '[' ? ']' : ch;
        text.Append(ch);
        continue;
      }
      if (ch != '@' || !StartsParameter(sql, i)) {
        text.Append(ch);
        continue;
      }
      var end = i + 1;
      while (end < sql.Length && IsNameChar(sql[end]))
        end++;
      var name = sql.Substring(i + 1, end - i - 1);
      text.Append(parameterPrefix).Append(name);
      if (!parameterNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        parameterNames.Add(name);
      i = end - 1;
    }
    return text.ToString();
  }

  #region Telling a parameter's @ from the other @ in SQL

  // An @ is a parameter only when a name starts right after it and nothing name-like stands right before it.
  // What that rule keeps as plain text, by vendor:
  //
  //   SQL Server   SELECT @@IDENTITY, @@ROWCOUNT          a second @ follows, not a name
  //   MySQL        SELECT @@version, @@sql_mode           the same
  //   PostgreSQL   WHERE tags @> ARRAY['a'], doc <@ x     an operator follows, not a name
  //                WHERE body @@ to_tsquery('mic')        the same
  //   Oracle       SELECT * FROM orders@remote            a name ends right before the @: a database link
  //   any          WHERE email = 'a@b.com'                inside a quoted literal, never reached here
  //
  // MySQL's user variables, @total or @prev, are written exactly like a parameter and cannot be told apart by the
  // text; the command builder leaves one with no value supplied in place, where the provider says the prefix is
  // shared with variables.

  #endregion

  private static bool StartsParameter(string sql, int at) {
    if (at + 1 >= sql.Length || !IsNameStart(sql[at + 1]))
      return false;
    if (at > 0 && (sql[at - 1] == '@' || IsNameChar(sql[at - 1])))
      return false;
    return true;
  }

  private static bool IsNameStart(char ch) => char.IsLetter(ch) || ch == '_';

  private static bool IsNameChar(char ch) => char.IsLetterOrDigit(ch) || ch == '_';
}
