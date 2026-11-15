using Mic.Orm;
using Mic.Orm.Extensions.MultiTenancy;

namespace BeautyShop;

// THE BEAUTY SHOP MODEL — an online service for the beauty industry: nails, lashes, hair. Each beauty company is a
// COMPANY record with its own shops, staff, customers, suppliers and books, and a TENANT of the service.
//
// THE TENANT IS INFRASTRUCTURE, THE COMPANY IS BUSINESS DOMAIN, and they are kept apart on purpose. Every entity in the
// model inherits `IBeautyEntity`, which carries the key and, through the shipped `ITenantBound`, the reference to the
// tenant — so partitioning is a fact of the base type rather than a line repeated thirty times and occasionally
// forgotten. The multi-tenancy extension filters every read by it and stamps every row with it. `ICompany` is the
// business record of the account: its name, its status, its contacts. Only the TOP entities of the hierarchy reference
// it — a shop, a user, a person, a vendor, a category, a payment source, a payroll run; a child reached through its
// parent does not.
//
// NOTHING IS SHARED BETWEEN TENANTS. A customer of one company is a different record from the same person at another,
// because each company is a different SITE to an end user. The visible price is that every company lists its own
// vendors and payment sources where a real platform would share a reference list — taken knowingly, since one rule
// with no exception is worth more here than realism that complicates every table.
//
// NO `CreatedOn` ANYWHERE. Every key is a version-7 Guid — `GuidFactory` calls `Guid.CreateVersion7(timestamp)` — so
// the creation instant is IN the key and a column repeating it would be a second copy to keep honest. The one record
// this does not serve is `ITenant`, whose key is a short the application assigns.
//
// WHAT IT EXERCISES, deliberately rather than by accident: a self-referencing reference (a stylist's manager),
// many-to-many WITH PAYLOAD (an appointment's services, a sale's lines) and WITHOUT (a stylist's shops), nullable
// references, `IList<T>` one-to-many with NO attribute (a list of entities is a one-to-many by its type), `DateOnly`
// and `TimeOnly` members, decimal money with explicit precision, plain and [Flags] enums, two-column unique keys, a
// filtered index, and [Unlimited] notes.
//
// BESIDE THE TABLES: three views the module defines in LINQ, one of them materialized, and a table function with a
// PostgreSQL body; the upgrade creates them with the tables. See the end of this file and `BeautyShopModule`.
//
// WHAT IS DELIBERATELY ABSENT: computed columns. A sale's total is a sum over another table, which a generated column
// cannot be, so it is stored and maintained by whatever writes the lines.

// ----- the tenant, the company, and what every entity has ---------------------------------------------------------------

/// <summary>What every entity of the model has: its key, and through <see cref="ITenantBound"/> the tenant it belongs
/// to.</summary>
/// <remarks>A BASE INTERFACE, not an attribute or a convention — MIC flattens a base interface's properties into the
/// entity, so this is the partitioning rule expressed once and inherited rather than copied. The tenant table itself,
/// <see cref="ITenant"/>, is the ORM's and is registered with the module.
///
/// THE INDEX ON `Tenant` IS PARTLY REDUNDANT, and knowingly so. Where an entity also declares a composite index
/// leading with `Tenant` — most of the interesting ones do — the marker's index is a second one the first already
/// covers. It is kept because the entities that declare NO index of their own are the majority (every child table
/// reached through its parent), and one narrow index per table is a cheaper mistake on a sample database than a
/// missing one. A real system would drop the duplicates.</remarks>
public interface IBeautyEntity : ITenantBound {
  [PrimaryKey, Auto] Guid Id { get; set; }
}

/// <summary>A beauty company: the business record of an account of the service.</summary>
/// <remarks>NOT THE TENANT. The tenant is the ORM's <see cref="ITenant"/>, a number every row carries; the company is
/// what the business knows about the account — its name, whether it is active, who to call. The top entities of the
/// hierarchy reference it; a child reached through its parent does not. Its contacts are the employees whose roles
/// carry <see cref="EmployeeRoles.CompanyContact"/>.</remarks>
[Entity]
public interface ICompany : IBeautyEntity {
  [Size(100), Unique] string Name { get; set; }
  RecordStatus Status { get; set; }
}

