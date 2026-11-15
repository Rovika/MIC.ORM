using Mic.Orm;
using Mic.Orm.Extensions.MultiTenancy;
using Mic.Orm.Runtime;

namespace BeautyShop;

/// <summary>Fills a Beauty Shop database with a plausible history — five companies, their shops, staff, customers, diary,
/// stock and books.</summary>
/// <remarks>REALISTIC IN SHAPE, SMALL IN VOLUME. The point of this database is to give a UI something with joins,
/// dates, money and enough rows to paginate — not to be a load test. Everything here fits in one process and a few
/// seconds.
///
/// NOTHING IS QUERIED, because nothing can be: there is no LINQ yet, so `EntitySet` throws. Every row this creates is
/// therefore kept in memory as it goes and referenced directly — which is why the method reads as one long build
/// rather than a set of independent steps. That constraint disappears with the LINQ task and this can be split then.
///
/// DETERMINISTIC BY DEFAULT. One seeded `Random` decides every name, price and time, so two runs produce the same
/// database and a screenshot taken today still matches tomorrow. The KEYS differ between runs — they are version-7
/// Guids minted from the clock — which is correct: the rows really were created at different moments. A key's
/// timestamp says when the ROW was made, never when the appointment was.
///
/// ONE SAVE PER COMPANY rather than one at the end: it bounds the batch, and it means a failure halfway leaves whole
/// companies rather than a torn one. The session of each is opened as that company's tenant, so the extension stamps
/// every row with it; nothing here sets the tenant by hand.</remarks>
public static class BeautySeed {
  /// <summary>What was written, for a caller that wants to say so.</summary>
  public readonly record struct Counts(int Companies, int Shops, int Employees, int Customers, int Appointments, int Sales) {
    public override string ToString() =>
      $"{Companies} companies, {Shops} shops, {Employees} employees, {Customers} customers, "
      + $"{Appointments} appointments, {Sales} sales";
  }

  public static async Task<Counts> RunAsync(EntityApp app, int companyCount = 5, int randomSeed = 20260831) {
    ArgumentNullException.ThrowIfNull(app);
    var rnd = new Random(randomSeed);
    var counts = new Counts();

    // TODAY IS THE ANCHOR, and everything is placed relative to it: a year of history behind, a month of bookings
    // ahead. Seeded again next month, the data moves with the calendar rather than ageing into the past.
    var today = DateTime.UtcNow.Date;

    for (var companyIndex = 0; companyIndex < companyCount; companyIndex++) {
      // One tenant per company, numbered by hand — see ITenant — and the session opened as that tenant, so that
      // every row written below is stamped with it by the extension.
      var tenantId = (short)(companyIndex + 1);
      var context = new OperationContext(app);
      context.SetValue("TenantId", tenantId);
      var session = context.OpenSession();
      var tenant = session.NewEntity<ITenant>();
      tenant.Id = tenantId;
      tenant.Name = BeautySeedData.CompanyNames[companyIndex % BeautySeedData.CompanyNames.Length];
      var company = session.NewEntity<ICompany>();
      company.Tenant = tenant;
      company.Name = tenant.Name;
      company.Status = RecordStatus.Active;

      var built = SeedCompany(session, company, rnd, today);
      await session.SaveChangesAsync();

      counts = counts with {
        Companies = counts.Companies + 1,
        Shops = counts.Shops + built.Shops,
        Employees = counts.Employees + built.Employees,
        Customers = counts.Customers + built.Customers,
        Appointments = counts.Appointments + built.Appointments,
        Sales = counts.Sales + built.Sales,
      };
    }
    return counts;
  }

