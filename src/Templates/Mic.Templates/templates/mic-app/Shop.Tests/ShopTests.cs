using Mic.Linq;
using Mic.Orm;
using Shop.Model;

namespace Shop.Tests;

[TestClass]
public class ShopTests {
  [TestMethod]
  public async Task Customer_SavedAndReadBack() {
    var app = await DatabaseHelper.ConnectAsync(AppConfig.Load());

    var session = app.OpenSession();
    var customer = session.NewEntity<ICustomer>();
    customer.Name = "Customer " + Guid.NewGuid().ToString("N")[..8];
    customer.Email = "customer@example.com";
    customer.Status = CustomerStatus.Active;
    var order = session.NewEntity<IOrder>();
    order.Customer = customer;
    order.PlacedOn = DateTime.UtcNow;
    order.Status = OrderStatus.Paid;
    order.Total = 12.50m;
    await session.SaveChangesAsync();

    var reader = app.OpenSession();
    var loaded = await reader.EntitySet<ICustomer>().Where(c => c.Id == customer.Id).FirstOrDefaultAsync();
    Assert.IsNotNull(loaded);
    Assert.AreEqual(customer.Name, loaded.Name);
    Assert.AreEqual(1, loaded.Orders.Count);
    Assert.AreEqual(12.50m, loaded.Orders[0].Total);
  }
}