/// <summary>Someone who can sign in.</summary>
/// <remarks>`Company` IS NULLABLE: an admin who manages the application belongs to no company. The admin still belongs
/// to a tenant, as every row does.
///
/// IT KNOWS NOTHING OF EMPLOYEES OR CUSTOMERS. The link runs the other way — `IEmployee.User` and `ICustomer.User`,
/// both nullable — because a login is optional for both: a junior stylist may never sign in, and a walk-in customer
/// has a record without an account. Pointing from here instead would have meant two nullable columns that must never
/// both be set, enforced by nothing.
///
/// `Login` is unique WITHIN a tenant rather than globally, which is what the two-column key says.</remarks>
[Entity, Unique("Tenant,Login"), Unique("Tenant,Email")]
public interface IUser : IBeautyEntity {
  ICompany? Company { get; set; }
  [Size(100)] string Login { get; set; }
  [Size(200)] string Email { get; set; }
  [Size(100), Secret] string PasswordHash { get; set; }
  UserType UserType { get; set; }
  RecordStatus Status { get; set; }
  [Utc] DateTime? LastSignedInOn { get; set; }
}

// ----- people, places, and how to reach them --------------------------------------------------------------------------

[Entity, Index("Tenant,City"), Index("Tenant,PostalCode")]
public interface IAddress : IBeautyEntity {
  [Size(200)] string Line1 { get; set; }
  [Size(200)] string? Line2 { get; set; }
  [Size(80)] string City { get; set; }
  [Size(40)] string? State { get; set; }
  [Size(20)] string PostalCode { get; set; }
  [Size(60)] string Country { get; set; }
}

[Entity, Index("Tenant,Number")]
public interface IPhone : IBeautyEntity {
  PhoneType PhoneType { get; set; }
  [Size(30)] string Number { get; set; }
}

/// <summary>A human being. The NORMALISATION: a stylist and a customer are both people, and everything true of a
/// person rather than of a role lives here once.</summary>
[Entity, Index("Tenant,LastName,FirstName"), Index("Tenant,Email"), Index("Tenant,Phone")]
public interface IPerson : IBeautyEntity {
  [Index] ICompany Company { get; set; }
  [Size(60)] string FirstName { get; set; }
  [Size(60)] string LastName { get; set; }
  [Size(200)] string? Email { get; set; }
  DateOnly? BirthDate { get; set; }
  IAddress? Address { get; set; }
  IPhone? Phone { get; set; }
}

/// <summary>One salon. A company has one to five of them.</summary>
[Entity, Unique("Tenant,Name")]
public interface IShop : IBeautyEntity {
  [Index] ICompany Company { get; set; }
  [Size(100)] string Name { get; set; }
  IAddress? Address { get; set; }
  IPhone? Phone { get; set; }
  [Size(60)] string TimeZone { get; set; }
  RecordStatus Status { get; set; }

  IList<IShopHours> OpenHours { get; }
  /// <summary>The staff whose home shop this is.</summary>
  [OneToMany("HomeShop")] IList<IEmployee> Employees { get; }
  /// <summary>Everyone who works here, at home or on a rota: the many-to-many through <see cref="IShopEmployee"/>.
  /// </summary>
  [ManyToMany(typeof(IShopEmployee))] IList<IEmployee> Staff { get; }
}

/// <summary>When a shop is open, one row per weekday.</summary>
/// <remarks>An ENTITY rather than a string, because opening hours are asked questions — "is this shop open at four on
/// Thursday" — and a string cannot answer one. It is also this model's only use of `TimeOnly`, which is a clock time
/// with no date and exactly what an opening hour is.</remarks>
[Entity, Unique("Shop,DayOfWeek")]
public interface IShopHours : IBeautyEntity {
  IShop Shop { get; set; }
  DayOfWeek DayOfWeek { get; set; }
  TimeOnly OpensAt { get; set; }
  TimeOnly ClosesAt { get; set; }
}