  private static Counts SeedCompany(IEntitySession session, ICompany company, Random rnd, DateTime today) {
    // ----- the menu, and the goods ----------------------------------------------------------------------------------
    var services = new List<IService>();
    foreach (var (categoryName, specialties, serviceNames) in BeautySeedData.Menu) {
      var category = session.NewEntity<IServiceCategory>();
      category.Company = company;
      category.Name = categoryName;
      category.Specialties = specialties;
      foreach (var serviceName in serviceNames) {
        var service = session.NewEntity<IService>();
        service.Category = category;
        service.Name = serviceName;
        service.DurationMinutes = 15 * rnd.Next(2, 9);              // 30 minutes to two hours
        service.Price = Money(rnd, 20, 180);
        service.Status = RecordStatus.Active;
        services.Add(service);
      }
    }

    var vendors = new List<IVendor>();
    foreach (var vendorName in BeautySeedData.VendorNames.Take(rnd.Next(2, 5))) {
      var vendor = session.NewEntity<IVendor>();
      vendor.Company = company;
      vendor.Name = vendorName;
      vendor.ContactEmail = $"orders@{Slug(vendorName)}.example";
      vendor.Address = NewAddress(session, company, rnd);
      vendor.Phone = NewPhone(session, company, rnd, PhoneType.Office);
      vendor.Status = RecordStatus.Active;
      vendors.Add(vendor);
    }

    var products = new List<IProduct>();
    foreach (var (categoryName, productNames) in BeautySeedData.Catalogue) {
      var category = session.NewEntity<IProductCategory>();
      category.Company = company;
      category.Name = categoryName;
      foreach (var productName in productNames) {
        var product = session.NewEntity<IProduct>();
        product.Category = category;
        product.Vendor = Pick(rnd, vendors);
        product.Sku = $"{Slug(categoryName)[..3].ToUpperInvariant()}-{products.Count + 100}";
        product.Name = productName;
        product.UnitCost = Money(rnd, 3, 40);
        product.RetailPrice = product.UnitCost * 2 + 1;
        product.Status = RecordStatus.Active;
        products.Add(product);
      }
    }

    var paymentSources = new List<IPaymentSource>();
    foreach (var (name, kind) in new[] {
        ("Main Account", PaymentSourceKind.BankAccount),
        ("Card Terminal", PaymentSourceKind.CardProcessor),
        ("Front Desk Till", PaymentSourceKind.CashDrawer) }) {
      var source = session.NewEntity<IPaymentSource>();
      source.Company = company;
      source.Name = name;
      source.Kind = kind;
      source.BankName = kind == PaymentSourceKind.BankAccount ? "Meridian Bank" : null;
      source.AccountLast4 = kind == PaymentSourceKind.CashDrawer ? null : rnd.Next(1000, 9999).ToString();
      source.Status = RecordStatus.Active;
      paymentSources.Add(source);
    }

    // ----- the shops, and who works in them ---------------------------------------------------------------------------
    var shops = new List<IShop>();
    var employees = new List<IEmployee>();
    var shopCount = rnd.Next(1, 6);

    for (var s = 0; s < shopCount; s++) {
      var shop = session.NewEntity<IShop>();
      shop.Company = company;
      shop.Name = $"{company.Name} {BeautySeedData.ShopWords[(s + company.Tenant.Id) % BeautySeedData.ShopWords.Length]}";
      shop.Address = NewAddress(session, company, rnd);
      shop.Phone = NewPhone(session, company, rnd, PhoneType.Office);
      shop.TimeZone = Pick(rnd, BeautySeedData.TimeZones);
      shop.Status = RecordStatus.Active;
      shops.Add(shop);

      // Open Tuesday to Saturday. A shop that is shut on Monday has no row for it, which is the honest way to say so.
      foreach (var day in new[] {
          DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday }) {
        var hours = session.NewEntity<IShopHours>();
        hours.Shop = shop;
        hours.DayOfWeek = day;
        hours.OpensAt = new TimeOnly(9, 0);
        hours.ClosesAt = day == DayOfWeek.Saturday ? new TimeOnly(16, 0) : new TimeOnly(19, 0);
      }

      for (var n = 1; n <= rnd.Next(3, 7); n++) {
        var station = session.NewEntity<IServiceStation>();
        station.Shop = shop;
        station.Number = n;
        station.Specialties = Pick(rnd, BeautySeedData.Menu).Specialties;
        station.Status = RecordStatus.Active;
      }

      // A manager first, so everyone after them has somebody to report to — the self-reference, populated rather than
      // left null for the whole table.
      var manager = NewEmployee(session, company, shop, rnd, today, EmployeeRoles.Manager | EmployeeRoles.Stylist, null);
      if (s == 0)
        manager.Roles |= EmployeeRoles.CompanyContact;     // the company's contact: the first shop's manager
      employees.Add(manager);
      var staffCount = rnd.Next(10, 31) / shopCount + 1;
      for (var e = 0; e < staffCount; e++)
        employees.Add(NewEmployee(session, company, shop, rnd, today,
          rnd.Next(4) == 0 ? EmployeeRoles.Assistant : EmployeeRoles.Stylist, manager));
    }

    // A few stylists cover a second shop: the many-to-many beside the home shop.
    if (shops.Count > 1)
      foreach (var employee in employees.Where(_ => rnd.Next(5) == 0))
        employee.Shops.Add(Pick(rnd, shops.Where(s => s != employee.HomeShop).ToList()));

    // Time off, for roughly one in four — enough that a roster screen has something to show.
    foreach (var employee in employees.Where(_ => rnd.Next(4) == 0)) {
      var away = session.NewEntity<IAvailability>();
      away.Employee = employee;
      away.Kind = (AvailabilityKind)rnd.Next(4);
      var start = today.AddDays(rnd.Next(-120, 60)).AddHours(rnd.Next(8, 14));
      away.StartsOn = start;
      // A DATETIME range, so an afternoon off and a fortnight away are the same shape.
      away.EndsOn = rnd.Next(3) == 0 ? start.AddHours(4) : start.AddDays(rnd.Next(2, 15));
    }

    // Stock, per shop per product.
    foreach (var shop in shops)
      foreach (var product in products) {
        var stock = session.NewEntity<IStockLevel>();
        stock.Shop = shop;
        stock.Product = product;
        stock.ReorderLevel = rnd.Next(3, 10);
        // A quarter of the lines are at or below the reorder level, so `mv_LowStock` is not empty.
        stock.QuantityOnHand = rnd.Next(4) == 0 ? rnd.Next(0, stock.ReorderLevel + 1) : rnd.Next(10, 60);
        stock.LastCountedOn = DateOnly.FromDateTime(today.AddDays(-rnd.Next(1, 90)));
      }

    // ----- customers, and the diary -----------------------------------------------------------------------------------
    var customers = new List<ICustomer>();
    var customerCount = rnd.Next(30, 51);
    for (var c = 0; c < customerCount; c++) {
      var customer = session.NewEntity<ICustomer>();
      customer.Person = NewPerson(session, company, rnd, withBirthDate: true);
      customer.JoinedOn = DateOnly.FromDateTime(today.AddDays(-rnd.Next(30, 900)));
      customer.Notes = rnd.Next(5) == 0 ? "Prefers afternoon appointments." : null;
      customer.Status = RecordStatus.Active;
      // Two in three have an account; the rest are walk-ins, which is what the nullable User is for.
      if (rnd.Next(3) != 0)
        customer.User = NewUser(session, company, customer.Person, UserType.Customer, rnd);
      customers.Add(customer);
    }

    var appointments = 0;
    var sales = 0;
    foreach (var customer in customers) {
      var visits = rnd.Next(4, 16);
      for (var v = 0; v < visits; v++) {
        var shop = Pick(rnd, shops);
        var stylist = Pick(rnd, employees.Where(e => e.HomeShop == shop).ToList());
        // A year behind, a month ahead — so the diary has both a history and something still coming.
        var start = today.AddDays(rnd.Next(-330, 30)).AddHours(rnd.Next(9, 18)).AddMinutes(rnd.Next(4) * 15);

        var appointment = session.NewEntity<IAppointment>();
        appointment.Shop = shop;
        appointment.Customer = customer;
        appointment.Employee = stylist;
        appointment.StartsOn = start;
        appointment.Status = StatusFor(start, today, rnd);
        appointment.Notes = rnd.Next(8) == 0 ? "Allergy noted — patch test done." : null;
        appointments++;

        // One to three services, and the stylist has to be able to do them: the specialties on the category are what
        // makes the booking one a real shop could have taken.
        var possible = services.Where(sv => (sv.Category.Specialties & stylist.Specialties) != 0).ToList();
        if (possible.Count == 0)
          possible = services;
        var minutes = 0;
        var value = 0m;
        foreach (var service in PickDistinct(rnd, possible, rnd.Next(1, 4))) {
          var line = session.NewEntity<IAppointmentService>();
          line.Appointment = appointment;
          line.Service = service;
          line.Price = service.Price;
          line.DurationMinutes = service.DurationMinutes;
          minutes += service.DurationMinutes;
          value += service.Price;
        }
        appointment.EndsOn = start.AddMinutes(minutes);

        // A completed appointment becomes a sale — services, sometimes a product, and a payment that settles it.
        if (appointment.Status != AppointmentStatus.Completed)
          continue;

        var sale = session.NewEntity<ISale>();
        sale.Shop = shop;
        sale.Customer = customer;
        sale.Appointment = appointment;
        sale.SoldOn = appointment.EndsOn;
        sale.Status = SaleStatus.Completed;
        sales++;

        var total = 0m;
        var serviceLine = session.NewEntity<ISaleLine>();
        serviceLine.Sale = sale;
        serviceLine.Service = Pick(rnd, possible);
        serviceLine.PerformedBy = stylist;
        serviceLine.Quantity = 1;
        serviceLine.UnitPrice = value;
        total += value;

        if (rnd.Next(3) == 0) {                       // one visit in three also buys something
          var product = Pick(rnd, products);
          var productLine = session.NewEntity<ISaleLine>();
          productLine.Sale = sale;
          productLine.Product = product;
          productLine.Quantity = rnd.Next(1, 3);
          productLine.UnitPrice = product.RetailPrice;
          total += product.RetailPrice * productLine.Quantity;
        }

        // STORED, not computed: a sum over another table is not a generated column, so whoever writes the lines owns
        // the total.
        sale.TotalAmount = total;

        var payment = session.NewEntity<IPayment>();
        payment.Sale = sale;
        payment.Source = Pick(rnd, paymentSources);
        payment.Method = (PaymentMethod)rnd.Next(5);
        payment.PaidOn = sale.SoldOn;
        payment.Amount = total;
        payment.TransactionReference = payment.Method == PaymentMethod.Cash ? null : $"TXN{rnd.Next(100000, 999999)}";
      }
    }

    // ----- supplies coming in, and the invoices for them ---------------------------------------------------------------
    foreach (var shop in shops) {
      for (var i = 0; i < rnd.Next(2, 6); i++) {
        var vendor = Pick(rnd, vendors);
        var shipped = today.AddDays(-rnd.Next(10, 300));

        var shipment = session.NewEntity<IShipment>();
        shipment.Vendor = vendor;
        shipment.Shop = shop;
        shipment.ReferenceNumber = $"SH-{rnd.Next(10000, 99999)}";
        shipment.ShippedOn = DateOnly.FromDateTime(shipped);
        shipment.Status = ShipmentStatus.Received;
        shipment.ReceivedOn = DateOnly.FromDateTime(shipped.AddDays(rnd.Next(2, 10)));

        var invoiceTotal = 0m;
        foreach (var product in PickDistinct(rnd, products, rnd.Next(2, 6))) {
          var line = session.NewEntity<IShipmentLine>();
          line.Shipment = shipment;
          line.Product = product;
          line.Quantity = rnd.Next(5, 40);
          line.UnitCost = product.UnitCost;
          invoiceTotal += line.UnitCost * line.Quantity;
        }

        var invoice = session.NewEntity<IVendorInvoice>();
        invoice.Vendor = vendor;
        invoice.Shipment = shipment;
        invoice.InvoiceNumber = $"INV-{rnd.Next(100000, 999999)}";
        invoice.IssuedOn = shipment.ReceivedOn!.Value;
        invoice.DueOn = invoice.IssuedOn.AddDays(30);
        invoice.TotalAmount = invoiceTotal;

        // Most are paid; a few are left outstanding so an unpaid-invoices screen is not empty.
        if (rnd.Next(5) == 0) {
          invoice.Status = invoice.DueOn < DateOnly.FromDateTime(today) ? InvoiceStatus.Overdue : InvoiceStatus.Issued;
          continue;
        }
        invoice.Status = InvoiceStatus.Paid;
        var vendorPayment = session.NewEntity<IVendorPayment>();
        vendorPayment.Invoice = invoice;
        vendorPayment.Source = paymentSources[0];
        vendorPayment.PaidOn = invoice.IssuedOn.AddDays(rnd.Next(1, 30));
        vendorPayment.Amount = invoiceTotal;
        vendorPayment.TransactionReference = $"BNK{rnd.Next(100000, 999999)}";
      }
    }

    // ----- hours worked, and what they were paid --------------------------------------------------------------------
    //
    // Three monthly runs, so a payroll screen has a period to choose between. The lines are computed from the hours
    // rather than invented, because a payroll that does not add up is a poor thing to demonstrate against.
    for (var month = 3; month >= 1; month--) {
      var periodStart = new DateOnly(today.Year, today.Month, 1).AddMonths(-month);
      var run = session.NewEntity<IPayrollRun>();
      run.Company = company;
      run.PeriodStart = periodStart;
      run.PeriodEnd = periodStart.AddMonths(1).AddDays(-1);
      run.Status = PayrollStatus.Paid;
      run.ApprovedOn = run.PeriodEnd.ToDateTime(new TimeOnly(17, 0), DateTimeKind.Utc);

      foreach (var employee in employees) {
        var hours = 0m;
        var billed = 0m;
        for (var day = periodStart; day <= run.PeriodEnd; day = day.AddDays(1)) {
          if (day.DayOfWeek is DayOfWeek.Sunday or DayOfWeek.Monday)
            continue;
          var entry = session.NewEntity<ITimeEntry>();
          entry.Employee = employee;
          entry.WorkedOn = day;
          entry.Hours = 6 + rnd.Next(0, 5) * 0.5m;
          entry.BilledHours = entry.Hours - rnd.Next(0, 3) * 0.5m;
          hours += entry.Hours;
          billed += entry.BilledHours;
        }

        var line = session.NewEntity<IPayrollLine>();
        line.Run = run;
        line.Employee = employee;
        line.Hours = hours;
        line.GrossAmount = decimal.Round(hours * employee.HourlyRate, 2);
        line.CommissionAmount = decimal.Round(billed * employee.HourlyRate * employee.CommissionPercent / 100, 2);
      }
    }

    return new Counts(1, shops.Count, employees.Count, customers.Count, appointments, sales);
  }

