using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Provider;
using Mic.Orm.Logging;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm.Db;

// SQL THE APPLICATION WRITES, run through the session: a parameter written `{name}` and rendered as the provider's
// marker, the rows back as they came, as tracked records of an entity, as scalars or as typed objects built by column
// name, one value, a row count — and what a statement says to the entity cache, which is nothing unless the caller
// says what it touched.
public abstract partial class DmlTests {
  sealed record WidgetRow(Guid Id, string Name);

  sealed class WidgetDto {
    public string Name { get; set; } = "";
    public int Unused { get; set; } = 7;
  }

  [TestMethod]
  public async Task DirectSql_RunsThroughTheSession() {
    var tag = "sql-" + Guid.NewGuid().ToString("N")[..8];
    await using var session = NewSession();
    var widgets = new List<Dml.IWidget>();
    foreach (var suffix in new[] { "-a", "-b", "-c" }) {
      var widget = session.NewEntity<Dml.IWidget>();
      widget.Id = Guid.NewGuid();
      widget.Name = tag + suffix;
      widgets.Add(widget);
    }
    await session.SaveChangesAsync();
    var table = Db.GetTableRef("Widget");
    var name = Db.Quote("Name");
    var id = Db.Quote("Id");
    var byPrefix = ("prefix", (object?)(tag + "%"));

    session.LogMessage("direct: rows as they came, the parameter rendered with the provider's prefix");
    var rows = await session.RunSqlQueryAsync($"SELECT {id}, {name} FROM {table} WHERE {name} LIKE @prefix ORDER BY {name}", byPrefix);
    Assert.AreEqual(3, rows.Rows.Count);
    Assert.AreEqual(tag + "-a", rows.Rows[0].GetString("Name"));
    var ran = session.LastCommands.Single();
    StringAssert.Contains(ran.Sql, Db.Provider.ParameterPrefix + "prefix", "the marker is the provider's");
    Assert.IsFalse(ran.Sql.Contains('{'), "no brace survives the render: " + ran.Sql);

    session.LogMessage("direct: as entities — the columns matched by name, the records the ones the session tracks");
    var entities = await session.RunSqlQueryAsync<Dml.IWidget>(
      $"SELECT {name}, {id} FROM {table} WHERE {name} LIKE @prefix ORDER BY {name}", byPrefix);
    Assert.AreEqual(3, entities.Count);
    Assert.AreSame(widgets[0], entities[0], "the identity map answered with the record the save tracks");
    Assert.AreEqual(tag + "-b", entities[1].Name);
    var untypedRecords = (IReadOnlyList<EntityRecord>)(await session.RunSqlQueryAsync(
      $"SELECT {name}, {id} FROM {table} WHERE {name} LIKE @prefix ORDER BY {name}", typeof(Dml.IWidget), byPrefix))!;
    Assert.AreSame<object>(widgets[0], untypedRecords[0].EntityInstance, "the untyped form, named by type, gives the same records");
    var noKey = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
      session.RunSqlQueryAsync<Dml.IWidget>($"SELECT {name} FROM {table}"));
    StringAssert.Contains(noKey.Message, "Id");

    session.LogMessage("direct: as scalars, and as typed objects built by column name");
    var names = await session.RunSqlQueryAsync<string>(
      $"SELECT {name} FROM {table} WHERE {name} LIKE @prefix ORDER BY {name}", byPrefix);
    CollectionAssert.AreEqual(widgets.Select(w => w.Name).ToList(), names.ToList());
    var records = await session.RunSqlQueryAsync<WidgetRow>(
      $"SELECT {id}, {name} FROM {table} WHERE {name} LIKE @prefix ORDER BY {name}", byPrefix);
    Assert.AreEqual(widgets[2].Id, records[2].Id, "a Guid read back as a Guid, however the server stores one");
    Assert.AreEqual(tag + "-c", records[2].Name);
    var dtos = await session.RunSqlQueryAsync<WidgetDto>(
      $"SELECT {name} FROM {table} WHERE {name} LIKE @prefix ORDER BY {name}", byPrefix);
    Assert.AreEqual(tag + "-a", dtos[0].Name);
    Assert.AreEqual(7, dtos[0].Unused, "a settable property with no column keeps what the constructor gave it");

    session.LogMessage("direct: one value, a row count, a Guid bound as the server stores it");
    Assert.AreEqual(3, await session.RunSqlScalarAsync<int>($"SELECT COUNT(*) FROM {table} WHERE {name} LIKE @prefix", byPrefix));
    Assert.AreEqual(1, await session.RunSqlNonQueryAsync($"UPDATE {table} SET {name} = @renamed WHERE {name} = @old",
      ("renamed", tag + "-z"), ("old", tag + "-a")));
    Assert.AreEqual(tag + "-z", await session.RunSqlScalarAsync<string>($"SELECT {name} FROM {table} WHERE {id} = @id", ("id", widgets[0].Id)));
    Assert.AreEqual(tag + "-a", widgets[0].Name, "the statement told the tracked record nothing, by the rule");

    session.LogMessage("direct: the names in the text and the values given have to agree");
    // Where the prefix is also the user variables', as on MySQL, an unmatched name is a variable and the statement runs.
    if (Db.Provider.Supports(DbFeatures.ParameterPrefixSharedWithVariables))
      Assert.AreEqual(0, (await session.RunSqlQueryAsync($"SELECT {name} FROM {table} WHERE {name} = @missing")).Rows.Count, "a null variable matches nothing");
    else
      await Assert.ThrowsExactlyAsync<ArgumentException>(() => session.RunSqlQueryAsync($"SELECT {name} FROM {table} WHERE {name} = @missing"));
    await Assert.ThrowsExactlyAsync<ArgumentException>(() => session.RunSqlQueryAsync($"SELECT {name} FROM {table}", ("stray", 1)));
    var quoted = await session.RunSqlQueryAsync<string>($"SELECT '@not_a_parameter' AS v FROM {table} WHERE {id} = @id", ("id", widgets[1].Id));
    Assert.AreEqual("@not_a_parameter", quoted.Single(), "an @ inside a literal is text");

    session.LogMessage("direct: a write says nothing about the cache unless the caller says what it touched");
    session.Context.SetValue(((DmlApp)App).Tenancy.TenantIdKey, Guid.NewGuid());
    var color = App.Model!.FindEntity(typeof(Dml.IColor))!;
    var cache = SharedDatabase.EntityCache;
    var before = await cache.GetBlockAsync(color, session);
    var colors = Db.GetTableRef("Color");
    await session.RunSqlNonQueryAsync($"UPDATE {colors} SET {name} = {name} WHERE 1 = 0");
    Assert.AreSame(before, await cache.GetBlockAsync(color, session), "the block stays: the ORM does not know what the text touched");
    await session.RunSqlNonQueryAsync($"UPDATE {colors} SET {name} = {name} WHERE 1 = 0", [typeof(Dml.IColor)]);
    Assert.AreNotSame(before, await cache.GetBlockAsync(color, session), "the caller said, and the block went");

    Assert.AreEqual(3, await session.RunSqlNonQueryAsync($"DELETE FROM {table} WHERE {name} LIKE @prefix", byPrefix));
  }
}