/// <summary>A member of staff.</summary>
/// <remarks>`Manager` is the SELF-REFERENCE — nullable, because the owner reports to nobody, and the one place this
/// model puts a foreign key back into its own table.
///
/// `HomeShop` IS NULLABLE and `Shops` is a list: a stylist usually works at one shop and sometimes covers another,
/// and a company contact — the owner, the accountant — may work at none. The home shop is where the diary looks for
/// them first; the list, through <see cref="IShopEmployee"/>, is everywhere they may be booked.</remarks>
[Entity, Index("Tenant,Person"), Index("Tenant,HomeShop"), Index("Tenant,User"), Index("Tenant,Manager")]
public interface IEmployee : IBeautyEntity {
  IPerson Person { get; set; }
  IShop? HomeShop { get; set; }
  /// <summary>Null for someone who never signs in — which is ordinary for a junior.</summary>
  IUser? User { get; set; }
  EmployeeRoles Roles { get; set; }
  Specialties Specialties { get; set; }
  DateOnly HiredOn { get; set; }
  /// <summary>Null while they still work here.</summary>
  DateOnly? TerminatedOn { get; set; }
  [Column(Precision = 9, Scale = 2)] decimal HourlyRate { get; set; }
  [Column(Precision = 5, Scale = 2)] decimal CommissionPercent { get; set; }
  IEmployee? Manager { get; set; }
  RecordStatus Status { get; set; }

  [ManyToMany(typeof(IShopEmployee))] IList<IShop> Shops { get; }
}

/// <summary>A member of staff at a shop — MANY-TO-MANY WITHOUT PAYLOAD, the link the two lists go through.</summary>
/// <remarks>The pair IS the key: a composite primary key over the two references, no surrogate, which is also the
/// unique constraint the link needs. The one entity outside `IBeautyEntity`, since it has no `Id`; it inherits the
/// tenant marker directly.</remarks>
[Entity, PrimaryKey("Shop,Employee")]
public interface IShopEmployee : ITenantBound {
  IShop Shop { get; set; }
  IEmployee Employee { get; set; }
}

/// <summary>Someone who books.</summary>
[Entity, Index("Tenant,Person"), Index("Tenant,User")]
public interface ICustomer : IBeautyEntity {
  IPerson Person { get; set; }
  /// <summary>Null for a walk-in with no account.</summary>
  IUser? User { get; set; }
  DateOnly JoinedOn { get; set; }
  [Unlimited] string? Notes { get; set; }
  RecordStatus Status { get; set; }
}

/// <summary>A period a member of staff is NOT available — a holiday, illness, training.</summary>
[Entity, Index("Tenant,Employee,StartsOn")]
public interface IAvailability : IBeautyEntity {
  IEmployee Employee { get; set; }
  AvailabilityKind Kind { get; set; }
  /// <summary>A DATETIME rather than a date: an afternoon off is as ordinary as a fortnight away.</summary>
  [Utc] DateTime StartsOn { get; set; }
  [Utc] DateTime EndsOn { get; set; }
  [Unlimited] string? Notes { get; set; }
}

// ----- what is sold ----------------------------------------------------------------------------------------------------

/// <summary>A grouping on the service menu. Its `Specialties` say what KIND of work it is, which is what matches a
/// category to the stylists and stations able to do it.</summary>
[Entity, Unique("Tenant,Name")]
public interface IServiceCategory : IBeautyEntity {
  [Index] ICompany Company { get; set; }
  [Size(80)] string Name { get; set; }
  Specialties Specialties { get; set; }
}

/// <summary>A treatment on the menu, with its list price and how long it takes.</summary>
[Entity, Unique("Tenant,Category,Name")]
public interface IService : IBeautyEntity {
  IServiceCategory Category { get; set; }
  [Size(100)] string Name { get; set; }
  int DurationMinutes { get; set; }
  [Column(Precision = 9, Scale = 2)] decimal Price { get; set; }
  RecordStatus Status { get; set; }
}

/// <summary>A chair, table or station in a shop, identified by its number and by what it is fitted for.</summary>
[Entity, Unique("Shop,Number")]
public interface IServiceStation : IBeautyEntity {
  IShop Shop { get; set; }
  int Number { get; set; }
  Specialties Specialties { get; set; }
  RecordStatus Status { get; set; }
}

// ----- the diary ---------------------------------------------------------------------------------------------------------