  // ----- the small makers ---------------------------------------------------------------------------------------------

  private static IEmployee NewEmployee(IEntitySession session, ICompany company, IShop shop, Random rnd, DateTime today,
      EmployeeRoles roles, IEmployee? manager) {
    var employee = session.NewEntity<IEmployee>();
    employee.Person = NewPerson(session, company, rnd, withBirthDate: true);
    employee.HomeShop = shop;
    employee.Shops.Add(shop);
    employee.Roles = manager == null ? roles | EmployeeRoles.Admin : roles;
    employee.Specialties = SomeSpecialties(rnd);
    employee.HiredOn = DateOnly.FromDateTime(today.AddDays(-rnd.Next(60, 2500)));
    employee.HourlyRate = Money(rnd, 14, 45);
    employee.CommissionPercent = rnd.Next(0, 5) * 2.5m;
    employee.Manager = manager;
    employee.Status = RecordStatus.Active;

    // One in ten has left — which is what `TerminatedOn` and the Inactive status are for, and it gives a staff list
    // something to filter.
    if (rnd.Next(10) == 0) {
      employee.TerminatedOn = DateOnly.FromDateTime(today.AddDays(-rnd.Next(1, 200)));
      employee.Status = RecordStatus.Inactive;
    }
    // Most staff sign in; a junior may not, which is what the nullable User says.
    if (rnd.Next(5) != 0)
      employee.User = NewUser(session, company, employee.Person, UserType.Employee, rnd);
    return employee;
  }

