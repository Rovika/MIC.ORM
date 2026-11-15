using Mic.Orm;

namespace Shop.Model;

/// <summary>The module that declares the entities.</summary>
/// <remarks>A module is a unit of declaration and versioning. One is enough until parts of the model are released
/// separately.</remarks>
public class ShopModule : EntityModule {
  public ShopModule() : base("Shop", new Version(1, 0)) {
    RegisterEntities(typeof(ICustomer), typeof(IOrder));
  }
}

/// <summary>The application: its modules, and the schema they live in.</summary>
/// <remarks>The schema is the application's default as well as the module's, so MIC's own bookkeeping table lands
/// there with everything else. A server without schemas (SQLite) ignores it; on Oracle a schema is a user, so
/// connect as that user or create it first. To use the connection's own schema instead, drop the settings and the
/// second argument of <c>AddModule</c>.</remarks>
public class ShopApp : EntityApp {
  public const string SchemaName = "shop";

  public ShopApp() : base("Shop", new Version(1, 0), new EntityAppSettings { DefaultSchema = SchemaName }) {
    AddModule(new ShopModule(), SchemaName);
  }
}