/// <summary>A booking: who, with whom, where, and when.</summary>
/// <remarks>The FILTERED INDEX earns itself: the diary is read by "what is still coming" far more than by anything
/// else, and completed and cancelled rows are the bulk of the table within a year. Its SQL is PostgreSQL's, which is
/// what the sample targets.</remarks>
[Entity]
[ClusteredIndex("Tenant,Shop,StartsOn")]
[Index("Tenant,Customer")]
[Index("Tenant,Employee")]
[Index("Tenant,StartsOn", Filter = "\"Status\" < 3", IncludeMembers = "Employee,Customer")]
public interface IAppointment : IBeautyEntity {
  IShop Shop { get; set; }
  ICustomer Customer { get; set; }
  IEmployee Employee { get; set; }
  /// <summary>Which station, once somebody decides — commonly not at booking time.</summary>
  IServiceStation? Station { get; set; }
  [Utc] DateTime StartsOn { get; set; }
  [Utc] DateTime EndsOn { get; set; }
  AppointmentStatus Status { get; set; }
  [Unlimited] string? Notes { get; set; }

  IList<IAppointmentService> Services { get; }
}

/// <summary>One service on one appointment — MANY-TO-MANY WITH PAYLOAD.</summary>
/// <remarks>Not a bare link table: the price is captured AT BOOKING, because a menu price changes and a booking made
/// last month was made at last month's price. That payload is why this is an entity.</remarks>
[Entity, UniqueClusteredIndex("Appointment,Service")]
public interface IAppointmentService : IBeautyEntity {
  IAppointment Appointment { get; set; }
  IService Service { get; set; }
  [Column(Precision = 9, Scale = 2)] decimal Price { get; set; }
  int DurationMinutes { get; set; }
}

// ----- supplies ----------------------------------------------------------------------------------------------------------

[Entity, Unique("Tenant,Name")]
public interface IVendor : IBeautyEntity {
  [Index] ICompany Company { get; set; }
  [Size(120)] string Name { get; set; }
  [Size(200)] string ContactEmail { get; set; }
  IAddress? Address { get; set; }
  IPhone? Phone { get; set; }
  RecordStatus Status { get; set; }
}

[Entity, Unique("Tenant,Name")]
public interface IProductCategory : IBeautyEntity {
  [Index] ICompany Company { get; set; }
  [Size(80)] string Name { get; set; }
}

[Entity, Unique("Tenant,Sku"), Index("Tenant,Vendor"), Index("Tenant,Category")]
public interface IProduct : IBeautyEntity {
  IProductCategory Category { get; set; }
  IVendor? Vendor { get; set; }
  [Size(40)] string Sku { get; set; }
  [Size(120)] string Name { get; set; }
  [Column(Precision = 9, Scale = 2)] decimal UnitCost { get; set; }
  [Column(Precision = 9, Scale = 2)] decimal RetailPrice { get; set; }
  RecordStatus Status { get; set; }
}

/// <summary>How much of a product one shop holds.</summary>
/// <remarks>The two-column unique key is the point of the shape: a shop has ONE row per product, and the database
/// enforces it rather than the code that writes it.</remarks>
[Entity, UniqueClusteredIndex("Shop,Product"), Index("Tenant,Product")]
public interface IStockLevel : IBeautyEntity {
  IShop Shop { get; set; }
  IProduct Product { get; set; }
  int QuantityOnHand { get; set; }
  int ReorderLevel { get; set; }
  DateOnly? LastCountedOn { get; set; }
}

[Entity, Index("Tenant,Vendor"), Index("Tenant,ShippedOn")]
public interface IShipment : IBeautyEntity {
  IVendor Vendor { get; set; }
  IShop Shop { get; set; }
  [Size(40)] string ReferenceNumber { get; set; }
  DateOnly ShippedOn { get; set; }
  DateOnly? ReceivedOn { get; set; }
  ShipmentStatus Status { get; set; }

  IList<IShipmentLine> Lines { get; }
}

[Entity, ClusteredIndex("Shipment"), Index("Tenant,Product")]
public interface IShipmentLine : IBeautyEntity {
  IShipment Shipment { get; set; }
  IProduct Product { get; set; }
  int Quantity { get; set; }
  [Column(Precision = 9, Scale = 2)] decimal UnitCost { get; set; }
}

// ----- money out ---------------------------------------------------------------------------------------------------------

/// <summary>Where a company's money moves through — its own account, its card processor, a till.</summary>
/// <remarks>A REFERENCE, NOT AN INTEGRATION. Nothing here talks to a bank or a card service; this exists so a payment
/// can say where it went, which is what a books-keeping model needs and all it needs.</remarks>
[Entity, Unique("Tenant,Name")]
public interface IPaymentSource : IBeautyEntity {
  [Index] ICompany Company { get; set; }
  [Size(100)] string Name { get; set; }
  PaymentSourceKind Kind { get; set; }
  [Size(80)] string? BankName { get; set; }
  [Size(4)] string? AccountLast4 { get; set; }
  RecordStatus Status { get; set; }
}

