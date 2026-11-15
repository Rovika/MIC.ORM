using Mic.Orm;

namespace Shop.Model;

// THE STARTER MODEL: a customer and the orders placed by one. Replace it with your own entities, or generate them
// from an existing database with `mic-db entities` (see mic-db/entities.json beside the host).
//
// An entity is an interface; MIC emits the class. A reference to another entity is a property of its type, and a
// getter-only IList<T> is the one-to-many back from it. The attributes carry what the database needs to know: sizes,
// keys, indexes, precision.

public enum CustomerStatus { Active, Inactive }

public enum OrderStatus { Open, Paid, Shipped, Cancelled }

[Entity]
public interface ICustomer {
  [PrimaryKey, Auto] Guid Id { get; set; }
  [Size(100), Unique] string Name { get; set; }
  [Size(200)] string? Email { get; set; }
  CustomerStatus Status { get; set; }
  IList<IOrder> Orders { get; }
}

[Entity, Index("Customer,PlacedOn")]
public interface IOrder {
  [PrimaryKey, Auto] Guid Id { get; set; }
  ICustomer Customer { get; set; }
  [Utc] DateTime PlacedOn { get; set; }
  OrderStatus Status { get; set; }
  [Column(Precision = 12, Scale = 2)] decimal Total { get; set; }
  [Unlimited] string? Notes { get; set; }
}