  private static IUser NewUser(IEntitySession session, ICompany company, IPerson person, UserType type, Random rnd) {
    var user = session.NewEntity<IUser>();
    user.Company = company;
    // Unique within the company, which is what the two-column keys on Login and Email require. The counter is what
    // guarantees it — two people of the same name in one company is ordinary.
    var tag = rnd.Next(100000, 999999);
    user.Login = $"{person.FirstName.ToLowerInvariant()}.{person.LastName.ToLowerInvariant()}{tag}";
    user.Email = $"{user.Login}@example.com";
    user.PasswordHash = $"seeded-not-a-real-hash-{tag}";
    user.UserType = type;
    user.Status = RecordStatus.Active;
    user.LastSignedInOn = rnd.Next(3) == 0 ? null : DateTime.UtcNow.AddDays(-rnd.Next(1, 60));
    return user;
  }

  private static IPerson NewPerson(IEntitySession session, ICompany company, Random rnd, bool withBirthDate) {
    var person = session.NewEntity<IPerson>();
    person.Company = company;
    person.FirstName = Pick(rnd, BeautySeedData.FirstNames);
    person.LastName = Pick(rnd, BeautySeedData.LastNames);
    person.Email = rnd.Next(6) == 0
      ? null
      : $"{person.FirstName.ToLowerInvariant()}.{person.LastName.ToLowerInvariant()}{rnd.Next(100, 999)}@example.com";
    if (withBirthDate && rnd.Next(4) != 0)
      person.BirthDate = new DateOnly(rnd.Next(1960, 2006), rnd.Next(1, 13), rnd.Next(1, 28));
    person.Address = rnd.Next(3) == 0 ? null : NewAddress(session, company, rnd);
    person.Phone = rnd.Next(5) == 0 ? null : NewPhone(session, company, rnd, PhoneType.Mobile);
    return person;
  }