[Entity, Unique("Tenant,Vendor,InvoiceNumber"), Index("Tenant,IssuedOn")]
public interface IVendorInvoice : IBeautyEntity {
  IVendor Vendor { get; set; }
  IShipment? Shipment { get; set; }
  [Size(40)] string InvoiceNumber { get; set; }
  DateOnly IssuedOn { get; set; }
  DateOnly DueOn { get; set; }
  [Column(Precision = 12, Scale = 2)] decimal TotalAmount { get; set; }
  InvoiceStatus Status { get; set; }
}

[Entity, Index("Tenant,PaidOn")]
public interface IVendorPayment : IBeautyEntity {
  IVendorInvoice Invoice { get; set; }
  IPaymentSource Source { get; set; }
  DateOnly PaidOn { get; set; }
  [Column(Precision = 12, Scale = 2)] decimal Amount { get; set; }
  [Size(60)] string? TransactionReference { get; set; }
}

// ----- money in ----------------------------------------------------------------------------------------------------------

/// <summary>What a customer bought in one visit — services, products, or both.</summary>
/// <remarks>`TotalAmount` is STORED and maintained by whatever writes the lines: a sum over another table is not a
/// generated column on any server, and the sample keeps no trigger.</remarks>
[Entity, Index("Tenant,SoldOn"), Index("Tenant,Customer")]
public interface ISale : IBeautyEntity {
  IShop Shop { get; set; }
  ICustomer Customer { get; set; }
  IAppointment? Appointment { get; set; }
  [Utc] DateTime SoldOn { get; set; }
  [Column(Precision = 12, Scale = 2)] decimal TotalAmount { get; set; }
  SaleStatus Status { get; set; }

  IList<ISaleLine> Lines { get; }
  IList<IPayment> Payments { get; }
}

/// <summary>One line of a sale — a product OR a service, never both and never neither.</summary>
/// <remarks>BOTH REFERENCES ARE NULLABLE and exactly one is set, which nothing in this model enforces: MIC generates
/// no CHECK constraints, so the rule lives in the code that writes lines. It is a fair thing for the sample to
/// demonstrate, since the schema browser will show the constraint as absent.</remarks>
[Entity, ClusteredIndex("Sale"), Index("Tenant,Product")]
public interface ISaleLine : IBeautyEntity {
  ISale Sale { get; set; }
  IProduct? Product { get; set; }
  IService? Service { get; set; }
  IEmployee? PerformedBy { get; set; }
  int Quantity { get; set; }
  [Column(Precision = 9, Scale = 2)] decimal UnitPrice { get; set; }
}

[Entity, ClusteredIndex("Sale")]
public interface IPayment : IBeautyEntity {
  ISale Sale { get; set; }
  IPaymentSource Source { get; set; }
  PaymentMethod Method { get; set; }
  [Utc] DateTime PaidOn { get; set; }
  [Column(Precision = 12, Scale = 2)] decimal Amount { get; set; }
  /// <summary>What the card service called it. A RECORD of a transaction, not a link to one — the integration is out
  /// of scope and this is what remains of it.</summary>
  [Size(60)] string? TransactionReference { get; set; }
}

// ----- hours and pay -----------------------------------------------------------------------------------------------------

/// <summary>Hours worked, and how many of them were billable.</summary>
/// <remarks>DECIMAL rather than double, and it is not caution about the hours themselves: these are multiplied by an
/// hourly rate and summed into `IPayrollLine.GrossAmount`, so they are a factor in a MONEY calculation. A binary
/// float would make payroll totals depend on the order rows were added.</remarks>
[Entity, ClusteredIndex("Tenant,Employee,WorkedOn")]
public interface ITimeEntry : IBeautyEntity {
  IEmployee Employee { get; set; }
  DateOnly WorkedOn { get; set; }
  [Column(Precision = 5, Scale = 2)] decimal Hours { get; set; }
  [Column(Precision = 5, Scale = 2)] decimal BilledHours { get; set; }
  [Unlimited] string? Notes { get; set; }
}

