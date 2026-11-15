using Mic.Linq;
using Mic.Orm;
using Shop;
using Shop.Model;

// Connects, makes sure there is a customer, and lists what is there. Replace with the application's own work.
try {
  await using var app = await DatabaseHelper.ConnectAsync(AppConfig.Load());
  Console.WriteLine($"{app.Name} {app.Version} is connected.");

  var session = app.OpenSession();
  if (!await session.EntitySet<ICustomer>().AnyAsync()) {
    var customer = session.NewEntity<ICustomer>();
    customer.Name = "First customer";
    customer.Status = CustomerStatus.Active;
    var order = session.NewEntity<IOrder>();
    order.Customer = customer;
    order.PlacedOn = DateTime.UtcNow;
    order.Status = OrderStatus.Open;
    order.Total = 100m;
    await session.SaveChangesAsync();
    Console.WriteLine("Added the first customer and an order.");
  }

  var customers = await session.EntitySet<ICustomer>().OrderBy(c => c.Name).ToListAsync();
  Console.WriteLine($"{customers.Count} customer(s):");
  foreach (var customer in customers)
    Console.WriteLine($"  {customer.Name}, {customer.Status}, {customer.Orders.Count} order(s)");
  return 0;
} catch (Exception ex) {
  Console.Error.WriteLine(ex.Message);
  return 1;
}