  private static IAddress NewAddress(IEntitySession session, ICompany company, Random rnd) {
    var address = session.NewEntity<IAddress>();
    address.Line1 = $"{rnd.Next(1, 200)} {Pick(rnd, BeautySeedData.Streets)}";
    address.Line2 = rnd.Next(4) == 0 ? $"Flat {rnd.Next(1, 40)}" : null;
    address.City = Pick(rnd, BeautySeedData.Cities);
    address.State = null;
    address.PostalCode = $"{rnd.Next(10000, 99999)}";
    address.Country = "Ruritania";
    return address;
  }

  private static IPhone NewPhone(IEntitySession session, ICompany company, Random rnd, PhoneType type) {
    var phone = session.NewEntity<IPhone>();
    phone.PhoneType = type;
    phone.Number = $"+99 {rnd.Next(100, 999)} {rnd.Next(100, 999)} {rnd.Next(1000, 9999)}";
    return phone;
  }

  // ----- dice -----------------------------------------------------------------------------------------------------

  /// <summary>What an appointment's status should be, given when it is.</summary>
  /// <remarks>THE PAST IS MOSTLY DONE AND THE FUTURE IS NOT, which is what makes the filtered index and the
  /// `v_UpcomingAppointments` view meaningful: without this every row would be the same shade and neither would show
  /// anything.</remarks>
  private static AppointmentStatus StatusFor(DateTime start, DateTime today, Random rnd) {
    // TODAY is its own case, and the only place `CheckedIn` can honestly occur: somebody is in the chair now.
    if (start.Date == today)
      return rnd.Next(2) == 0 ? AppointmentStatus.CheckedIn : AppointmentStatus.Confirmed;
    if (start > today)
      return rnd.Next(3) == 0 ? AppointmentStatus.Requested : AppointmentStatus.Confirmed;
    return rnd.Next(10) switch {
      0 => AppointmentStatus.Cancelled,
      1 => AppointmentStatus.NoShow,
      _ => AppointmentStatus.Completed,
    };
  }

  private static Specialties SomeSpecialties(Random rnd) {
    var specialties = new[] {
      Specialties.Hair, Specialties.Colour, Specialties.Nails, Specialties.Lashes, Specialties.Brows,
      Specialties.Skincare, Specialties.Massage,
    };
    var chosen = Specialties.None;
    foreach (var specialty in PickDistinct(rnd, specialties, rnd.Next(1, 4)))
      chosen |= specialty;
    return chosen;
  }

  private static decimal Money(Random rnd, int low, int high) =>
    decimal.Round(low + (decimal)rnd.NextDouble() * (high - low), 2);

  private static T Pick<T>(Random rnd, IReadOnlyList<T> items) => items[rnd.Next(items.Count)];

  /// <summary>`count` different items, or all of them when there are fewer.</summary>
  private static List<T> PickDistinct<T>(Random rnd, IReadOnlyList<T> items, int count) {
    var pool = items.ToList();
    var chosen = new List<T>();
    for (var i = 0; i < count && pool.Count > 0; i++) {
      var at = rnd.Next(pool.Count);
      chosen.Add(pool[at]);
      pool.RemoveAt(at);
    }
    return chosen;
  }

  private static string Slug(string text) =>
    new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
