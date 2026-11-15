using System.Text.RegularExpressions;
using Mic.Orm.Db.Model.Construction;
using Mic.Orm.Db.Provider;

namespace Mic.Orm.Db.MySql;

/// <summary>Reads a MySQL schema into a database model.</summary>
/// <remarks>MySQL has an <c>information_schema</c>, so most of the reading is inherited from
/// <see cref="DbModelLoader"/>; the overrides cover what MySQL keeps elsewhere or keeps in addition.</remarks>
public class MySqlModelLoader(DbSettings settings) : DbModelLoader(settings) {
  /// <summary>Asynchronously reads the schemas, which on MySQL are the databases.</summary>
  protected override Task<InfoTable> GetSchemasAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync(
      $"SELECT schema_name FROM information_schema.schemata WHERE {SchemaFilter("schema_name")}", cancellation);

  /// <summary>Asynchronously reads the columns, deriving the identity flag from <c>auto_increment</c> in
  /// <c>extra</c>, since MySQL has no <c>is_identity</c> column.</summary>
  protected override Task<InfoTable> GetColumnsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT table_schema, table_name, column_name,
       CASE WHEN data_type = 'point' AND srs_id IS NOT NULL THEN CONCAT('point SRID ', srs_id) ELSE data_type END
         AS data_type,
       is_nullable, column_default,
       character_maximum_length, numeric_precision, numeric_scale,
       CASE WHEN extra LIKE '%auto_increment%' THEN 1 ELSE 0 END AS is_identity,
       CASE WHEN extra LIKE '%GENERATED%' THEN 1 ELSE 0 END AS is_generated,
       column_comment
FROM information_schema.columns
WHERE {SchemaFilter("table_schema")}
ORDER BY table_schema, table_name, ordinal_position", cancellation);

  /// <summary>Re-quotes a literal default, since MySQL reports a default's value rather than its expression: a column
  /// declared <c>DEFAULT 'none'</c> comes back as <c>none</c>.</summary>
  /// <remarks>Only a value that is plainly not an expression is quoted. A number, <c>CURRENT_TIMESTAMP</c> and
  /// anything parenthesised is MySQL's own expression syntax and is left as it is.</remarks>
  /// <param name="expression">The default as the server reports it.</param>
  /// <returns>The default as an expression.</returns>
  protected override string? NormalizeDefault(string? expression) {
    if (expression == null || expression.Length == 0)
      return expression;
    if (expression.StartsWith('\'') || expression.StartsWith('(')
        || expression.Equals("CURRENT_TIMESTAMP", StringComparison.OrdinalIgnoreCase)
        || double.TryParse(expression, System.Globalization.CultureInfo.InvariantCulture, out _))
      return expression;
    return "'" + expression.Replace("'", "''") + "'";
  }

  /// <summary>Asynchronously reads the indexes from <c>information_schema.statistics</c>, leaving out the primary key
  /// and the indexes InnoDB made for the foreign keys.</summary>
  /// <remarks>The <c>PRIMARY</c> index is the primary key, which the base class has already read as a constraint.
  /// InnoDB creates an index for every foreign key, named after the constraint; left in, each would arrive as an
  /// index MIC never generated and the upgrade would offer to drop it. This is what
  /// <c>DbFeatures.ForeignKeysAutoIndexed</c> is declared for.</remarks>
  protected override Task<InfoTable> GetIndexesAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT DISTINCT s.table_schema, s.table_name, s.index_name,
       CASE WHEN s.non_unique = 0 THEN 1 ELSE 0 END AS is_unique,
       0 AS is_primary, 0 AS is_clustered, NULL AS filter,
       s.index_type
FROM information_schema.statistics s
WHERE s.index_name <> 'PRIMARY' AND {SchemaFilter("s.table_schema")}
  AND NOT EXISTS (
    SELECT 1 FROM information_schema.table_constraints tc
    WHERE tc.constraint_schema = s.table_schema AND tc.table_name = s.table_name
      AND tc.constraint_name = s.index_name AND tc.constraint_type = 'FOREIGN KEY')", cancellation);

  /// <summary>Asynchronously reads the index columns, with the descending flag taken from <c>collation</c>.</summary>
  /// <remarks><c>collation</c> is <c>D</c> for a descending column and null where the column is not ordered at all.
  /// </remarks>
  protected override Task<InfoTable> GetIndexColumnsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT s.table_schema, s.table_name, s.index_name, s.column_name,
       CASE WHEN s.collation = 'D' THEN 1 ELSE 0 END AS is_descending,
       0 AS is_included
FROM information_schema.statistics s
WHERE s.index_name <> 'PRIMARY' AND {SchemaFilter("s.table_schema")}
  AND NOT EXISTS (
    SELECT 1 FROM information_schema.table_constraints tc
    WHERE tc.constraint_schema = s.table_schema AND tc.table_name = s.table_name
      AND tc.constraint_name = s.index_name AND tc.constraint_type = 'FOREIGN KEY')
ORDER BY s.table_schema, s.table_name, s.index_name, s.seq_in_index", cancellation);

  /// <summary>Asynchronously reports no sequences, since MySQL has none.</summary>
  protected override Task<InfoTable> GetSequencesAsync(CancellationToken cancellation) =>
    Task.FromResult(new InfoTable([]));

  /// <summary>Asynchronously reads the foreign keys from <c>key_column_usage</c>, which carries the target table
  /// directly.</summary>
  /// <remarks>MySQL has no <c>constraint_column_usage</c> view, which the standard join goes through.</remarks>
  protected override Task<InfoTable> GetReferentialConstraintsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT DISTINCT k.table_schema, k.table_name, k.constraint_name,
       k.referenced_table_schema AS target_schema, k.referenced_table_name AS target_table,
       rc.delete_rule
FROM information_schema.key_column_usage k
     JOIN information_schema.referential_constraints rc
       ON rc.constraint_schema = k.constraint_schema AND rc.constraint_name = k.constraint_name
WHERE k.referenced_table_name IS NOT NULL AND {SchemaFilter("k.table_schema")}", cancellation);

  protected override Task<InfoTable> GetRefConstraintColumnsAsync(CancellationToken cancellation) =>
    ExecuteSelectAsync($@"
SELECT k.table_schema, k.table_name, k.constraint_name,
       k.column_name, k.referenced_column_name AS target_column
FROM information_schema.key_column_usage k
WHERE k.referenced_table_name IS NOT NULL AND {SchemaFilter("k.table_schema")}
ORDER BY k.table_schema, k.table_name, k.constraint_name, k.ordinal_position", cancellation);

  /// <summary>Asynchronously loads the check constraints and turns each condition back into SQL the server takes.
  /// </summary>
  /// <remarks><c>information_schema.check_constraints</c> gives a condition as it would stand inside a quoted
  /// string, a quote and a backslash each behind a backslash: <c>_utf8mb4\'x\'</c> for the literal <c>'x'</c>. One
  /// backslash is taken off each escaped character.</remarks>
  protected override async Task LoadCheckConstraintsAsync(CancellationToken cancellation) {
    await base.LoadCheckConstraintsAsync(cancellation);
    foreach (var check in Model.Tables.SelectMany(t => t.CheckConstraints))
      check.Expression = Regex.Replace(check.Expression, @"\\(.)", "$1", RegexOptions.Singleline);
  }

  /// <summary>Asynchronously reports no domains, since MySQL has no <c>information_schema.DOMAINS</c> view.</summary>
  protected override Task<InfoTable> GetDomainsAsync(CancellationToken cancellation) =>
    Task.FromResult(new InfoTable([]));
}
