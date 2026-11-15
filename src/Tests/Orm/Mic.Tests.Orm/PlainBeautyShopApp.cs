using BeautyShop;
using Mic.Orm;

namespace Mic.Tests.Orm;

/// <summary>The Beauty Shop model as an application WITHOUT its multi-tenancy extension, for the translation tests.
/// </summary>
/// <remarks>WHY THIS EXISTS. The translation tests compare the SQL the translator writes against expected statements
/// written by hand, and each statement is about one thing: a join, a group-by, a paging clause. With the extension
/// added, every statement over a bound entity — which in Beauty Shop is every entity — carries
/// <c>WHERE "Tenant_Id" = @TenantId</c> and a parameter, which would put the same condition into every expected
/// string and hide what each test checks. The sample itself always adds the extension, as a real application does;
/// the seam is here, on the test side, not a switch in the sample's constructor. <c>BeautyShopTests</c> builds the
/// real <see cref="BeautyShopApp"/> and asserts the extension's filters there.</remarks>
public sealed class PlainBeautyShopApp : EntityApp {
  public PlainBeautyShopApp() : base("BeautyShop", new Version(1, 0),
      new EntityAppSettings { DefaultSchema = BeautyShopApp.SchemaName }) {
    AddModule(new BeautyShopModule(), BeautyShopApp.SchemaName);
  }
}