[Entity]
public interface IPayrollRun : IBeautyEntity {
  [Index] ICompany Company { get; set; }
  DateOnly PeriodStart { get; set; }
  DateOnly PeriodEnd { get; set; }
  PayrollStatus Status { get; set; }
  [Utc] DateTime? ApprovedOn { get; set; }

  IList<IPayrollLine> Lines { get; }
}

[Entity, UniqueClusteredIndex("Run,Employee"), Index("Tenant,Employee")]
public interface IPayrollLine : IBeautyEntity {
  IPayrollRun Run { get; set; }
  IEmployee Employee { get; set; }
  [Column(Precision = 7, Scale = 2)] decimal Hours { get; set; }
  [Column(Precision = 12, Scale = 2)] decimal GrossAmount { get; set; }
  [Column(Precision = 12, Scale = 2)] decimal CommissionAmount { get; set; }
}

// ----- views and a function ---------------------------------------------------------------------------------------------
//
// A VIEW IS ONE DATABASE OBJECT FOR EVERY TENANT: its select is defined without the extension's filter. The tenant is
// enforced where the ORM reads from it, so each view projects the tenant's id as a column marked `[TenantId]`, and a
// read through the view sees the context's tenant's rows like a read of a table.

/// <summary>An appointment still to come, flattened for a diary screen: the one query the appointment table's
/// filtered index is for.</summary>
/// <remarks>A VIEW the module defines in LINQ; the upgrade creates it and recreates it when the query changes. The
/// names come from <c>Person</c> through two joins each, written here once instead of in every query above.</remarks>
[Entity, View("v_UpcomingAppointments")]
public interface IUpcomingAppointment {
  [PrimaryKey] Guid AppointmentId { get; set; }
  [TenantId] short TenantId { get; set; }
  [Size(100)] string CompanyName { get; set; }
  [Size(100)] string ShopName { get; set; }
  [Utc] DateTime StartsOn { get; set; }
  [Utc] DateTime EndsOn { get; set; }
  AppointmentStatus Status { get; set; }
  [Size(121)] string CustomerName { get; set; }
  [Size(121)] string StylistName { get; set; }
  [Column(Precision = 12, Scale = 2)] decimal BookedValue { get; set; }
}

/// <summary>What each stylist brought in and what they are owed for it: the join a payroll screen would otherwise
/// repeat.</summary>
[Entity, View("v_StylistRevenue")]
public interface IStylistRevenue {
  [PrimaryKey] Guid EmployeeId { get; set; }
  [TenantId] short TenantId { get; set; }
  [Size(121)] string StylistName { get; set; }
  /// <summary>Null for a member of staff with no home shop.</summary>
  [Size(100)] string? HomeShopName { get; set; }
  int AppointmentCount { get; set; }
  [Column(Precision = 12, Scale = 2)] decimal RevenueAmount { get; set; }
  [Column(Precision = 12, Scale = 2)] decimal CommissionAmount { get; set; }
}

/// <summary>Stock at or below its reorder level.</summary>
/// <remarks>MATERIALIZED: the server stores the rows, and <c>RefreshMaterializedViewAsync</c> recomputes them. The
/// setup refreshes it after the seed, a cross-tenant operation as the refresh of one object for every tenant is.
/// </remarks>
[Entity, View("mv_LowStock", Materialized = true)]
public interface ILowStock {
  [PrimaryKey] Guid Id { get; set; }
  [TenantId] short TenantId { get; set; }
  [Size(100)] string ShopName { get; set; }
  [Size(40)] string Sku { get; set; }
  [Size(120)] string ProductName { get; set; }
  int QuantityOnHand { get; set; }
  int ReorderLevel { get; set; }
}

/// <summary>One line of a shop's day: the rows of the table function <c>fn_ShopDaySchedule(shop, day)</c>.</summary>
/// <remarks>No primary key, so the rows are not tracked, and no tenant: the function takes the shop, which is one
/// tenant's. The function's body is SQL, PostgreSQL's, in the module.</remarks>
[Entity]
public interface IShopDaySlot {
  [Utc] DateTime StartsOn { get; set; }
  [Utc] DateTime EndsOn { get; set; }
  [Size(121)] string StylistName { get; set; }
  [Size(121)] string CustomerName { get; set; }
}
